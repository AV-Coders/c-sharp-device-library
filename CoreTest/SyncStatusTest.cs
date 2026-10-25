namespace AVCoders.Core.Tests;

public class SyncStatusTest
{
    private class TestSyncStatus() : SyncStatus("Test", AVEndpointType.Encoder)
    {
        public void SetDeviceConnectionState(ConnectionState state) => DeviceConnectionState = state;
    }

    private readonly TestSyncStatus _status = new();

    [Fact]
    public void DeviceConnectionState_Change_RaisesTheHandlers()
    {
        var states = new List<ConnectionState>();
        _status.DeviceConnectionStateHandlers += states.Add;

        _status.SetDeviceConnectionState(ConnectionState.Connected);

        Assert.Equal(ConnectionState.Connected, _status.DeviceConnectionState);
        Assert.Equal([ConnectionState.Connected], states);
    }

    [Fact]
    public void DeviceConnectionState_Unchanged_DoesNotRaiseTheHandlers()
    {
        var states = new List<ConnectionState>();
        _status.SetDeviceConnectionState(ConnectionState.Connected);
        _status.DeviceConnectionStateHandlers += states.Add;

        _status.SetDeviceConnectionState(ConnectionState.Connected);

        Assert.Empty(states);
    }
}
