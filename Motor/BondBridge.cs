using AVCoders.Core;

namespace AVCoders.Motor;

public class BondGroup : Motor
{
    private readonly string _deviceId;
    private readonly BondBridge _bridge;

    public BondGroup(string name, string groupId, RelayAction powerOnAction, int moveSeconds, BondBridge bridge) : base(name, powerOnAction, moveSeconds)
    {
        _deviceId = groupId;
        _bridge = bridge;
    }

    public override void Raise()
    {
        _bridge.OpenGroup(_deviceId);
    }

    public override void Lower()
    {
        _bridge.CloseGroup(_deviceId);
    }

    public override void Stop()
    {
        _bridge.StopGroup(_deviceId);
    }
}

public class BondDevice : Motor
{
    private readonly string _deviceId;
    private readonly BondBridge _bridge;

    public BondDevice(string name, string deviceId, RelayAction powerOnAction, int moveSeconds, BondBridge bridge) : base(name, powerOnAction, moveSeconds)
    {
        _deviceId = deviceId;
        _bridge = bridge;
    }

    public override void Raise()
    {
        _bridge.OpenDevice(_deviceId);
    }

    public override void Lower()
    {
        _bridge.CloseDevice(_deviceId);
    }

    public override void Stop()
    {
        _bridge.StopDevice(_deviceId);
    }
}

public class BondBridge
{
    private readonly RestComms _comms;
    private readonly ThreadWorker _pollWorker;
    private readonly Uri _versionUri;

    public BondBridge(RestComms comms, string token)
    {
        _comms = comms;
        _comms.AddDefaultHeader("BOND-Token", token);
        
        _versionUri = new Uri("/v2/sys/version", UriKind.Relative);

        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(85), true);
        _pollWorker.Restart();
    }

    private async Task Poll(CancellationToken arg)
    {
        await _comms.Get(_versionUri);
    }

    private void ExecuteAction(string type, string id, string action)
    {
        _comms.Put(new Uri($"/v2/{type}/{id}/actions/{action}", UriKind.Relative), "{}", "application/json");
    }

    public void OpenDevice(string deviceId)
    {
        ExecuteAction("devices", deviceId, "Open");
    }

    public void CloseDevice(string deviceId)
    {
        ExecuteAction("devices", deviceId, "Close");
    }

    public void StopDevice(string deviceId)
    {
        ExecuteAction("devices", deviceId, "Hold");
    }

    public void OpenGroup(string groupId)
    {
        ExecuteAction("groups", groupId, "Open");
    }

    public void CloseGroup(string groupId)
    {
        ExecuteAction("groups", groupId, "Close");
    }

    public void StopGroup(string groupId)
    {
        ExecuteAction("groups", groupId, "Hold");
    }
}