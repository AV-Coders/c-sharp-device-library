using System.Collections.Concurrent;
using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusInterfaceTest
{
    internal const string FirstWire = "\\0538007919r\r";
    internal const string SecondWire = "\\053800011Ar\r";
    private const string ThirdWire = "\\053800791Ar\r";
    private const string UnitTypeRead = "@2101r\r";
    private const string FirmwareRead = "@2102r\r";
    private const string NetworkVoltageRead = "@2107r\r";
    private const string DiagnosticSummaryRead = "@2104r\r";
    private static readonly Actor FirstActor = new(ActorKind.Browser, "10.0.0.1", "First browser", null, "first");
    private static readonly Actor SecondActor = new(ActorKind.Browser, "10.0.0.2", "Second browser", null, "second");
    private readonly CBusSerialInterface _interface;
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly List<string> _sent = [];
    private readonly List<(string Wire, string? TriggerId, Actor? Actor)> _sends = [];
    private readonly List<CBusGroupMessage> _groupMessages = [];
    private readonly List<CBusChannelLevel> _channelLevels = [];

    public CBusInterfaceTest()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(wire =>
        {
            _sent.Add(wire);
            _sends.Add((wire, Trigger.Id, Actor.Current));
        });
        _interface = new CBusSerialInterface(_mockClient.Object, _time);
        _interface.GroupMessageHandlers += _groupMessages.Add;
        _interface.ChannelLevelHandlers += _channelLevels.Add;
    }

    private void Receive(string text) => _mockClient.Object.ResponseByteHandlers!.Invoke(Encoding.ASCII.GetBytes(text));

    private async Task SetupPci()
    {
        Task setup = _interface.SetupPci();
        while (!setup.IsCompleted)
            await Task.Run(() => _time.Advance(CBusSerialInterface.SetupStepDelay));
        await setup;
    }

    private void ConfirmThePciReads()
    {
        while (_interface.QueuedCommands > 0)
            Receive("r.");
        Receive("r.");
    }

    private void AnswerThePciReads()
    {
        Receive("r.86939300890135353030504320202D\r\n");
        Receive("r.869393008902312E322E363620205E\r\n");
        Receive("r.86939300860732392E3656A2\r\n");
        Receive("r.869393008D0438FF00123400000000BC00008A\r\n");
    }

    private string? Detail(string label) => _interface.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    private Issue? OngoingIssue(string key) => _interface.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    [Fact]
    public void SendPointToMultipointPayload_SendsUppercaseHexWithConfirmationAndNoChecksum()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);

        Assert.Equal(["\\0538007919r\r"], _sent);
    }

    [Fact]
    public void RequestChannelStatus_SendsThePointToPointStatusRequest()
    {
        _interface.RequestChannelStatus(0x74, 0x01);

        Assert.Equal(["\\0674002A0101r\r"], _sent);
    }

    [Fact]
    public void SecondCommand_WaitsForTheConfirmation()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);

        Assert.Equal(["\\0538007919r\r"], _sent);
        Assert.Equal(1, _interface.QueuedCommands);

        Receive("r.");

        Assert.Equal(["\\0538007919r\r", "\\053800011Ar\r"], _sent);
        Assert.Equal(0, _interface.QueuedCommands);
    }

    [Fact]
    public void Confirmation_WithNothingQueued_LetsTheNextCommandGoStraightOut()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        Receive("r.");
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x19]);

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void MissingConfirmation_SendsTheNextCommandAfterTheTimeout()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);

        _time.Advance(CBusSerialInterface.ConfirmationTimeout);

        Assert.Equal(["\\0538007919r\r", "\\053800011Ar\r"], _sent);
    }

    [Fact]
    public void SendFailureAfterTheConfirmationTimeout_IsLoggedAndTheQueueMovesOn()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x1A]);
        var sendError = new IOException("Oh No!");
        _mockClient.Setup(x => x.Send(SecondWire)).Throws(sendError);

        _time.Advance(CBusSerialInterface.ConfirmationTimeout);
        _time.Advance(CBusSerialInterface.ConfirmationTimeout);

        Assert.Same(sendError, Assert.Single(_interface.Errors).Exception);
        Assert.Equal([FirstWire, ThirdWire], _sent);
    }

    [Fact]
    public void QueuedCommand_IsSentUnderItsOwnTriggerId()
    {
        Trigger.Start();
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        var second = Trigger.Start();
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);

        Trigger.Start();
        Receive("r.");

        Assert.Equal(second, _sends.Single(x => x.Wire == SecondWire).TriggerId);
    }

    [Fact]
    public async Task TimeoutDrivenSend_OnTheSystemClock_DoesNotCarryThePreviousCommandsActor()
    {
        var client = TestFactory.CreateCommunicationClient();
        var sends = new ConcurrentQueue<(string Wire, string? TriggerId, Actor? Actor)>();
        client.Setup(x => x.Send(It.IsAny<string>()))
            .Callback<string>(wire => sends.Enqueue((wire, Trigger.Id, Actor.Current)));
        var cbus = new CBusSerialInterface(client.Object);
        Actor.Current = FirstActor;
        Trigger.Start();
        cbus.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        Actor.Current = SecondActor;
        var second = Trigger.Start();
        cbus.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);
        Actor.Current = null;

        await Wait.UntilAsync(() => sends.Any(x => x.Wire == SecondWire), "The second command was never sent after the confirmation timeout");

        var send = sends.Single(x => x.Wire == SecondWire);
        Assert.Equal(second, send.TriggerId);
        Assert.Null(send.Actor);
    }

    [Fact]
    public void FullQueue_IsCleared()
    {
        for (int i = 0; i <= CBusSerialInterface.MaxQueuedCommands + 1; i++)
            _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);

        Assert.Equal(1, _interface.QueuedCommands);
    }

    [Fact]
    public async Task SetupPci_SendsTheSetupStringsInOrder()
    {
        await SetupPci();

        Assert.Equal(["~~~\r", "~~~p\r", "@A330005100p\r", "@A3410051p\r", "@A3420007p\r", "@A32100FF00p\r", UnitTypeRead], _sent);
    }

    [Fact]
    public async Task SetupPci_ClearsTheQueue()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x19]);

        await SetupPci();
        ConfirmThePciReads();
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x1A]);

        Assert.Equal(0, _interface.QueuedCommands);
        Assert.DoesNotContain("\\0538000119r\r", _sent);
        Assert.Equal("\\053800791Ar\r", _sent.Last());
    }

    [Fact]
    public async Task SetupPci_ThenReadsTheUnitTypeFirmwareNetworkVoltageAndStatus_OneAtATime()
    {
        await SetupPci();
        Assert.Equal(UnitTypeRead, _sent.Last());

        ConfirmThePciReads();

        Assert.Equal([UnitTypeRead, FirmwareRead, NetworkVoltageRead, DiagnosticSummaryRead], _sent.Skip(6));
    }

    [Fact]
    public async Task UnitTypeReply_ShowsTheUnitType()
    {
        await SetupPci();
        Receive("86939300890135353030504320202D\r\n");

        Assert.Equal("5500PC", Detail("Unit Type"));
    }

    [Fact]
    public async Task FirmwareReply_FromTheGuidesIdentifyExample_ShowsTheFirmware()
    {
        await SetupPci();
        Receive("869393008902312E322E363620205E\r\n");

        Assert.Equal("1.2.66", Detail("Firmware"));
        Assert.Empty(_channelLevels);
    }

    [Fact]
    public async Task NetworkVoltageReply_ShowsTheVoltageAsSent()
    {
        await SetupPci();
        Receive("86939300860732392E3656A2\r\n");

        Assert.Equal("29.6V", Detail("Network Voltage"));
    }

    [Fact]
    public async Task DiagnosticSummary_WithTheNetworkVoltageLowBit_RaisesAMajorIssue()
    {
        await SetupPci();
        Receive("869393008D0438FF00123400000000BC60002A\r\n");

        var issue = OngoingIssue("network-voltage");
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Contains("low", issue.Message);
    }

    [Fact]
    public async Task DiagnosticSummary_WithTheNetworkVoltageMarginalBit_RaisesAMinorIssue()
    {
        await SetupPci();
        Receive("869393008D0438FF00123400000000BC20006A\r\n");

        var issue = OngoingIssue("network-voltage");
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Contains("marginal", issue.Message);
    }

    [Fact]
    public async Task DiagnosticSummary_WithTheNetworkVoltageBitsClear_ResolvesTheIssue()
    {
        await SetupPci();
        Receive("869393008D0438FF00123400000000BC60002A\r\n");
        _time.Advance(CBusSerialInterface.HealthReadInterval);

        Receive("869393008D0438FF00123400000000BC00008A\r\n");

        Assert.Null(OngoingIssue("network-voltage"));
        Assert.Equal("00", Detail("Error Flags"));
    }

    [Fact]
    public async Task DiagnosticSummary_WithOnlyThePowerOnResetFlag_ShowsItWithNoIssue()
    {
        await SetupPci();
        Receive("869393008D0438FF00123400000000BC000189\r\n");

        Assert.Equal("01 (micro power on reset)", Detail("Error Flags"));
        Assert.Empty(_interface.GetOngoingIssues());
    }

    [Theory]
    [InlineData("869393008D0438FF00123400000000BC002169", IssueSeverity.Major, "21 (micro power on reset, EE checksum error)")]
    [InlineData("869393008D0438FF00123400000000BC000486", IssueSeverity.Minor, "04 (comms tx error)")]
    public async Task DiagnosticSummary_WithAnErrorFlag_RaisesAnIssueOfItsSeverity(string reply, IssueSeverity severity, string flags)
    {
        await SetupPci();
        Receive($"{reply}\r\n");

        var issue = OngoingIssue("error-flags");
        Assert.NotNull(issue);
        Assert.Equal(severity, issue.Severity);
        Assert.Contains(flags, issue.Message);
    }

    [Theory]
    [InlineData("869393008902312E32FF363620208D", "@2102", "firmware-read", "which is not ASCII text", "869393008902312E322E363620205E")]
    [InlineData("869393008902313266", "@2102", "firmware-read", "which is not the length its header gives", "869393008902312E322E363620205E")]
    [InlineData("86939300850438FF001282", "@2104", "diagnostic-summary-read", "which is not 12 bytes", "869393008D0438FF00123400000000BC00008A")]
    public async Task UnreadableReply_IsAskedAgainAndRaisesTheReadIssueWithTheRawReplyOnTheThirdInARow(
        string reply, string command, string issueKey, string reason, string goodReply)
    {
        await SetupPci();
        ConfirmThePciReads();
        Receive($"{reply}\r\n");
        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();
        Receive($"{reply}\r\n");

        Assert.Empty(_interface.GetIssues());

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();
        Receive($"{reply}\r\n");

        Assert.Equal(3, _sent.Count(sent => sent == $"{command}r\r"));
        var issue = OngoingIssue(issueKey);
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal($"The PCI did not answer {command} 3 times in a row, the last was {reply}, {reason}", issue.Message);

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();
        Receive($"{goodReply}\r\n");

        Assert.Null(OngoingIssue(issueKey));
    }

    [Fact]
    public async Task FirmwareReply_WithNoText_ShowsTheRawReplyWithNoIssueAndIsNotAskedAgain()
    {
        await SetupPci();
        ConfirmThePciReads();
        Receive("869393008102D1\r\n");
        _sent.Clear();

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();

        Assert.Equal(new DeviceDetail("Firmware", "869393008102D1"), _interface.Details.Single(detail => detail.Label == "Firmware"));
        Assert.DoesNotContain(FirmwareRead, _sent);
        Assert.Empty(_interface.GetIssues());
    }

    [Fact]
    public void SetupAcknowledge_IsIgnored()
    {
        Receive("86939300323000F2\r\n");

        Assert.Empty(_interface.Details);
        Assert.Empty(_interface.GetOngoingIssues());
        Assert.Empty(_channelLevels);
    }

    [Fact]
    public async Task HealthRead_EveryInterval_ReadsTheNetworkVoltageAndStatus()
    {
        await SetupPci();
        AnswerThePciReads();
        _sent.Clear();

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();

        Assert.Equal([NetworkVoltageRead, DiagnosticSummaryRead], _sent);
    }

    [Fact]
    public async Task OverlappingSetupPci_LeavesOneHealthTimer()
    {
        Task first = _interface.SetupPci();
        Task second = _interface.SetupPci();
        while (!first.IsCompleted || !second.IsCompleted)
            await Task.Run(() => _time.Advance(CBusSerialInterface.SetupStepDelay));
        await Task.WhenAll(first, second);
        ConfirmThePciReads();
        AnswerThePciReads();
        _sent.Clear();

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();
        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();

        Assert.Equal([NetworkVoltageRead, DiagnosticSummaryRead, NetworkVoltageRead, DiagnosticSummaryRead], _sent);
    }

    [Fact]
    public async Task HealthRead_UnansweredReads_AreAskedAgainAndRaiseTheReadIssueOnTheThirdMissUntilAnswered()
    {
        await SetupPci();
        ConfirmThePciReads();
        _sent.Clear();

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();
        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();

        Assert.Empty(_interface.GetIssues());
        Assert.Equal([UnitTypeRead, FirmwareRead, NetworkVoltageRead, DiagnosticSummaryRead,
            UnitTypeRead, FirmwareRead, NetworkVoltageRead, DiagnosticSummaryRead], _sent);

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();

        var issue = OngoingIssue("firmware-read");
        Assert.NotNull(issue);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The PCI did not answer @2102 3 times in a row, the last was no reply", issue.Message);
        Assert.Equal(12, _sent.Count);

        Receive("869393008902312E322E363620205E\r\n");

        Assert.Null(OngoingIssue("firmware-read"));
        Assert.NotNull(OngoingIssue("network-voltage-read"));
    }

    [Fact]
    public async Task HealthRead_AfterAnsweredIdentityReads_AsksOnlyForTheUnansweredOneAndTheHealthReads()
    {
        await SetupPci();
        ConfirmThePciReads();
        Receive("86939300890135353030504320202D\r\n");
        _sent.Clear();

        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();

        Assert.Equal([FirmwareRead, NetworkVoltageRead, DiagnosticSummaryRead], _sent);
    }

    [Fact]
    public async Task SetupPci_StartsTheMissCountAgain()
    {
        await SetupPci();
        ConfirmThePciReads();
        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();
        _time.Advance(CBusSerialInterface.HealthReadInterval);
        ConfirmThePciReads();

        await SetupPci();
        ConfirmThePciReads();
        _time.Advance(CBusSerialInterface.HealthReadInterval);

        Assert.Empty(_interface.GetIssues());
    }

    [Fact]
    public void MonitoredOn_RaisesAGroupMessage()
    {
        Receive("0553380079C532\r\n");

        Assert.Equal([new CBusGroupMessage(0x53, 0x38, 0x79, 0xC5, 0xFF)], _groupMessages);
    }

    [Fact]
    public void MonitoredOff_RaisesAGroupMessage()
    {
        Receive("0552380001D898\r\n");

        Assert.Equal([new CBusGroupMessage(0x52, 0x38, 0x01, 0xD8, 0x00)], _groupMessages);
    }

    [Fact]
    public void MonitoredRamp_RaisesTheTargetLevel()
    {
        Receive("05533800121980E5\r\n");

        Assert.Equal([new CBusGroupMessage(0x53, 0x38, 0x12, 0x19, 0x80)], _groupMessages);
    }

    [Fact]
    public void MessageWithTwoCommands_RaisesBoth()
    {
        Receive("05533800791901196A\r\n");

        Assert.Equal(
            [new CBusGroupMessage(0x53, 0x38, 0x79, 0x19, 0xFF), new CBusGroupMessage(0x53, 0x38, 0x01, 0x19, 0x00)],
            _groupMessages);
    }

    [Fact]
    public void MessageWithOnAndRamp_RaisesBoth()
    {
        Receive("0553380079190A1A40AA\r\n");

        Assert.Equal(
            [new CBusGroupMessage(0x53, 0x38, 0x79, 0x19, 0xFF), new CBusGroupMessage(0x53, 0x38, 0x0A, 0x1A, 0x40)],
            _groupMessages);
    }

    [Fact]
    public void MessageSplitAcrossReads_IsJoined()
    {
        Receive("0553380");
        Receive("079C532\r\n");

        Assert.Single(_groupMessages);
    }

    [Fact]
    public void ConfirmationCharacter_DiscardsThePartialLine()
    {
        Receive("05533r.80079C532\r\n");

        Assert.Empty(_groupMessages);
    }

    [Fact]
    public void SetupPci_WaitsBeforeTheNextSetupString()
    {
        _ = _interface.SetupPci();

        Assert.Equal(["~~~\r"], _sent);
    }

    [Fact]
    public void NoiseOfAFullLineWithoutALineFeed_IsDiscarded()
    {
        Receive(new string('0', CBusSerialInterface.MaxLineLength) + "0553380079C532\r\n");

        Assert.Equal([new CBusGroupMessage(0x53, 0x38, 0x79, 0xC5, 0xFF)], _groupMessages);
    }

    [Fact]
    public void StatusReply_RaisesTheChannelLevel()
    {
        Receive("867400008201806A\r\n");

        Assert.Equal([new CBusChannelLevel(0x74, 0x01, 0x80)], _channelLevels);
    }

    [Fact]
    public async Task StatusReplyFromUnitZero_WhileTheUnitTypeIsAwaited_RaisesTheChannelLevelAndLeavesTheUnitTypeAwaited()
    {
        await SetupPci();

        Receive("8600000082018077\r\n");

        Assert.Equal([new CBusChannelLevel(0x00, 0x01, 0x80)], _channelLevels);
        Assert.Empty(_interface.Details);
        Assert.Empty(_interface.GetIssues());

        Receive("86939300890135353030504320202D\r\n");

        Assert.Equal("5500PC", Detail("Unit Type"));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class CBusInterfaceLogTest
{
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly CBusSerialInterface _interface;

    public CBusInterfaceLogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _interface = new CBusSerialInterface(_mockClient.Object, _time);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Receive(string text) => _mockClient.Object.ResponseByteHandlers!.Invoke(Encoding.ASCII.GetBytes(text));

    private IEnumerable<(string Message, string? TriggerId)> Logged() => _logs.Logs.Select(log => (log.Message, log.TriggerId));

    [Fact]
    public void Confirmation_IsHandledUnderTheInFlightCommandsTriggerId()
    {
        var first = Trigger.Start();
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        var second = Trigger.Start();
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);

        Trigger.Start();
        Receive("r.");
        Receive("r.");

        Assert.Contains(($"PCI confirmed {CBusInterfaceTest.FirstWire.TrimEnd()}", first), Logged());
        Assert.Contains(($"PCI confirmed {CBusInterfaceTest.SecondWire.TrimEnd()}", second), Logged());
    }

    [Fact]
    public void Confirmation_ForACommandSentWithNoTriggerId_KeepsTheReceiveTriggerId()
    {
        Assert.Null(Trigger.Id);
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);

        var receiveId = Trigger.Start();
        Receive("r.");

        Assert.Contains(($"PCI confirmed {CBusInterfaceTest.FirstWire.TrimEnd()}", receiveId), Logged());
    }
}
