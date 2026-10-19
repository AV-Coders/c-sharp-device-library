using Moq;

namespace AVCoders.Core.Tests;

public class DeviceBaseTest
{
    private readonly TestDevice _device;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public DeviceBaseTest()
    {
        _device = new TestDevice("Test Device", _mockClient.Object);
    }

    private class TestDevice(string name, CommunicationClient client) : DeviceBase(name, client)
    {
        public int PowerOnCalls;
        public int PowerOffCalls;

        public void SetDesired(PowerState state) => DesiredPowerState = state;

        public void SetActual(PowerState state) => PowerState = state;

        public void InvokeProcessPowerState() => ProcessPowerState();

        public void SetCommunicationState(CommunicationState state) => CommunicationState = state;

        public override void PowerOn() => PowerOnCalls++;

        public override void PowerOff() => PowerOffCalls++;
    }

    private class TestClient() : CommunicationClient("Test", "10.0.0.1", 4999, CommandStringFormat.Ascii)
    {
        public override void Send(string message) { }

        public override void Send(byte[] bytes) { }

        public void Drop() => ConnectionState = ConnectionState.Disconnected;

        public void Disconnect()
        {
            DesiredConnectionState = ConnectionState.Disconnected;
            ConnectionState = ConnectionState.Disconnected;
        }
    }

    [Fact]
    public void RequestedDisconnect_SetsNotAttempted_AndResolvesTheCommunicationIssue()
    {
        var client = new TestClient();
        var device = new TestDevice("Test Device", client);
        device.SetCommunicationState(CommunicationState.Error);

        client.Disconnect();

        Assert.Equal(CommunicationState.NotAttempted, device.CommunicationState);
        Assert.Empty(device.GetOngoingIssues());
    }

    [Fact]
    public void RequestedDisconnect_LeavesOtherOngoingIssuesAlone()
    {
        var client = new TestClient();
        var device = new TestDevice("Test Device", client);
        device.SetDesired(PowerState.On);
        device.SetActual(PowerState.Off);
        device.InvokeProcessPowerState();

        client.Disconnect();

        Assert.Single(device.GetOngoingIssues());
    }

    [Fact]
    public void DroppedConnection_LeavesTheCommunicationStateAlone()
    {
        var client = new TestClient();
        var device = new TestDevice("Test Device", client);
        device.SetCommunicationState(CommunicationState.Error);

        client.Drop();

        Assert.Equal(CommunicationState.Error, device.CommunicationState);
        Assert.Single(device.GetOngoingIssues());
    }

    [Fact]
    public void DesiredPowerState_DefaultsToUnknown()
    {
        Assert.Equal(PowerState.Unknown, _device.DesiredPowerState);
    }

    [Fact]
    public void DesiredPowerState_RaisesHandlersAndEvent()
    {
        var handlerStates = new List<PowerState>();
        var eventStates = new List<PowerState>();
        _device.DesiredPowerStateHandlers += state => handlerStates.Add(state);
        _device.OnDesiredPowerStateChanged += state => eventStates.Add(state);

        _device.SetDesired(PowerState.On);

        Assert.Equal(PowerState.On, _device.DesiredPowerState);
        Assert.Equal([PowerState.On], handlerStates);
        Assert.Equal([PowerState.On], eventStates);
    }

    [Fact]
    public void DesiredPowerState_DoesNotRaiseWhenUnchanged()
    {
        var invocations = 0;
        _device.SetDesired(PowerState.On);
        _device.OnDesiredPowerStateChanged += _ => invocations++;

        _device.SetDesired(PowerState.On);

        Assert.Equal(0, invocations);
    }

    [Fact]
    public void ProcessPowerState_ForcesTheDesiredState()
    {
        _device.SetDesired(PowerState.On);
        _device.SetActual(PowerState.Off);

        _device.InvokeProcessPowerState();

        Assert.Equal(1, _device.PowerOnCalls);
        Assert.Equal(0, _device.PowerOffCalls);
    }

    [Fact]
    public void ProcessPowerState_DoesNothingWhenDesiredIsUnknown()
    {
        _device.SetActual(PowerState.On);

        _device.InvokeProcessPowerState();

        Assert.Equal(0, _device.PowerOnCalls);
        Assert.Equal(0, _device.PowerOffCalls);
    }
}
