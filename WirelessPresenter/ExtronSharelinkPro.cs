using AVCoders.Core;

namespace WirelessPresenter;

public record ExtronSharelinkUser(string ConnectionId, string UserName, string StreamId, string Platform, 
    bool ConnectionApproved, string VideoPortId, string DeviceName);

public class ExtronSharelinkPro : DeviceBase, IDisposable
{
    public static readonly ushort DefaultPort = 22023;
    public static readonly TimeSpan ConnectedUsersNotifyDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
    private readonly TimeProvider _timeProvider;
    private readonly CommunicationClient _client;
    private readonly ThreadWorker _pollWorker;
    private readonly ThreadWorker _connectedUserNotifier;
    private readonly object _spacedSendsLock = new();
    private Task _spacedSends = Task.CompletedTask;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private const string EscapeHeader = "\x1b";
    private const int UserFieldCount = 12;
    public readonly List<ExtronSharelinkUser> ConnectedUsers = [];
    public IntHandler? ConnectedUsersHandlers;

    public ExtronSharelinkPro(CommunicationClient client, string name)
        : this(client, name, null)
    {
    }

    internal ExtronSharelinkPro(CommunicationClient client, string name, TimeProvider? timeProvider)
        : base(name, client)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _client = client;
        _connectedUserNotifier = new ThreadWorker(NotifyOfConnectedUsers, ConnectedUsersNotifyDelay, true, _timeProvider);
        _client.ConnectionStateHandlers += HandleConnectionState;
        _client.ResponseHandlers += HandleResponse;
        _pollWorker = new ThreadWorker(Poll, PollInterval, true, _timeProvider);
        _pollWorker.Restart();
        HandleConnectionState(_client.ConnectionState);
    }

    private Task NotifyOfConnectedUsers(CancellationToken arg)
    {
        ConnectedUsersHandlers?.Invoke(ConnectedUsers.Count);
        _ = _connectedUserNotifier.Stop();
        return Task.CompletedTask;
    }

    private void HandleResponse(string response)
    {
        if (response.StartsWith("SharQ"))
        {
            if (!int.TryParse(response.Substring(5).TrimEnd(), out int stateIndex))
            {
                using (PushProperties())
                    LogWarning("Ignoring the share state reply {Response}, its state is not a number", response);
                return;
            }
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
            if (parts.Length < UserFieldCount || parts[4] is not ("0" or "1"))
            {
                using (PushProperties())
                    LogWarning("Ignoring the user reply {Response}, it needs {FieldCount} fields and an approval of 0 or 1",
                        response, UserFieldCount);
                return;
            }
            ConnectedUsers.Add(new ExtronSharelinkUser(parts[0], parts[1], parts[2], parts[3], parts[4] == "1",
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
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(_ => Initialise(), TaskScheduler.Default).Unwrap();
    }

    private async Task Initialise()
    {
        if (_disposeCancellation.IsCancellationRequested)
            return;
        try
        {
            WrapAndSendCommand("3CV");
            await Task.Delay(ConnectStepGap, _timeProvider, _disposeCancellation.Token);
            WrapAndSendCommand("0ECHO");
            await Task.Delay(ConnectStepGap, _timeProvider, _disposeCancellation.Token);
            WrapAndSendCommand("QSHAR");
            await Task.Delay(ConnectStepGap, _timeProvider, _disposeCancellation.Token);
            WrapAndSendCommand("KSHAR");
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            LogDebug("Stopped initialising, the Sharelink was disposed");
        }
        catch (Exception e)
        {
            LogException(e, "Initialising the Sharelink failed");
        }
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private void SendCommand(string command)
    {
        using (PushProperties("SendCommand"))
        {
            try
            {
                LogVerbose("Sending {command}", command);
                _client.Send(command);
                CommunicationState = CommunicationState.Okay;
            }
            catch (Exception e)
            {
                LogException(e);
                CommunicationState = CommunicationState.Error;
            }
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

    public void Dispose()
    {
        if (_disposeCancellation.IsCancellationRequested)
            return;
        _disposeCancellation.Cancel();
        _client.ResponseHandlers -= HandleResponse;
        _client.ConnectionStateHandlers -= HandleConnectionState;
        _client.ConnectionStateHandlers -= HandleRequestedDisconnect;
        Task.Run(_pollWorker.Stop).GetAwaiter().GetResult();
        Task.Run(_connectedUserNotifier.Stop).GetAwaiter().GetResult();
        LogBaseRegistry.Deregister(_pollWorker);
        LogBaseRegistry.Deregister(_connectedUserNotifier);
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }
}