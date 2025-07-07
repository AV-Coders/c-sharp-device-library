using AVCoders.Core;
using Serilog;

namespace WirelessPresenter;

public record ExtronSharelinkUser(string ConnectionId, string UserName, string StreamId, string Platform, 
    bool ConnectionApproved, string VideoPortId, string DeviceName);

public class ExtronSharelinkPro : DeviceBase
{
    public static readonly ushort DefaultPort = 22023;
    private readonly CommunicationClient _client;
    private readonly ThreadWorker _pollWorker;
    private readonly ThreadWorker _connectedUserNotifier;
    private const string EscapeHeader = "\x1b";
    public readonly List<ExtronSharelinkUser> ConnectedUsers = [];
    public IntHandler? ConnectedUsersHandlers;

    public ExtronSharelinkPro(CommunicationClient client, string name) : base(name)
    {
        _client = client;
        _connectedUserNotifier = new ThreadWorker(NotifyOfConnectedUsers, TimeSpan.FromSeconds(2), true);
        _client.ConnectionStateHandlers += HandleConnectionState;
        _client.ResponseHandlers += HandleResponse;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
    }

    private Task NotifyOfConnectedUsers(CancellationToken arg)
    {
        ConnectedUsersHandlers?.Invoke(ConnectedUsers.Count);
        _connectedUserNotifier.Restart();
        return Task.CompletedTask;
    }

    private void HandleResponse(string response)
    {
        if (response.StartsWith("SharQ"))
        {
            int stateIndex = int.Parse(response.Substring(5).TrimEnd());
            switch (stateIndex)
            {
                case 1:
                    PowerState = PowerState.Off;
                    return;
                default:
                    PowerState = PowerState.On;
                    return;
            }
        }

        if (response.StartsWith("UserChg"))
        {
            WrapAndSendCommand("KSHAR");
        }
        else if (response.StartsWith("SharK"))
        {
            ConnectedUsers.Clear();
            WrapAndSendCommand("L0SHAR");
        }
        else if (response.StartsWith("SharL") || (!response.Contains("Shar") && response.Contains("*")))
        {
            if (response.TrimEnd().Length == 5)
            {
                ConnectedUsers.Clear();
                _connectedUserNotifier.Restart();
                return;
            }
        
            string userInfo = response.StartsWith("SharL") ? response.Substring(5) : response;
            var parts = userInfo.Split('*');
            ConnectedUsers.Add(new ExtronSharelinkUser(parts[0], parts[1], parts[2], parts[3], 
                parts[4] switch
                {
                    "0" => false,
                    "1" => true,
                    _ => throw new Exception("Unknown connection approved value")
                },
                parts[10], parts[11].TrimEnd()));
            _connectedUserNotifier.Restart();
        }
    }

    private Task Poll(CancellationToken arg)
    {
        if(_client.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        
        WrapAndSendCommand("0TC");
        return Task.CompletedTask;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if(connectionState != ConnectionState.Connected)
            return;
        WrapAndSendCommand("3CV");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        WrapAndSendCommand("0ECHO");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        WrapAndSendCommand("QSHAR");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        WrapAndSendCommand("KSHAR");
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private void SendCommand(String command)
    {
        try
        {
            Log.Information("Sending {command}", command);
            _client.Send(command);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            Error(e.Message);
            CommunicationState = CommunicationState.Error;
        }
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public void DisconnectUser(ExtronSharelinkUser user)
    {
        WrapAndSendCommand($"D{user.ConnectionId}SHAR");
    }

    public void ApproveConnectionRequest(ExtronSharelinkUser user)
    {
        WrapAndSendCommand($"C{user.ConnectionId}*1SHAR");
    }

    public void DenyConnectionRequest(ExtronSharelinkUser user)
    {
        WrapAndSendCommand($"C{user.ConnectionId}*0SHAR");
    }

    public void ApproveShareRequest(ExtronSharelinkUser user)
    {
        WrapAndSendCommand($"R{user.ConnectionId}*1SHAR");
    }

    public void DenyShareRequest(ExtronSharelinkUser user)
    {
        WrapAndSendCommand($"R{user.ConnectionId}*0SHAR");
    }
}