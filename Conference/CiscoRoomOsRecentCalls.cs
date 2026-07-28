using System.Linq;
using AVCoders.Core;

namespace AVCoders.Conference;

public record RecentCall(string Number, string Name);

public class CiscoRoomOsRecentCalls
{
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

        if (response.Contains("CallbackNumber:"))
            _recentCalls[index] = _recentCalls[index] with { Number = response.Split('"')[1] };
        else if (response.Contains("DisplayName:"))
            _recentCalls[index] = _recentCalls[index] with { Name = response.Split('"')[1] };
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