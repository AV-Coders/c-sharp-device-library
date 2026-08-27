using System.Diagnostics;
using AVCoders.CommunicationClients;
using AVCoders.Core;
using Lextm.SharpSnmpLib;
using Moq;

namespace AVCoders.Power.Tests;

public class TrippLitePduTest
{
    private const string DeviceCountOid = "1.3.6.1.4.1.850.1.1.1.1.0";
    private const string DeviceTypeOid = "1.3.6.1.4.1.850.1.1.1.2.1.3.1";
    private const string DeviceModelOid = "1.3.6.1.4.1.850.1.1.1.2.1.5.1";
    private const string FirmwareOid = "1.3.6.1.4.1.850.1.2.1.1.2.0";
    private const string SerialOid = "1.3.6.1.4.1.850.1.2.1.1.5.0";
    private const string AtsBranch = "1.3.6.1.4.1.850.1.1.3.4";
    private const string OutletCountOid = $"{AtsBranch}.1.2.1.4.1";
    private const string OutletNameColumn = $"{AtsBranch}.3.3.1.1.2.1";
    private const string OutletStateColumn = $"{AtsBranch}.3.3.1.1.4.1";
    private const string OutletControllableColumn = $"{AtsBranch}.3.3.1.1.5.1";
    private const string OutletCommandOidPrefix = $"{AtsBranch}.3.3.1.1.6.1.";
    private const string SourceAvailabilityOid = $"{AtsBranch}.3.1.1.1.12.1";
    private const string SourceInUseOid = $"{AtsBranch}.3.1.1.1.13.1";
    private const string InputVoltageColumn = $"{AtsBranch}.3.1.2.1.5.1";
    private const string OutputVoltageOid = $"{AtsBranch}.3.2.1.1.4.1.1";
    private const string OutputPowerOid = $"{AtsBranch}.2.1.1.9.1";
    private const string EnvirosenseBranch = "1.3.6.1.4.1.850.1.1.3.3";
    private const string SensorTypeOid = "1.3.6.1.4.1.850.1.1.1.2.1.3.2";
    private const string SensorModelOid = "1.3.6.1.4.1.850.1.1.1.2.1.5.2";
    private const string SensorNameOid = "1.3.6.1.4.1.850.1.1.1.2.1.6.2";
    private const string SensorTempSupportedOid = $"{EnvirosenseBranch}.1.2.1.1.2";
    private const string SensorHumiditySupportedOid = $"{EnvirosenseBranch}.1.2.1.2.2";
    private const string SensorTemperatureOid = $"{EnvirosenseBranch}.3.1.1.1.2";
    private const string SensorTemperatureAlarmOid = $"{EnvirosenseBranch}.3.1.1.3.2";
    private const string SensorHumidityOid = $"{EnvirosenseBranch}.3.2.1.1.2";
    private const string SensorHumidityAlarmOid = $"{EnvirosenseBranch}.3.2.1.2.2";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Mock<AvCodersSnmpV3Client> _mockClient =
        new("TestSnmp", "127.0.0.1", (ushort)161, "user", "authpass", "privpass");

    private static List<Variable> SnmpResult(ISnmpData data) =>
        [new Variable(new ObjectIdentifier("1.3.6.1"), data)];

    private static List<Variable> Column(string columnOid, Func<int, ISnmpData> data, int count, int firstIndex = 1) =>
        Enumerable.Range(firstIndex, count)
            .Select(i => new Variable(new ObjectIdentifier($"{columnOid}.{i}"), data(i)))
            .ToList();

