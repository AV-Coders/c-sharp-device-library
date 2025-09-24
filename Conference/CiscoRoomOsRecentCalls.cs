using System.Collections.Concurrent;
using AVCoders.Core;

namespace AVCoders.Conference;

public class CiscoRoomOsRecentCalls
{
    private readonly CommunicationClient _client;
    private readonly int _limit;
    private readonly List<string> _recentCalls = [];
    public List<string> RecentCalls => [.._recentCalls];
    public StringListHandler? CallListUpdatedHandlers;

    public CiscoRoomOsRecentCalls(CommunicationClient client, int limit = 30)
    {
        _limit = limit;
        _client = client;
        _client.ResponseHandlers += HandleResponse;
        _client.ConnectionStateHandlers += HandleConnectionState;
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
            return;
        }

        if (response.StartsWith("*r CallHistoryGetResult Entry ") && response.Contains("CallbackNumber:"))
        {
            _recentCalls.Add(response.Split('"')[1]);
            
            var parts = response.Split(' ');
            if (parts.Length >= 4 && int.TryParse(parts[3], out var index) && index == _limit - 1)
            {
                CallListUpdatedHandlers?.Invoke(RecentCalls);
            }

        }

        if (response.StartsWith("*e CallHistory Updated"))
        {
            _client.Send($"xCommand CallHistory Get Limit:{_limit}\n");
        }
            
            
    }
}