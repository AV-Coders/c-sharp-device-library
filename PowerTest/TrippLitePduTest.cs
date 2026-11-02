using System.Diagnostics;
using AVCoders.CommunicationClients;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Power.Tests;

public abstract class TrippLitePduFixture
{
    protected const string DeviceCountOid = "1.3.6.1.4.1.850.1.1.1.1.0";
    protected const string DeviceTypeOid = "1.3.6.1.4.1.850.1.1.1.2.1.3.1";
    protected const string DeviceModelOid = "1.3.6.1.4.1.850.1.1.1.2.1.5.1";
    protected const string FirmwareOid = "1.3.6.1.4.1.850.1.2.1.1.2.0";
    protected const string SerialOid = "1.3.6.1.4.1.850.1.2.1.1.5.0";
    // The MAC and alarm OIDs come from the librenms TRIPPLITE-PRODUCTS MIB, not a capture.
    protected const string MacOid = "1.3.6.1.4.1.850.1.2.1.1.4.0";
    protected const string AlarmsPresentOid = "1.3.6.1.4.1.850.1.3.1.0";
    protected const string AlarmDetailColumn = "1.3.6.1.4.1.850.1.3.2.1.6";
    protected const string AtsBranch = "1.3.6.1.4.1.850.1.1.3.4";
    protected const string OutletCountOid = $"{AtsBranch}.1.2.1.4.1";
    protected const string OutletNameColumn = $"{AtsBranch}.3.3.1.1.2.1";
    protected const string OutletStateColumn = $"{AtsBranch}.3.3.1.1.4.1";
    protected const string OutletControllableColumn = $"{AtsBranch}.3.3.1.1.5.1";
    protected const string OutletCommandOidPrefix = $"{AtsBranch}.3.3.1.1.6.1.";
    protected const string SourceAvailabilityOid = $"{AtsBranch}.3.1.1.1.12.1";
    protected const string SourceInUseOid = $"{AtsBranch}.3.1.1.1.13.1";
    protected const string InputVoltageColumn = $"{AtsBranch}.3.1.2.1.5.1";
    protected const string OutputVoltageOid = $"{AtsBranch}.3.2.1.1.4.1.1";
    protected const string OutputPowerOid = $"{AtsBranch}.2.1.1.9.1";
    protected const string EnvirosenseBranch = "1.3.6.1.4.1.850.1.1.3.3";
    protected const string SensorTypeOid = "1.3.6.1.4.1.850.1.1.1.2.1.3.2";
    protected const string SensorModelOid = "1.3.6.1.4.1.850.1.1.1.2.1.5.2";
    protected const string SensorNameOid = "1.3.6.1.4.1.850.1.1.1.2.1.6.2";
    protected const string SensorTempSupportedOid = $"{EnvirosenseBranch}.1.2.1.1.2";
    protected const string SensorHumiditySupportedOid = $"{EnvirosenseBranch}.1.2.1.2.2";
    protected const string SensorTemperatureOid = $"{EnvirosenseBranch}.3.1.1.1.2";
    protected const string SensorTemperatureAlarmOid = $"{EnvirosenseBranch}.3.1.1.3.2";
    protected const string SensorHumidityOid = $"{EnvirosenseBranch}.3.2.1.1.2";
    protected const string SensorHumidityAlarmOid = $"{EnvirosenseBranch}.3.2.1.2.2";

    protected static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    protected const string Mac = "00:06:67:1A:5D:B7";

    protected readonly FakeTimeProvider Time = new();

    protected readonly Mock<AvCodersSnmpV3Client> MockClient =
        new("TestSnmp", "127.0.0.1", (ushort)161, "user", "authpass", "privpass");

    protected static readonly SnmpReply NoReply = new([]);

    protected static SnmpReply SnmpResult(ISnmpData data) =>
        new([new Variable(new ObjectIdentifier("1.3.6.1"), data)]);

    protected static SnmpReply Column(string columnOid, Func<int, ISnmpData> data, int count, int firstIndex = 1) =>
        new(Enumerable.Range(firstIndex, count)
            .Select(i => new Variable(new ObjectIdentifier($"{columnOid}.{i}"), data(i)))
            .ToList());