    private void StubHealthyAtsPdu()
    {
        _mockClient.Setup(c => c.Get(DeviceCountOid)).Returns(SnmpResult(new Gauge32(1)));
        _mockClient.Setup(c => c.Get(FirmwareOid)).Returns(SnmpResult(new OctetString("20.2.1.942")));
        _mockClient.Setup(c => c.Get(SerialOid)).Returns(SnmpResult(new OctetString("3604TV01677D401633")));
        _mockClient.Setup(c => c.Get(DeviceTypeOid)).Returns(SnmpResult(new ObjectIdentifier(AtsBranch)));
        _mockClient.Setup(c => c.Get(DeviceModelOid)).Returns(SnmpResult(new OctetString("PDUMH15HVATNET")));
        _mockClient.Setup(c => c.Get(OutletCountOid)).Returns(SnmpResult(new Gauge32(10)));
        _mockClient.Setup(c => c.Walk(OutletNameColumn))
            .Returns(Column(OutletNameColumn, i => new OctetString($"Load {i}"), 10));
        _mockClient.Setup(c => c.Walk(OutletControllableColumn))
            .Returns(Column(OutletControllableColumn, i => new Integer32(i <= 8 ? 1 : 2), 10));
        _mockClient.Setup(c => c.Walk(OutletStateColumn))
            .Returns(Column(OutletStateColumn, _ => new Integer32(2), 10));
        _mockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(3)));
        _mockClient.Setup(c => c.Get(SourceInUseOid)).Returns(SnmpResult(new Integer32(0)));
        _mockClient.Setup(c => c.Walk(InputVoltageColumn)).Returns(
        [
            new Variable(new ObjectIdentifier($"{InputVoltageColumn}.1.1"), new Gauge32(2452)),
            new Variable(new ObjectIdentifier($"{InputVoltageColumn}.2.1"), new Gauge32(2440))
        ]);
        _mockClient.Setup(c => c.Get(OutputVoltageOid)).Returns(SnmpResult(new Gauge32(2452)));
        _mockClient.Setup(c => c.Get(OutputPowerOid)).Returns(SnmpResult(new Gauge32(120)));
        _mockClient.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(SnmpResult(new Integer32(0)));
    }

    private void StubTemperatureSensor()
    {
        _mockClient.Setup(c => c.Get(DeviceCountOid)).Returns(SnmpResult(new Gauge32(2)));
        _mockClient.Setup(c => c.Get(SensorTypeOid)).Returns(SnmpResult(new ObjectIdentifier(EnvirosenseBranch)));
        _mockClient.Setup(c => c.Get(SensorModelOid)).Returns(SnmpResult(new OctetString("E2MT")));
        _mockClient.Setup(c => c.Get(SensorNameOid)).Returns(SnmpResult(new OctetString("Sensor0333")));
        _mockClient.Setup(c => c.Get(SensorTempSupportedOid)).Returns(SnmpResult(new Integer32(1)));
        _mockClient.Setup(c => c.Get(SensorHumiditySupportedOid)).Returns(SnmpResult(new Integer32(2)));
        _mockClient.Setup(c => c.Get(SensorTemperatureOid)).Returns(SnmpResult(new Integer32(218)));
        _mockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(2)));
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

    private async Task<TrippLitePdu> CreateInitialisedPdu()
    {
        var pdu = new TrippLitePdu("Test PDU", _mockClient.Object, PollInterval);
        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Okay, "The PDU never initialised");
        return pdu;
    }

    [Fact]
    public async Task Initialise_DiscoversTheOutletsFromTheDevice()
    {
        StubHealthyAtsPdu();

        var pdu = await CreateInitialisedPdu();

        Assert.Equal(10, pdu.Outlets.Count);
        Assert.Equal("Load 1", pdu.Outlets[0].Name);
        Assert.Equal("Load 10", pdu.Outlets[9].Name);
        Assert.True(((TrippLiteOutlet)pdu.Outlets[7]).Controllable);
        Assert.False(((TrippLiteOutlet)pdu.Outlets[8]).Controllable);
        Assert.False(((TrippLiteOutlet)pdu.Outlets[9]).Controllable);
        Assert.Equal("PDUMH15HVATNET", pdu.Model);
        Assert.Equal("3604TV01677D401633", pdu.SerialNumber);
        Assert.Equal("20.2.1.942", pdu.FirmwareVersion);
    }

    [Fact]
    public async Task Initialise_WithAnUnansweredDeviceQuery_ReportsErrorAndRetries()
    {
        _mockClient.Setup(c => c.Get(It.IsAny<string>())).Returns([]);
        _mockClient.Setup(c => c.Walk(It.IsAny<string>())).Returns([]);

        var pdu = new TrippLitePdu("Test PDU", _mockClient.Object, PollInterval);

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed discovery was never reported");
        Assert.Empty(pdu.Outlets);

        StubHealthyAtsPdu();
        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Okay,
            "The PDU never recovered after discovery succeeded");
        Assert.Equal(10, pdu.Outlets.Count);
    }

    [Theory]
    [InlineData(1, PowerState.Off)]
    [InlineData(2, PowerState.On)]
    [InlineData(4, PowerState.Unknown)]
    public async Task Poll_MapsTheOutletState(int deviceState, PowerState expected)
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[0];
        await WaitUntilAsync(() => outlet.PowerState == PowerState.On, "The outlet never reported its initial state");
        _mockClient.Setup(c => c.Walk(OutletStateColumn))
            .Returns(Column(OutletStateColumn, _ => new Integer32(deviceState), 10));

        await WaitUntilAsync(() => outlet.PowerState == expected, "The outlet state never updated");
    }

    [Fact]
    public async Task Poll_ReadsTheAtsTelemetry()
    {
        StubHealthyAtsPdu();

        var pdu = await CreateInitialisedPdu();

        await WaitUntilAsync(() => pdu.InputFeeds.Count == 2, "The input feeds were never populated");
        Assert.Equal(TrippLiteInputSource.A, pdu.ActiveSource);
        Assert.Equal(new TrippLiteInputFeedStatus(TrippLiteInputSource.A, true, 245.2f), pdu.InputFeeds[0]);
        Assert.Equal(new TrippLiteInputFeedStatus(TrippLiteInputSource.B, true, 244.0f), pdu.InputFeeds[1]);
        Assert.Equal(245.2f, pdu.OutputVoltage);
        Assert.Equal(120, pdu.OutputPowerWatts);
    }

    [Fact]
    public async Task Poll_WithALostFeed_RaisesAndResolvesTheRedundancyIssue()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        _mockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(1)));

        await WaitUntilAsync(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("redundancy is lost")),
            "The redundancy issue was never raised");
        await WaitUntilAsync(() => pdu.InputFeeds.Count == 2 && !pdu.InputFeeds[1].Available,
            "Feed B was never reported as unavailable");

        _mockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(3)));
        await WaitUntilAsync(
            () => pdu.GetOngoingIssues().All(issue => !issue.Message.Contains("redundancy is lost")),
            "The redundancy issue was never resolved");
    }

    [Fact]
    public async Task Poll_ReadsTheTemperatureSensor()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();

        var pdu = await CreateInitialisedPdu();

        await WaitUntilAsync(() => pdu.SensorReadings.Count == 1, "The sensor reading was never populated");
        Assert.Equal(new TrippLiteSensorReading("Sensor0333", "E2MT", 21.8f, null, false), pdu.SensorReadings[0]);
        Assert.Equal(10, pdu.Outlets.Count);
    }

    [Fact]
    public async Task Poll_WithASensorAlarm_RaisesAndResolvesTheIssue()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        _mockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(1)));

        await WaitUntilAsync(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never raised");
        await WaitUntilAsync(() => pdu.SensorReadings.Count == 1 && pdu.SensorReadings[0].InAlarm,
            "The reading never reported the alarm");

        _mockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(2)));
        await WaitUntilAsync(
            () => pdu.GetOngoingIssues().All(issue => !issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never resolved");
    }

    [Fact]
    public async Task Poll_WithAnUnansweredSensorQuery_ReportsAnError()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        await WaitUntilAsync(() => pdu.SensorReadings.Count == 1, "The sensor reading was never populated");
        _mockClient.Setup(c => c.Get(SensorTemperatureOid)).Returns([]);

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed sensor poll was never reported");
    }

    [Fact]
    public async Task Poll_ReadsAHumiditySensor()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        _mockClient.Setup(c => c.Get(SensorHumiditySupportedOid)).Returns(SnmpResult(new Integer32(1)));
        _mockClient.Setup(c => c.Get(SensorHumidityOid)).Returns(SnmpResult(new Integer32(45)));
        _mockClient.Setup(c => c.Get(SensorHumidityAlarmOid)).Returns(SnmpResult(new Integer32(2)));

        var pdu = await CreateInitialisedPdu();

        await WaitUntilAsync(() => pdu.SensorReadings.Count == 1, "The sensor reading was never populated");
        Assert.Equal(new TrippLiteSensorReading("Sensor0333", "E2MT", 21.8f, 45, false), pdu.SensorReadings[0]);
    }

    [Fact]
    public async Task Poll_WithAnUnansweredAlarmQuery_DoesNotResolveTheAlarm()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        _mockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(1)));
        await WaitUntilAsync(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never raised");

        _mockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns([]);

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed alarm poll was never reported");
        Assert.Contains(pdu.GetOngoingIssues(), issue => issue.Message.Contains("Sensor0333"));
    }

    [Fact]
    public async Task Initialise_WithAnUnansweredSensorCapabilityQuery_ReportsErrorAndRetries()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        _mockClient.Setup(c => c.Get(SensorTempSupportedOid)).Returns([]);

        var pdu = new TrippLitePdu("Test PDU", _mockClient.Object, PollInterval);

        await WaitUntilAsync(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed sensor discovery was never reported");

        _mockClient.Setup(c => c.Get(SensorTempSupportedOid)).Returns(SnmpResult(new Integer32(1)));
        await WaitUntilAsync(() => pdu.SensorReadings.Count == 1,
            "The sensor was never discovered after the capability query recovered");
    }

    [Fact]
    public async Task Reinitialise_WithTheSensorRemoved_ResolvesItsAlarm()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        _mockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(1)));
        await WaitUntilAsync(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never raised");

        _mockClient.Setup(c => c.Get(DeviceCountOid)).Returns(SnmpResult(new Gauge32(1)));
        pdu.Reinitialise();

        await WaitUntilAsync(
            () => pdu.GetOngoingIssues().All(issue => !issue.Message.Contains("Sensor0333")),
            "The orphaned sensor alarm was never resolved");
        await WaitUntilAsync(() => pdu.SensorReadings.Count == 0, "The removed sensor's reading was never dropped");
    }

    [Fact]
    public async Task PowerOn_SendsTheCommandWithoutBlocking()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[0];
        outlet.OverridePowerState(PowerState.Off);

        var stopwatch = Stopwatch.StartNew();
        outlet.PowerOn();
        stopwatch.Stop();

        _mockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}1", 2), Times.Once);
        Assert.Equal(PowerState.On, outlet.PowerState);
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, "PowerOn blocked the calling thread");
    }

    [Fact]
    public async Task Reboot_SendsTheCycleCommand()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[1];

        outlet.Reboot();

        _mockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}2", 3), Times.Once);
    }

    [Fact]
    public async Task PowerOn_OnANonControllableOutlet_DoesNotSendACommand()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[8];

        outlet.PowerOn();

        _mockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}9", It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task PowerOff_OnThePdu_SkipsNonControllableOutlets()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();

        pdu.PowerOff();

        for (var outletNumber = 1; outletNumber <= 8; outletNumber++)
            _mockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}{outletNumber}", 1), Times.Once);
        _mockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}9", It.IsAny<int>()), Times.Never);
        _mockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}10", It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task PowerOn_WithAnUnacknowledgedCommand_DoesNotChangeTheState()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[0];
        outlet.OverridePowerState(PowerState.Off);
        _mockClient.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<int>())).Returns([]);

        outlet.PowerOn();

        Assert.Equal(PowerState.Off, outlet.PowerState);
    }
}
