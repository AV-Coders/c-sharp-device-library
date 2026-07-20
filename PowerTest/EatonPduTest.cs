using System.Diagnostics;
using AVCoders.CommunicationClients;
using AVCoders.Core;
using Lextm.SharpSnmpLib;
using Moq;

namespace AVCoders.Power.Tests;

public class EatonPduTest
{
    private const string AgentOid = ".1.3.6.1.4.1.850.1.2.1.1.1.0";
    private const string OutletNameOidPrefix = ".1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.2.1.";
    private const string PowerStateOidPrefix = ".1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.4.1.";
    private const string SetPowerOidPrefix = ".1.3.6.1.4.1.850.1.1.3.4.3.3.1.1.6.1.";

    private readonly Mock<AvCodersSnmpV3Client> _mockClient =
        new("TestSnmp", "127.0.0.1", (ushort)161, "user", "authpass", "privpass");

    private static List<Variable> SnmpResult(ISnmpData data) =>
        [new Variable(new ObjectIdentifier("1.3.6.1"), data)];

    private void StubHealthyPdu()
    {
        _mockClient.Setup(c => c.Get(AgentOid)).Returns(SnmpResult(new OctetString("EATON")));
        _mockClient.Setup(c => c.Get(It.Is<string>(oid => oid.StartsWith(OutletNameOidPrefix))))
            .Returns((string oid) => SnmpResult(new OctetString($"Outlet {oid.Substring(OutletNameOidPrefix.Length)}")));
        _mockClient.Setup(c => c.Get(It.Is<string>(oid => oid.StartsWith(PowerStateOidPrefix))))
            .Returns(SnmpResult(new Integer32(2)));
        _mockClient.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<int>())).Returns([]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage);
            await Task.Delay(25);
        }
    }

    private async Task<EatonPdu> CreateInitialisedPdu()
    {
        var pdu = new EatonPdu("Test PDU", _mockClient.Object);
        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Okay, "The PDU never initialised");
        return pdu;
    }

    [Fact]
    public async Task Initialise_CreatesAllEightOutlets()
    {
        StubHealthyPdu();

        var pdu = await CreateInitialisedPdu();

        Assert.Equal(8, pdu.Outlets.Count);
        Assert.Equal("Outlet 1", pdu.Outlets[0].Name);
        Assert.Equal("Outlet 8", pdu.Outlets[7].Name);
    }

    [Fact]
    public async Task Initialise_WithAMissedOutletNameQuery_ReportsErrorInsteadOfThrowing()
    {
        _mockClient.Setup(c => c.Get(AgentOid)).Returns(SnmpResult(new OctetString("EATON")));
        _mockClient.Setup(c => c.Get(It.Is<string>(oid => oid.StartsWith(OutletNameOidPrefix))))
            .Returns([]);

        var pdu = new EatonPdu("Test PDU", _mockClient.Object);

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error,
            "The missed outlet name query was never reported");
        Assert.Empty(pdu.Outlets);
    }

    [Fact]
    public async Task PollPowerState_WithAnEmptyResponse_ReportsUnknownInsteadOfThrowing()
    {
        StubHealthyPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (EatonOutlet)pdu.Outlets[0];
        _mockClient.Setup(c => c.Get(It.Is<string>(oid => oid.StartsWith(PowerStateOidPrefix))))
            .Returns([]);

        outlet.PollPowerState();

        Assert.Equal(PowerState.Unknown, outlet.PowerState);
        Assert.Equal(CommunicationState.Error, pdu.CommunicationState);
    }

    [Theory]
    [InlineData(1, PowerState.Off)]
    [InlineData(2, PowerState.On)]
    [InlineData(4, PowerState.Unknown)]
    public async Task PollPowerState_MapsTheDeviceState(int deviceState, PowerState expected)
    {
        StubHealthyPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (EatonOutlet)pdu.Outlets[0];
        _mockClient.Setup(c => c.Get(It.Is<string>(oid => oid.StartsWith(PowerStateOidPrefix))))
            .Returns(SnmpResult(new Integer32(deviceState)));

        outlet.PollPowerState();

        Assert.Equal(expected, outlet.PowerState);
    }

    [Fact]
    public async Task PowerOn_SetsTheExpectedStateWithoutBlocking()
    {
        StubHealthyPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (EatonOutlet)pdu.Outlets[0];
        outlet.OverridePowerState(PowerState.Off);

        var stopwatch = Stopwatch.StartNew();
        outlet.PowerOn();
        stopwatch.Stop();

        _mockClient.Verify(c => c.Set($"{SetPowerOidPrefix}1", 2), Times.Once);
        Assert.Equal(PowerState.On, outlet.PowerState);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, "PowerOn blocked the calling thread");
    }

    [Fact]
    public async Task Reboot_CyclesTheOutletWithoutBlocking()
    {
        StubHealthyPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (EatonOutlet)pdu.Outlets[1];

        var stopwatch = Stopwatch.StartNew();
        outlet.Reboot();
        stopwatch.Stop();

        _mockClient.Verify(c => c.Set($"{SetPowerOidPrefix}2", 3), Times.Once);
        Assert.Equal(PowerState.Off, outlet.PowerState);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, "Reboot blocked the calling thread");
    }
}
