using System.Linq;
using AVCoders.Core;
using Microsoft.Extensions.Logging;

namespace AVCoders.Conference;

public record RecentCall(string Number, string Name);

public class CiscoRoomOsRecentCalls
{
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static ILogger Logger => LogBase.LoggerFactory.CreateLogger<CiscoRoomOsRecentCalls>();
    private readonly CommunicationClient _client;
    private readonly int _limit;
    private readonly List<RecentCall> _recentCalls = [];
    private bool _inCallHistoryResult;
    public List<RecentCall> RecentCalls => [.._recentCalls];
    public StringListHandler? CallListUpdatedHandlers;
    public event Action<List<string>>? OnCallListUpdated;

    public CiscoRoomOsRecentCalls(CommunicationClient client, int limit = 30)
    {
        _limit = limit;
        _client = client;
        _client.ResponseHandlers += HandleResponse;
        if (_client.ConnectionState == ConnectionState.Connected)
            Reinitialise();
    }

    // The codec announces every new CLI session with *r Login successful - feedback
    // registrations don't survive across sessions, so that line is the re-register trigger.
    private void Reinitialise()
    {
        _client.Send("xFeedback Register Event/CallHistory/Updated\n");
        _client.Send($"xCommand CallHistory Get Limit:{_limit}\n");
    }

    private void HandleResponse(string response)
    {
        foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            ProcessResponse(line);
    }

    private void ProcessResponse(string response)
    {
        if (response.StartsWith("*r Login successful"))
        {
            Reinitialise();
            return;
        }

        if (response.StartsWith("*r CallHistoryGetResult (status=OK):"))
        {
            _recentCalls.Clear();
            _inCallHistoryResult = true;
            return;
        }

        if (response.StartsWith("*r CallHistoryGetResult Entry "))
            ProcessEntry(response);

        // End of a generic result set; only act if we are in the middle of CallHistory parsing
        if (response.StartsWith("** end") && _inCallHistoryResult)
            PublishCallList();

        if (response.StartsWith("*e CallHistory Updated"))
        {
            _client.Send($"xCommand CallHistory Get Limit:{_limit}\n");
        }
    }

    private void ProcessEntry(string response)
    {
        var parts = response.Split(' ');
        if (parts.Length < 4 || !int.TryParse(parts[3], out var index))
            return;

        while (_recentCalls.Count <= index)
        {
            _recentCalls.Add(new RecentCall(string.Empty, string.Empty));
        }

        var isNumber = response.Contains("CallbackNumber:");
        if (!isNumber && !response.Contains("DisplayName:"))
            return;

        if (!response.Contains('"'))
        {
            Logger.LogWarning("Ignoring an unquoted call history value: {Response}", response);
            return;
        }

        var value = response.Split('"')[1];
        _recentCalls[index] = isNumber
            ? _recentCalls[index] with { Number = value }
            : _recentCalls[index] with { Name = value };
    }

    private void PublishCallList()
    {
        _inCallHistoryResult = false;
        var numbers = _recentCalls
            .Where(rc => !string.IsNullOrEmpty(rc.Number))
            .Select(rc => rc.Number)
            .ToList();
        CallListUpdatedHandlers?.Invoke(numbers);
        OnCallListUpdated?.Invoke(numbers);
    }
}