    protected void StubHealthyAtsPdu()
    {
        MockClient.Setup(c => c.Get(DeviceCountOid)).Returns(SnmpResult(new Gauge32(1)));
        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(SnmpResult(new OctetString("20.2.1.942")));
        MockClient.Setup(c => c.Get(SerialOid)).Returns(SnmpResult(new OctetString("2918QX05284K736051")));
        MockClient.Setup(c => c.Get(MacOid)).Returns(SnmpResult(new OctetString(Mac)));
        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(SnmpResult(new Gauge32(0)));
        MockClient.Setup(c => c.Get(DeviceTypeOid)).Returns(SnmpResult(new ObjectIdentifier(AtsBranch)));
        MockClient.Setup(c => c.Get(DeviceModelOid)).Returns(SnmpResult(new OctetString("PDUMH15HVATNET")));
        MockClient.Setup(c => c.Get(OutletCountOid)).Returns(SnmpResult(new Gauge32(10)));
        MockClient.Setup(c => c.Walk(OutletNameColumn))
            .Returns(Column(OutletNameColumn, i => new OctetString($"Load {i}"), 10));
        MockClient.Setup(c => c.Walk(OutletControllableColumn))
            .Returns(Column(OutletControllableColumn, i => new Integer32(i <= 8 ? 1 : 2), 10));
        MockClient.Setup(c => c.Walk(OutletStateColumn))
            .Returns(Column(OutletStateColumn, _ => new Integer32(2), 10));
        MockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(3)));
        MockClient.Setup(c => c.Get(SourceInUseOid)).Returns(SnmpResult(new Integer32(0)));
        MockClient.Setup(c => c.Walk(InputVoltageColumn)).Returns(new SnmpReply(
        [
            new Variable(new ObjectIdentifier($"{InputVoltageColumn}.1.1"), new Gauge32(2452)),
            new Variable(new ObjectIdentifier($"{InputVoltageColumn}.2.1"), new Gauge32(2440))
        ]));
        MockClient.Setup(c => c.Get(OutputVoltageOid)).Returns(SnmpResult(new Gauge32(2452)));
        MockClient.Setup(c => c.Get(OutputPowerOid)).Returns(SnmpResult(new Gauge32(120)));
        MockClient.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(SnmpResult(new Integer32(0)));
    }

    protected void StubTemperatureSensor()
    {
        MockClient.Setup(c => c.Get(DeviceCountOid)).Returns(SnmpResult(new Gauge32(2)));
        MockClient.Setup(c => c.Get(SensorTypeOid)).Returns(SnmpResult(new ObjectIdentifier(EnvirosenseBranch)));
        MockClient.Setup(c => c.Get(SensorModelOid)).Returns(SnmpResult(new OctetString("E2MT")));
        MockClient.Setup(c => c.Get(SensorNameOid)).Returns(SnmpResult(new OctetString("Sensor0333")));
        MockClient.Setup(c => c.Get(SensorTempSupportedOid)).Returns(SnmpResult(new Integer32(1)));
        MockClient.Setup(c => c.Get(SensorHumiditySupportedOid)).Returns(SnmpResult(new Integer32(2)));
        MockClient.Setup(c => c.Get(SensorTemperatureOid)).Returns(SnmpResult(new Integer32(218)));
        MockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(2)));
    }

    protected TrippLitePdu CreatePdu() => new("Test PDU", MockClient.Object, PollInterval, Time);

    protected Task PollUntil(Func<bool> condition, string failureMessage) =>
        Wait.UntilAsync(Time, PollInterval, condition, failureMessage);

    protected async Task<TrippLitePdu> CreateInitialisedPdu()
    {
        var pdu = CreatePdu();
        await PollUntil(() => pdu.CommunicationState == CommunicationState.Okay, "The PDU never initialised");
        return pdu;
    }

    protected int Gets(string oid) =>
        MockClient.Invocations.Count(invocation =>
            invocation.Method.Name == nameof(AvCodersSnmpV3Client.Get) && (string)invocation.Arguments[0] == oid);

    protected static DeviceDetail Detail(TrippLitePdu pdu, string label) => pdu.Details.Single(detail => detail.Label == label);
}

