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

    public CiscoRoomOsRecentCalls(CommunicationClient client, int limit = 30)
    {
        _limit = limit;
        _client = client;
        _client.ResponseHandlers += HandleResponse;
        _client.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(_client.ConnectionState);
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        _client.Send("xFeedback Register Event/CallHistory/Updated\n");
        _client.Send($"xCommand CallHistory Get Limit:{_limit}\n");
    }

    private void HandleResponse(string response)
    {
        if (response.StartsWith("*r CallHistoryGetResult (status=OK):"))
        {
            _recentCalls.Clear();
            _inCallHistoryResult = true;
            return;
        }

        if (response.StartsWith("*r CallHistoryGetResult Entry "))
        {
            var parts = response.Split(' ');
            if (parts.Length >= 4 && int.TryParse(parts[3], out var index))
            {
                while (_recentCalls.Count <= index)
                {
                    _recentCalls.Add(new RecentCall(string.Empty, string.Empty));
                }

                if (response.Contains("CallbackNumber:"))
                {
                    var number = response.Split('"')[1];
                    var existing = _recentCalls[index];
                    _recentCalls[index] = existing with { Number = number };
                }
                else if (response.Contains("DisplayName:"))
                {
                    var name = response.Split('"')[1];
                    var existing = _recentCalls[index];
                    _recentCalls[index] = existing with { Name = name };
                }
            }
        }

        // End of a generic result set; only act if we are in the middle of CallHistory parsing
        if (response.StartsWith("** end") && _inCallHistoryResult)
        {
            _inCallHistoryResult = false;
            var numbers = _recentCalls
                .Where(rc => !string.IsNullOrEmpty(rc.Number))
                .Select(rc => rc.Number)
                .ToList();
            CallListUpdatedHandlers?.Invoke(numbers);
        }

        if (response.StartsWith("*e CallHistory Updated"))
        {
            _client.Send($"xCommand CallHistory Get Limit:{_limit}\n");
        }
            
            
    }
}