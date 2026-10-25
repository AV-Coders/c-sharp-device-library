using AVCoders.Core;

namespace AVCoders.Motor;

public record BondActionResult(bool Succeeded, string Failure);

public class BondGroup : Motor
{
    private readonly string _deviceId;
    private readonly BondBridge _bridge;

    public BondGroup(string name, string groupId, RelayAction powerOnAction, int moveSeconds, BondBridge bridge) 
        : base(name, powerOnAction, moveSeconds, bridge.Comms)
    {
        _deviceId = groupId;
        _bridge = bridge;
    }

    public override void Raise()
    {
        _ = TrackAction(nameof(Raise), _bridge.OpenGroup(_deviceId));
    }

    public override void Lower()
    {
        _ = TrackAction(nameof(Lower), _bridge.CloseGroup(_deviceId));
    }

    public override void Stop()
    {
        _ = TrackAction(nameof(Stop), _bridge.StopGroup(_deviceId));
    }

    private async Task TrackAction(string command, Task<BondActionResult> action)
    {
        BondActionResult result;
        try
        {
            result = await action;
        }
        catch (Exception e)
        {
            using (PushProperties(command))
                LogException(e, $"{command} of Bond group {_deviceId} failed");
            CommunicationState = CommunicationState.Error;
            return;
        }

        if (result.Succeeded)
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }
        using (PushProperties(command))
            LogWarning("{Command} of Bond group {GroupId} failed: {Failure}", command, _deviceId, result.Failure);
        CommunicationState = CommunicationState.Error;
    }
}

public class BondDevice : Motor
{
    private readonly string _deviceId;
    private readonly BondBridge _bridge;

    public BondDevice(string name, string deviceId, RelayAction powerOnAction, int moveSeconds, BondBridge bridge) 
        : base(name, powerOnAction, moveSeconds, bridge.Comms)
    {
        _deviceId = deviceId;
        _bridge = bridge;
    }

    public override void Raise()
    {
        _ = TrackAction(nameof(Raise), _bridge.OpenDevice(_deviceId));
    }

    public override void Lower()
    {
        _ = TrackAction(nameof(Lower), _bridge.CloseDevice(_deviceId));
    }

    public override void Stop()
    {
        _ = TrackAction(nameof(Stop), _bridge.StopDevice(_deviceId));
    }

    private async Task TrackAction(string command, Task<BondActionResult> action)
    {
        BondActionResult result;
        try
        {
            result = await action;
        }
        catch (Exception e)
        {
            using (PushProperties(command))
                LogException(e, $"{command} of Bond device {_deviceId} failed");
            CommunicationState = CommunicationState.Error;
            return;
        }

        if (result.Succeeded)
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }
        using (PushProperties(command))
            LogWarning("{Command} of Bond device {DeviceId} failed: {Failure}", command, _deviceId, result.Failure);
        CommunicationState = CommunicationState.Error;
    }
}

public class BondBridge
{
    private static readonly BondActionResult Succeeded = new(true, string.Empty);
    private static readonly BondActionResult NoResponse = new(false, "no response");
    private readonly RestComms _comms;
    private readonly ThreadWorker _pollWorker;
    private readonly Uri _versionUri;
    private readonly AsyncLocal<TaskCompletionSource<BondActionResult>?> _sendingAction = new();

    public BondBridge(RestComms comms, string token)
    {
        _comms = comms;
        _comms.AddDefaultHeader("BOND-Token", token);
        _comms.HttpResponseHandlers += HandleResponse;
        
        _versionUri = new Uri("/v2/sys/version", UriKind.Relative);

        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(85), true);
        _pollWorker.Restart();
    }

    internal RestComms Comms => _comms;

    private void HandleResponse(HttpResponseMessage response)
    {
        _sendingAction.Value?.TrySetResult(response.IsSuccessStatusCode
            ? Succeeded
            : new BondActionResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"));
    }

    private async Task Poll(CancellationToken arg)
    {
        await _comms.Get(_versionUri);
    }

    private async Task<BondActionResult> ExecuteAction(string type, string id, string action)
    {
        var reply = new TaskCompletionSource<BondActionResult>();
        _sendingAction.Value = reply;
        await _comms.Put(new Uri($"/v2/{type}/{id}/actions/{action}", UriKind.Relative), "{}", "application/json");
        return reply.Task.IsCompleted ? reply.Task.Result : NoResponse;
    }

    public Task<BondActionResult> OpenDevice(string deviceId)
    {
        return ExecuteAction("devices", deviceId, "Open");
    }

    public Task<BondActionResult> CloseDevice(string deviceId)
    {
        return ExecuteAction("devices", deviceId, "Close");
    }

    public Task<BondActionResult> StopDevice(string deviceId)
    {
        return ExecuteAction("devices", deviceId, "Hold");
    }

    public Task<BondActionResult> OpenGroup(string groupId)
    {
        return ExecuteAction("groups", groupId, "Open");
    }

    public Task<BondActionResult> CloseGroup(string groupId)
    {
        return ExecuteAction("groups", groupId, "Close");
    }

    public Task<BondActionResult> StopGroup(string groupId)
    {
        return ExecuteAction("groups", groupId, "Hold");
    }
}