public class TrippLitePduTest : TrippLitePduFixture
{
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
        Assert.Equal("2918QX05284K736051", pdu.SerialNumber);
        Assert.Equal("20.2.1.942", pdu.FirmwareVersion);
    }

    [Fact]
    public async Task Initialise_WithAnUnansweredDeviceQuery_ReportsErrorAndRetries()
    {
        MockClient.Setup(c => c.Get(It.IsAny<string>())).Returns(NoReply);
        MockClient.Setup(c => c.Walk(It.IsAny<string>())).Returns(NoReply);

        var pdu = CreatePdu();

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed discovery was never reported");
        Assert.Empty(pdu.Outlets);

        StubHealthyAtsPdu();
        await PollUntil(() => pdu.CommunicationState == CommunicationState.Okay,
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
        await PollUntil(() => outlet.PowerState == PowerState.On, "The outlet never reported its initial state");
        MockClient.Setup(c => c.Walk(OutletStateColumn))
            .Returns(Column(OutletStateColumn, _ => new Integer32(deviceState), 10));

        await PollUntil(() => outlet.PowerState == expected, "The outlet state never updated");
    }

    [Fact]
    public async Task Poll_ReadsTheAtsTelemetry()
    {
        StubHealthyAtsPdu();

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.InputFeeds.Count == 2, "The input feeds were never populated");
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
        MockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(1)));

        await PollUntil(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("redundancy is lost")),
            "The redundancy issue was never raised");
        await PollUntil(() => pdu.InputFeeds.Count == 2 && !pdu.InputFeeds[1].Available,
            "Feed B was never reported as unavailable");

        MockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(3)));
        await PollUntil(
            () => pdu.GetOngoingIssues().All(issue => !issue.Message.Contains("redundancy is lost")),
            "The redundancy issue was never resolved");
    }

    [Theory]
    [InlineData(TrippLiteInputSource.A, 2, 1, "Input source B has failed")]
    [InlineData(TrippLiteInputSource.B, 1, 2, "Input source A has failed")]
    public async Task Poll_WithAnInputWarningDisabled_OnlyRaisesTheOtherInputsFailure(
        TrippLiteInputSource disabled, int disabledFailedAvailability, int otherFailedAvailability,
        string otherFailureMessage)
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        pdu.DisableInputWarning(disabled);
        MockClient.Setup(c => c.Get(SourceAvailabilityOid))
            .Returns(SnmpResult(new Integer32(disabledFailedAvailability)));

        await PollUntil(
            () => pdu.InputFeeds.Count == 2 && !pdu.InputFeeds.Single(feed => feed.Source == disabled).Available,
            "The failed feed was never reported as unavailable");
        Assert.DoesNotContain(pdu.GetOngoingIssues(), issue => issue.Message.Contains("redundancy is lost"));

        MockClient.Setup(c => c.Get(SourceAvailabilityOid))
            .Returns(SnmpResult(new Integer32(otherFailedAvailability)));
        await PollUntil(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains(otherFailureMessage)),
            "The other input's failure was never raised");
    }

    [Fact]
    public async Task DisableInputWarning_ResolvesAnExistingIssue_AndEnableRaisesItAgain()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(1)));
        await PollUntil(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("redundancy is lost")),
            "The redundancy issue was never raised");

        pdu.DisableInputWarning(TrippLiteInputSource.B);
        Assert.Contains(pdu.Details, d => d.Label == "Input B Warning" && d.Tone == DetailTone.Warning);
        await PollUntil(
            () => pdu.GetOngoingIssues().All(issue => !issue.Message.Contains("redundancy is lost")),
            "The redundancy issue was never resolved");

        pdu.EnableInputWarning(TrippLiteInputSource.B);
        Assert.DoesNotContain(pdu.Details, d => d.Label == "Input B Warning");
        await PollUntil(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("Input source B has failed")),
            "The redundancy issue was never raised again");
    }

    [Fact]
    public void InputWarning_WithAnUnknownInput_Throws()
    {
        MockClient.Setup(c => c.Get(It.IsAny<string>())).Returns(NoReply);
        MockClient.Setup(c => c.Walk(It.IsAny<string>())).Returns(NoReply);
        var pdu = CreatePdu();

        Assert.Throws<ArgumentOutOfRangeException>(() => pdu.DisableInputWarning(TrippLiteInputSource.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdu.EnableInputWarning(TrippLiteInputSource.Unknown));
    }

    [Fact]
    public async Task Poll_ReadsTheTemperatureSensor()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.SensorReadings.Count == 1, "The sensor reading was never populated");
        Assert.Equal(new TrippLiteSensorReading("Sensor0333", "E2MT", 21.8f, null, false), pdu.SensorReadings[0]);
        Assert.Equal(10, pdu.Outlets.Count);
    }

    [Fact]
    public async Task Poll_WithASensorAlarm_RaisesAndResolvesTheIssue()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(1)));

        await PollUntil(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never raised");
        await PollUntil(() => pdu.SensorReadings.Count == 1 && pdu.SensorReadings[0].InAlarm,
            "The reading never reported the alarm");

        MockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(2)));
        await PollUntil(
            () => pdu.GetOngoingIssues().All(issue => !issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never resolved");
    }

    [Fact]
    public async Task Poll_WithAnUnansweredSensorQuery_ReportsAnError()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        await PollUntil(() => pdu.SensorReadings.Count == 1, "The sensor reading was never populated");
        MockClient.Setup(c => c.Get(SensorTemperatureOid)).Returns(NoReply);

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed sensor poll was never reported");
    }

    [Fact]
    public async Task Poll_ReadsAHumiditySensor()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        MockClient.Setup(c => c.Get(SensorHumiditySupportedOid)).Returns(SnmpResult(new Integer32(1)));
        MockClient.Setup(c => c.Get(SensorHumidityOid)).Returns(SnmpResult(new Integer32(45)));
        MockClient.Setup(c => c.Get(SensorHumidityAlarmOid)).Returns(SnmpResult(new Integer32(2)));

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.SensorReadings.Count == 1, "The sensor reading was never populated");
        Assert.Equal(new TrippLiteSensorReading("Sensor0333", "E2MT", 21.8f, 45, false), pdu.SensorReadings[0]);
    }

    [Fact]
    public async Task Poll_WithAnUnansweredAlarmQuery_DoesNotResolveTheAlarm()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(1)));
        await PollUntil(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never raised");

        MockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(NoReply);

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed alarm poll was never reported");
        Assert.Contains(pdu.GetOngoingIssues(), issue => issue.Message.Contains("Sensor0333"));
    }

    [Fact]
    public async Task Initialise_WithAnUnansweredSensorCapabilityQuery_ReportsErrorAndRetries()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        MockClient.Setup(c => c.Get(SensorTempSupportedOid)).Returns(NoReply);

        var pdu = CreatePdu();

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed sensor discovery was never reported");

        MockClient.Setup(c => c.Get(SensorTempSupportedOid)).Returns(SnmpResult(new Integer32(1)));
        await PollUntil(() => pdu.SensorReadings.Count == 1,
            "The sensor was never discovered after the capability query recovered");
    }

    [Fact]
    public async Task Reinitialise_WithTheSensorRemoved_ResolvesItsAlarm()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(1)));
        await PollUntil(
            () => pdu.GetOngoingIssues().Any(issue => issue.Message.Contains("Sensor0333")),
            "The sensor alarm issue was never raised");

        MockClient.Setup(c => c.Get(DeviceCountOid)).Returns(SnmpResult(new Gauge32(1)));
        pdu.Reinitialise();

        await PollUntil(
            () => pdu.GetOngoingIssues().All(issue => !issue.Message.Contains("Sensor0333")),
            "The orphaned sensor alarm was never resolved");
        await PollUntil(() => pdu.SensorReadings.Count == 0, "The removed sensor's reading was never dropped");
    }

    [Fact]
    public async Task Reinitialise_ReplacesTheOutletsAndDeregistersTheOldOnes()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var oldOutlets = pdu.Outlets;

        pdu.Reinitialise();

        await PollUntil(() => pdu.Outlets != oldOutlets, "The outlets were never rediscovered");
        var registered = LogBaseRegistry.GetAll();
        Assert.Equal(10, oldOutlets.Count);
        Assert.All(oldOutlets, outlet => Assert.DoesNotContain<LogBase>(outlet, registered));
        Assert.All(pdu.Outlets, outlet => Assert.Contains<LogBase>(outlet, registered));
    }

    [Fact]
    public async Task Initialise_TreatsANoSuchObjectReplyAsMissing()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(SnmpResult(new NoSuchObject()));
        MockClient.Setup(c => c.Get(SerialOid)).Returns(SnmpResult(new NoSuchInstance()));
        MockClient.Setup(c => c.Get(DeviceModelOid)).Returns(SnmpResult(new EndOfMibView()));

        var pdu = await CreateInitialisedPdu();

        Assert.Equal(string.Empty, pdu.FirmwareVersion);
        Assert.Equal(string.Empty, pdu.SerialNumber);
        Assert.Equal("Unknown", pdu.Model);
    }

    [Fact]
    public async Task Initialise_ShowsTheIdentityAsDetails()
    {
        StubHealthyAtsPdu();

        var pdu = await CreateInitialisedPdu();

        Assert.Equal(new DeviceDetail("Model", "PDUMH15HVATNET"), Detail(pdu, "Model"));
        Assert.Equal(new DeviceDetail("Serial", "2918QX05284K736051"), Detail(pdu, "Serial"));
        Assert.Equal(new DeviceDetail("Firmware", "20.2.1.942"), Detail(pdu, "Firmware"));
        Assert.Equal(new DeviceDetail("MAC", "00:06:67:1A:5D:B7"), Detail(pdu, "MAC"));
        Assert.Empty(pdu.GetOngoingIssues());
    }

    [Fact]
    public async Task Initialise_WithAnUnsupportedFirmwareQuery_ShowsItIsNotSupportedWithNoIssue()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(SnmpResult(new NoSuchInstance()));

        var pdu = await CreateInitialisedPdu();

        Assert.Equal(new DeviceDetail("Firmware", "Not supported: NoSuchInstance", DetailTone.Normal), Detail(pdu, "Firmware"));
        Assert.Empty(pdu.GetIssues());
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);
        Assert.Equal(string.Empty, pdu.FirmwareVersion);
    }

    [Fact]
    public async Task Initialise_WithAnUnansweredFirmwareQuery_CarriesOnAndRaisesTheReadIssueOnlyOnTheThirdMiss()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(NoReply);

        var pdu = await CreateInitialisedPdu();
        Assert.Equal(10, pdu.Outlets.Count);
        Assert.Equal(new DeviceDetail("Firmware", "no answer", DetailTone.Warning), Detail(pdu, "Firmware"));

        await PollUntil(() => Gets(FirmwareOid) == 2, "The firmware query was never asked again");
        Assert.DoesNotContain(pdu.GetIssues(), issue => issue.Key == "firmware-read");
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);

        await PollUntil(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "firmware-read"),
            "The third unanswered firmware query never raised the read issue");
        Assert.Equal(3, Gets(FirmwareOid));
        var readIssue = pdu.GetOngoingIssues().Single(issue => issue.Key == "firmware-read");
        Assert.Equal(IssueSeverity.Minor, readIssue.Severity);
        Assert.Contains("no reply", readIssue.Message);

        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(SnmpResult(new OctetString("20.2.1.942")));
        await PollUntil(() => pdu.GetOngoingIssues().All(issue => issue.Key != "firmware-read"),
            "The answered firmware query never resolved the read issue");
        Assert.Equal(new DeviceDetail("Firmware", "20.2.1.942"), Detail(pdu, "Firmware"));
        Assert.Equal("20.2.1.942", pdu.FirmwareVersion);
    }

    [Fact]
    public async Task Poll_AsksForAnOwedIdentityReadAfterTheOutletAtsSensorAndAlarmPolls()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(NoReply);
        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => Gets(FirmwareOid) == 2, "The firmware query was never asked again");
        var queries = MockClient.Invocations.Select(invocation => (string)invocation.Arguments[0]).ToList();

        var beforeSecondFirmware = queries.GetRange(0, queries.LastIndexOf(FirmwareOid));
        var cycleStart = beforeSecondFirmware.LastIndexOf(OutletStateColumn);
        Assert.True(cycleStart > beforeSecondFirmware.IndexOf(OutletStateColumn),
            "The firmware query was asked again before the second outlet poll");
        string[] expectedCycle =
        [
            OutletStateColumn, SourceAvailabilityOid, InputVoltageColumn, SourceInUseOid, OutputVoltageOid,
            OutputPowerOid, SensorTemperatureOid, SensorTemperatureAlarmOid, AlarmsPresentOid
        ];
        Assert.Equal(expectedCycle, beforeSecondFirmware.Skip(cycleStart));
        Assert.Equal(10, pdu.Outlets.Count);
    }

    [Fact]
    public async Task Poll_WhileACorePollFails_SkipsTheAlarmAndIdentityReadsAndResumesThemOnTheFirstHealthyPoll()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(NoReply);
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Walk(OutletStateColumn)).Returns(NoReply);

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error, "The failed outlet poll was never reported");
        var alarmCounts = Gets(AlarmsPresentOid);
        var firmwareQueries = Gets(FirmwareOid);
        var sourcePolls = Gets(SourceAvailabilityOid);
        await PollUntil(() => Gets(SourceAvailabilityOid) >= sourcePolls + 3, "The PDU stopped polling while unhealthy");

        Assert.Equal(alarmCounts, Gets(AlarmsPresentOid));
        Assert.Equal(firmwareQueries, Gets(FirmwareOid));
        Assert.DoesNotContain(pdu.GetIssues(), issue => issue.Key is "firmware-read" or "alarm-count-read");

        MockClient.Setup(c => c.Walk(OutletStateColumn)).Returns(Column(OutletStateColumn, _ => new Integer32(2), 10));
        await PollUntil(() => Gets(AlarmsPresentOid) > alarmCounts && Gets(FirmwareOid) > firmwareQueries,
            "The alarm and identity reads never resumed after the PDU recovered");
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);
    }

    public static TheoryData<ErrorCode, int> RejectionErrorStatuses => new()
    {
        { ErrorCode.NoSuchName, 1 },
        { ErrorCode.NoAccess, 1 },
        { ErrorCode.AuthorizationError, 0 },
    };

    [Theory]
    [MemberData(nameof(RejectionErrorStatuses))]
    public async Task Initialise_WithAFirmwareQueryRejectedByErrorStatus_ShowsItIsNotSupportedWithNoIssueAndStopsAsking(
        ErrorCode status, int index)
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(FirmwareOid)).Returns(new SnmpReply([], status, index));

        var pdu = await CreateInitialisedPdu();
        await PollUntil(() => Gets(AlarmsPresentOid) >= 3, "The PDU never polled three times");

        Assert.Equal(new DeviceDetail("Firmware", $"Not supported: error status {status} ({(int)status}), index {index}", DetailTone.Normal),
            Detail(pdu, "Firmware"));
        Assert.Equal(1, Gets(FirmwareOid));
        Assert.Empty(pdu.GetIssues());
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);
        Assert.Equal(string.Empty, pdu.FirmwareVersion);
    }

    [Theory]
    [InlineData(ErrorCode.TooBig, 0)]
    [InlineData(ErrorCode.GenError, 1)]
    [InlineData(ErrorCode.ResourceUnavailable, 1)]
    public async Task Poll_WithATransientErrorStatusOnTheSerialQuery_CountsItAsAMissAndRaisesTheReadIssueOnTheThirdInARow(
        ErrorCode status, int index)
    {
        StubHealthyAtsPdu();
        var errorText = $"error status {status} ({(int)status}), index {index}";
        MockClient.Setup(c => c.Get(SerialOid)).Returns(new SnmpReply([], status, index));

        var pdu = await CreateInitialisedPdu();
        Assert.Equal(new DeviceDetail("Serial", errorText, DetailTone.Warning), Detail(pdu, "Serial"));

        await PollUntil(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "serial-read"),
            "The third error status never raised the read issue");
        var readIssue = pdu.GetOngoingIssues().Single(issue => issue.Key == "serial-read");
        Assert.Equal(IssueSeverity.Minor, readIssue.Severity);
        Assert.Contains(errorText, readIssue.Message);
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);

        MockClient.Setup(c => c.Get(SerialOid)).Returns(SnmpResult(new OctetString("2918QX05284K736051")));
        await PollUntil(() => pdu.GetOngoingIssues().All(issue => issue.Key != "serial-read"),
            "The answered serial query never resolved the read issue");
        Assert.Equal("2918QX05284K736051", pdu.SerialNumber);
    }

    [Fact]
    public async Task Poll_WithAnAlarmCountRejectedByErrorStatus_ShowsItIsNotSupportedAndStopsAsking()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(new SnmpReply([], ErrorCode.NoSuchName, 1));
        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => Gets(OutputPowerOid) >= 4, "The PDU never polled four times");

        Assert.Equal(1, Gets(AlarmsPresentOid));
        Assert.Equal(new DeviceDetail("Alarms", "Not supported: error status NoSuchName (2), index 1", DetailTone.Normal),
            Detail(pdu, "Alarms"));
        Assert.Empty(pdu.GetIssues());
    }

    [Fact]
    public async Task Poll_WithAnErrorStatusOnTheAlarmCount_CountsItAsAMiss()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(new SnmpReply([], ErrorCode.GenError, 1));

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "alarm-count-read"),
            "The third error status never raised the read issue");
        Assert.Contains("error status GenError (5), index 1",
            pdu.GetOngoingIssues().Single(issue => issue.Key == "alarm-count-read").Message);
        Assert.Equal(new DeviceDetail("Alarms", "error status GenError (5), index 1", DetailTone.Warning), Detail(pdu, "Alarms"));
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);
    }

    [Fact]
    public async Task Poll_WithAnErrorStatusOnTheOutletStateWalk_ReportsItLikeAnUnansweredPollWithTheRawStatus()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Walk(OutletStateColumn)).Returns(new SnmpReply([], ErrorCode.GenError, 1));

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed outlet poll was never reported");
        await PollUntil(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "unanswered-outlet-poll"),
            "The failed outlet poll never escalated");
        Assert.Contains(pdu.GetIssues(), issue => issue.Key == "unanswered-outlet-poll" && issue.Message ==
            "The outlet state poll for device 1 was answered with error status GenError (5), index 1");

        MockClient.Setup(c => c.Walk(OutletStateColumn)).Returns(Column(OutletStateColumn, _ => new Integer32(2), 10));
        await PollUntil(() => pdu.CommunicationState == CommunicationState.Okay, "The PDU never recovered");
    }

    [Fact]
    public async Task Poll_WithAnErrorStatusOnTheSourceAvailability_ReportsItLikeAnUnansweredPollWithTheRawStatus()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(new SnmpReply([], ErrorCode.TooBig, 0));

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed input source poll was never reported");
        Assert.Contains(pdu.GetIssues(), issue => issue.Key == "unanswered-ats-poll" && issue.Message ==
            "The input source poll for device 1 was answered with error status TooBig (1), index 0");
    }

    [Fact]
    public async Task Poll_WithAnUnansweredSourceAvailability_KeepsTheUnansweredMessage()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(NoReply);

        await PollUntil(() => pdu.CommunicationState == CommunicationState.Error,
            "The failed input source poll was never reported");
        Assert.Contains(pdu.GetIssues(), issue => issue.Key == "unanswered-ats-poll" && issue.Message ==
            "The input source poll for device 1 was not answered");
    }

    [Fact]
    public async Task Poll_ShowsTheAtsTelemetryAsDetails()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Walk(InputVoltageColumn)).Returns(new SnmpReply(
        [
            new Variable(new ObjectIdentifier($"{InputVoltageColumn}.1.1"), new Gauge32(2452)),
            new Variable(new ObjectIdentifier($"{InputVoltageColumn}.2.1"), new OctetString("garbage"))
        ]));

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.Details.Any(detail => detail.Label == "Output Power"),
            "The output power was never shown");
        Assert.Equal(new DeviceDetail("Output Voltage", "245.2 V"), Detail(pdu, "Output Voltage"));
        Assert.Equal(new DeviceDetail("Output Power", "120 W"), Detail(pdu, "Output Power"));
        Assert.Equal(new DeviceDetail("Input A", "Available, 245.2 V"), Detail(pdu, "Input A"));
        Assert.Equal(new DeviceDetail("Input B", "Available, garbage"), Detail(pdu, "Input B"));
    }

    [Fact]
    public async Task Poll_ShowsAFailedFeedAsAWarningUnlessItsWarningIsDisabled()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(SourceAvailabilityOid)).Returns(SnmpResult(new Integer32(1)));
        MockClient.Setup(c => c.Walk(InputVoltageColumn)).Returns(
            new SnmpReply([new Variable(new ObjectIdentifier($"{InputVoltageColumn}.1.1"), new Gauge32(2452))]));
        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.Details.Any(detail => detail.Label == "Input B"), "Input B was never shown");
        Assert.Equal(new DeviceDetail("Input B", "Unavailable, no voltage reading", DetailTone.Warning), Detail(pdu, "Input B"));

        pdu.DisableInputWarning(TrippLiteInputSource.B);
        await PollUntil(() => Detail(pdu, "Input B").Tone == DetailTone.Normal,
            "Input B kept its warning after its warning was disabled");
    }

    [Fact]
    public async Task Poll_ShowsTheSensorReadingAsADetail()
    {
        StubHealthyAtsPdu();
        StubTemperatureSensor();
        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.Details.Any(detail => detail.Label == "Sensor0333"), "The sensor was never shown");
        Assert.Equal(new DeviceDetail("Sensor0333", "21.8 °C"), Detail(pdu, "Sensor0333"));

        MockClient.Setup(c => c.Get(SensorTemperatureAlarmOid)).Returns(SnmpResult(new Integer32(1)));
        await PollUntil(() => Detail(pdu, "Sensor0333").Tone == DetailTone.Warning, "The sensor alarm was never shown");
        Assert.Equal("21.8 °C, in alarm", Detail(pdu, "Sensor0333").Value);

        MockClient.Setup(c => c.Get(DeviceCountOid)).Returns(SnmpResult(new Gauge32(1)));
        pdu.Reinitialise();
        await PollUntil(() => pdu.Details.All(detail => detail.Label != "Sensor0333"),
            "The removed sensor's detail was never dropped");
    }

    [Fact]
    public async Task Poll_WithNoAlarms_ShowsAZeroCount()
    {
        StubHealthyAtsPdu();

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.Details.Any(detail => detail.Label == "Alarms"), "The alarm count was never shown");
        Assert.Equal(new DeviceDetail("Alarms", "0"), Detail(pdu, "Alarms"));
        MockClient.Verify(c => c.Walk(AlarmDetailColumn), Times.Never);
    }

    [Fact]
    public async Task Poll_WithAlarmsPresent_ShowsThemAllAndRaisesAMinorIssueOnlyForTheOnesThatAreNotDeliberate()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        MockClient.Setup(c => c.Walk(AlarmDetailColumn)).Returns(new SnmpReply(
        [
            new Variable(new ObjectIdentifier($"{AlarmDetailColumn}.46"), new OctetString("Load Off")),
            new Variable(new ObjectIdentifier($"{AlarmDetailColumn}.47"), new OctetString("Input Source B Failed"))
        ]));
        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(SnmpResult(new Gauge32(2)));

        await PollUntil(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "alarms-present"),
            "The alarms issue was never raised");
        var issue = pdu.GetOngoingIssues().Single(i => i.Key == "alarms-present");
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The PDU reports alarms: Input Source B Failed", issue.Message);
        Assert.Equal(new DeviceDetail("Alarms", "2: Load Off; Input Source B Failed", DetailTone.Warning), Detail(pdu, "Alarms"));

        pdu.DisableInputWarning(TrippLiteInputSource.B);
        await PollUntil(() => pdu.GetOngoingIssues().All(i => i.Key != "alarms-present"),
            "Disabling input B's warning never resolved the alarms issue");
        Assert.Equal(new DeviceDetail("Alarms", "2: Load Off; Input Source B Failed"), Detail(pdu, "Alarms"));

        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(SnmpResult(new Gauge32(0)));
        await PollUntil(() => Detail(pdu, "Alarms").Value == "0", "The cleared alarm count was never shown");
    }

    [Fact]
    public async Task Poll_WithAnUnsupportedAlarmCount_ShowsItIsNotSupportedWithNoIssueAndAsksAgainOnlyAfterReinitialise()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(SnmpResult(new NoSuchObject()));
        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => Gets(OutputPowerOid) >= 4, "The PDU never polled four times");
        Assert.Equal(1, Gets(AlarmsPresentOid));
        Assert.Equal(new DeviceDetail("Alarms", "Not supported: NoSuchObject", DetailTone.Normal), Detail(pdu, "Alarms"));
        Assert.Empty(pdu.GetIssues());
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);

        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(SnmpResult(new Gauge32(0)));
        pdu.Reinitialise();
        await PollUntil(() => Detail(pdu, "Alarms").Value == "0",
            "The alarm count was never asked for again after reinitialising");
    }

    [Fact]
    public async Task Poll_WithAnUnansweredAlarmCount_RaisesTheReadIssueOnlyOnTheThirdMissAndLeavesTheCommunicationStateAlone()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(NoReply);

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => Gets(AlarmsPresentOid) == 2, "The alarm count was never asked for a second time");
        Assert.Equal(new DeviceDetail("Alarms", "no answer", DetailTone.Warning), Detail(pdu, "Alarms"));
        Assert.Empty(pdu.GetIssues());
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);

        await PollUntil(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "alarm-count-read"),
            "The third unanswered alarm count never raised the read issue");
        Assert.Equal(3, Gets(AlarmsPresentOid));
        var readIssue = pdu.GetOngoingIssues().Single(issue => issue.Key == "alarm-count-read");
        Assert.Equal(IssueSeverity.Minor, readIssue.Severity);
        Assert.Contains("no reply", readIssue.Message);
        Assert.Equal(CommunicationState.Okay, pdu.CommunicationState);

        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(SnmpResult(new Gauge32(0)));
        await PollUntil(() => pdu.GetOngoingIssues().All(issue => issue.Key != "alarm-count-read"),
            "The answered alarm count never resolved the read issue");
        Assert.Equal(new DeviceDetail("Alarms", "0"), Detail(pdu, "Alarms"));
    }

    [Fact]
    public async Task Poll_WithAnAlarmCountThatIsNotANumber_ShowsTheRawReply()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(AlarmsPresentOid)).Returns(SnmpResult(new OctetString("two")));

        var pdu = await CreateInitialisedPdu();

        await PollUntil(() => pdu.GetOngoingIssues().Any(issue => issue.Key == "alarm-count-read"),
            "The unparseable alarm count was never reported");
        Assert.Contains("two", pdu.GetOngoingIssues().Single(issue => issue.Key == "alarm-count-read").Message);
        Assert.Equal(new DeviceDetail("Alarms", "two", DetailTone.Warning), Detail(pdu, "Alarms"));
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

        MockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}1", 2), Times.Once);
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

        MockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}2", 3), Times.Once);
    }

    [Fact]
    public async Task PowerOn_OnANonControllableOutlet_DoesNotSendACommand()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[8];

        outlet.PowerOn();

        MockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}9", It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task PowerOn_OnAnOutletOfAnUndiscoveredDevice_DoesNotSendACommand()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();
        var stray = new TrippLiteOutlet("Stray", pdu, 5, 1, true);

        stray.PowerOn();

        MockClient.Verify(c => c.Set(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        Assert.Equal(PowerState.Unknown, stray.PowerState);
    }

    [Fact]
    public async Task PowerOff_OnThePdu_SkipsNonControllableOutlets()
    {
        StubHealthyAtsPdu();
        var pdu = await CreateInitialisedPdu();

        pdu.PowerOff();

        for (var outletNumber = 1; outletNumber <= 8; outletNumber++)
            MockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}{outletNumber}", 1), Times.Once);
        MockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}9", It.IsAny<int>()), Times.Never);
        MockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}10", It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task PowerOn_WithAnUnacknowledgedCommand_DoesNotChangeTheState()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Walk(OutletStateColumn))
            .Returns(Column(OutletStateColumn, _ => new Integer32(1), 10));
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[0];
        await PollUntil(() => outlet.PowerState == PowerState.Off, "The outlet never polled as off");
        MockClient.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<int>())).Returns(NoReply);

        outlet.PowerOn();

        MockClient.Verify(c => c.Set($"{OutletCommandOidPrefix}1", 2), Times.Once);
        Assert.Equal(PowerState.Off, outlet.PowerState);
    }

    [Fact]
    public async Task PowerOn_WithACommandRefusedByErrorStatus_ShowsTheRawStatusAndDoesNotChangeTheState()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Walk(OutletStateColumn))
            .Returns(Column(OutletStateColumn, _ => new Integer32(1), 10));
        var pdu = await CreateInitialisedPdu();
        var outlet = (TrippLiteOutlet)pdu.Outlets[0];
        await PollUntil(() => outlet.PowerState == PowerState.Off, "The outlet never polled as off");
        MockClient.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<int>())).Returns(new SnmpReply([], ErrorCode.NoAccess, 1));

        outlet.PowerOn();

        Assert.Equal(PowerState.Off, outlet.PowerState);
        Assert.Contains(pdu.GetIssues(), issue => issue.Key == "unacknowledged-command" && issue.Message ==
            "The power on command for outlet Load 1 was answered with error status NoAccess (6), index 1");
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class TrippLitePduLogTest : TrippLitePduFixture
{
    private async Task<TrippLitePdu> CreateLoggedPdu(ScopeCapturingLoggerFactory logs)
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        TrippLitePdu pdu;
        try
        {
            pdu = CreatePdu();
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
        await PollUntil(() => pdu.CommunicationState == CommunicationState.Okay, "The PDU never initialised");
        return pdu;
    }

    [Fact]
    public async Task Initialise_WithAnUnsupportedMac_ShowsItIsNotSupportedAndLogsItOnceWithNoIssue()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(MacOid)).Returns(SnmpResult(new NoSuchObject()));
        var logs = new ScopeCapturingLoggerFactory();

        var pdu = await CreateLoggedPdu(logs);
        await PollUntil(() => Gets(AlarmsPresentOid) >= 3, "The PDU never polled three times");

        Assert.Equal(new DeviceDetail("MAC", "Not supported: NoSuchObject", DetailTone.Normal), Detail(pdu, "MAC"));
        Assert.Empty(pdu.GetIssues());
        Assert.Single(logs.Logs, log => log.Message.Contains(MacOid));
        Assert.Contains(logs.Logs, log => log.Level == LogLevel.Information && log.Message.Contains(MacOid)
                                                                             && log.Message.Contains("NoSuchObject"));
        Assert.Equal(1, Gets(MacOid));
        Assert.Equal(10, pdu.Outlets.Count);
    }

    [Fact]
    public async Task Initialise_WithAnUnansweredMac_ShowsNoAnswerCarriesOnAndAsksAgainOnTheNextPoll()
    {
        StubHealthyAtsPdu();
        MockClient.Setup(c => c.Get(MacOid)).Returns(NoReply);
        var logs = new ScopeCapturingLoggerFactory();

        var pdu = await CreateLoggedPdu(logs);

        Assert.Equal(new DeviceDetail("MAC", "no answer", DetailTone.Warning), Detail(pdu, "MAC"));
        Assert.Equal(10, pdu.Outlets.Count);
        Assert.Contains(logs.Logs, log => log.Level == LogLevel.Information && log.Message.Contains(MacOid)
                                                                             && log.Message.Contains("no reply"));

        MockClient.Setup(c => c.Get(MacOid)).Returns(SnmpResult(new OctetString(Mac)));
        await PollUntil(() => Detail(pdu, "MAC").Value == Mac,
            "The unanswered MAC query was never asked again");
    }
}