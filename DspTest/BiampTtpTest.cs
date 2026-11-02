using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Dsp.Tests;

public class BiampTtpTest
{
    private readonly FakeTimeProvider _time = new();
    private readonly BiampTtp _dsp;
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private const string GainName = "Gain";
    private const string MuteName = "Mute";
    private const string StringName = "String";
    private const int PollIntervalMs = 100;
    private const int NeverPollMs = 3_600_000;
    private const string MaxLevelQuery = $"{GainName} get maxLevel 1\n";
    private const string MinLevelQuery = $"{GainName} get minLevel 1\n";
    private const string MaxLevelReply = "+OK \"value\":12.000000\n";
    private const string MinLevelReply = "+OK \"value\":0.000000\n";
    private const string LevelReply = "+OK \"value\":3.000000\n";
    private const string UnknownControl = "Nowhere";
    private const string DisconnectedPollEvent = "IP Comms disconnected, not polling";
    private const string FirstQueryAfterReinitialise = $"{GainName} get maxLevel 1\n";
    private const string FirmwareQueryCommand = "DEVICE get version\n";
    private const string SerialQueryCommand = "DEVICE get serialNumber\n";
    private const string DeviceInfoQueryCommand = "DEVICE get deviceInfo\n";
    private const string NetworkStatusQueryCommand = "DEVICE get networkStatus\n";
    private const string AliasesQueryCommand = "SESSION get aliases\n";
    private const string FaultsQueryCommand = "DEVICE get activeFaultList\n";
    private const string VersionReply = TesiraReplies.Version;
    private const string OlderVersionReply = "+OK \"value\":\"4.2.1.23\"";
    private const string SerialReply = TesiraReplies.SerialNumber;
    private const string GeneralFailureError = "-ERR GENERAL_FAILURE";
    private const string FirmwareIssueKey = "firmware-read";
    private const string SerialIssueKey = "serial-read";
    private const string DeviceInfoIssueKey = "device-info-read";
    private const string NetworkStatusIssueKey = "network-status-read";
    private const string AliasesIssueKey = "aliases-read";
    private const string FaultsIssueKey = "fault-list-read";
    private const string SlowRepliesIssueKey = "slow-replies";
    private const string FirmwareHealthIssueKey = "firmware-health";
    private const string FanFaultIssueKey = "fault:04351854:FAULT_FAN_MALFUNCTION";
    private const string FanFault = """{"id":FAULT_FAN_MALFUNCTION "name":"Cooling fan malfunction.  Check filter and fan."}""";
    private const string FanFaultReply = $$"""+OK "value":[{"id":INDICATOR_MINOR_IN_DEVICE "name":"Minor Fault in Device" "faults":[{{FanFault}}] "serialNumber":"04351854"}]""";
    private const string ExpanderFaultReply = $$"""+OK "value":[{"id":INDICATOR_MINOR_IN_DEVICE "name":"Minor Fault in Device" "faults":[{{FanFault}}] "serialNumber":"04351854"} {"id":INDICATOR_MAJOR_IN_DEVICE "name":"Major Fault in Device" "faults":[{"id":FAULT_DSP_CARD "name":"One or more DSP cards failed to boot"}] "serialNumber":"01882935"}]""";
    private const string NotAStringReply = "+OK \"value\":-12.000000";
    private const string NoModelReply = """+OK "info":{"deviceRevision":"Rev. A" "serialNumber":"04351854"}""";
    private const string RecMicLevel = "rec_mic_level";
    private const string RecMicLevelSubscription = "rec_mic_level subscribe level 1 AvCodersLevel-rec_mic_level-1\n";
    private const string NoInterfacesReply ="""+OK "value":{"schemaVersion":2 "hostname":"TesiraForte04351854" "dnsStatus":{"primaryDNSServer":"10.1.0.1" "secondaryDNSServer":"" "domainName":"millpark.acarbone.com"}}""";
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan KeepAliveReplyWindow = TimeSpan.FromMilliseconds(BiampTtp.KeepAliveReplyWindowMs);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InterruptedReplyTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SlowReply = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan OneTick = TimeSpan.FromMilliseconds(1);
    private static readonly Query FirmwareQuery = new("DEVICE", BiampQuery.Firmware, FirmwareQueryCommand);
    private static readonly Query SerialQuery = new("DEVICE", BiampQuery.Serial, SerialQueryCommand);
    private static readonly Query DeviceInfoQuery = new("DEVICE", BiampQuery.DeviceInfo, DeviceInfoQueryCommand);
    private static readonly Query NetworkStatusQuery = new("DEVICE", BiampQuery.NetworkStatus, NetworkStatusQueryCommand);
    private static readonly Query AliasesQuery = new("SESSION", BiampQuery.Aliases, AliasesQueryCommand);

    private static readonly string[] Subscriptions =
    [
        $"{GainName} subscribe level 1 AvCodersLevel-{GainName}-1\n",
        $"{MuteName} subscribe mute 1 AvCodersMute-{MuteName}-1\n"
    ];

    public BiampTtpTest()
    {
        _dsp = new BiampTtp(_mockClient.Object, "Test", PollIntervalMs, FaderCurve.Linear, _time);

        _mockClient.Setup(x => x.Send($"{GainName} get maxLevel 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":12.000000\n"));
        _mockClient.Setup(x => x.Send($"{GainName} get minLevel 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":0.000000\n"));
        _mockClient.Setup(x => x.Send($"{GainName} get level 1\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":3.000000\n"));

        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    [Fact]
    public void AddControl_WhileConnected_SubscribesStraightAway()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);

        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);

        Assert.Equal(Subscriptions, client.Sent.Where(IsSubscribe));
    }

    [Fact]
    public void AddControl_WhileDisconnected_SendsNoSubscribe()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = Create(client);

        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);

        Assert.DoesNotContain(client.Sent, IsSubscribe);
    }

    [Fact]
    public async Task Connecting_SendsEachRegisteredSubscriptionOnce()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = Create(client);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        await Wait.UntilAsync(_time, Step, () => dsp.Events.Any(e => e.Info == DisconnectedPollEvent), "the poll worker never ran");

        client.SetConnectionState(ConnectionState.Connected);
        await Wait.UntilAsync(_time, Step, () => client.Sent.Contains(FirstQueryAfterReinitialise), "the first query after reinitialising was never sent");

        Assert.Equal(Subscriptions.Order(), client.Sent.Where(IsSubscribe).Order());
    }

    [Fact]
    public async Task Reinitialise_WhileDisconnected_SendsNothing()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = Create(client);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);

        await dsp.Reinitialise();

        Assert.Empty(client.Sent);
    }

    [Fact]
    public void HandleResponse_StoresThePercentage()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersLevel-Gain-1\" \"value\":-2.000000\n");

        Assert.Equal(98, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData("! \"publishToken\":\"AvCodersMute-Mute-1\" \"value\":false\n", MuteState.Off)]
    [InlineData("! \"publishToken\":\"AvCodersMute-Mute-1\" \"value\":true\n", MuteState.On)]
    public void HandleResponse_StoresTheMuteState(string response, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        Assert.Equal(expectedMuteState, _dsp.GetAudioMute(MuteName));
    }

    [Theory]
    [InlineData(0, "Gain set level 1 -100\n")]
    [InlineData(100, "Gain set level 1 0\n")]
    [InlineData(50, "Gain set level 1 -50\n")]
    [InlineData(13, "Gain set level 1 -87\n")]
    public void SetLevel_SendsTheCorrectDB(int percentage, string expectedCommand)
    {
        _dsp.SetLevel(GainName, percentage);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(0, "Gain set level 1 -100\n")]
    [InlineData(50, "Gain set level 1 -30\n")]
    [InlineData(100, "Gain set level 1 0\n")]
    public void SetLevel_WithPerceptualCurve_SendsTheCorrectDB(int percentage, string expectedCommand)
    {
        var client = TestFactory.CreateTcpClient();
        var dsp = new BiampTtp(client.Object, "Perceptual", PollIntervalMs, FaderCurve.Perceptual, _time);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);

        dsp.SetLevel(GainName, percentage);

        client.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void HandleResponse_WithPerceptualCurve_StoresThePercentage()
    {
        var client = TestFactory.CreateTcpClient();
        var dsp = new BiampTtp(client.Object, "Perceptual", PollIntervalMs, FaderCurve.Perceptual, _time);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);

        client.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersLevel-Gain-1\" \"value\":-30.000000\n");

        Assert.Equal(50, dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData(1001, "DEVICE recallPreset 1001\n")]
    [InlineData(1500, "DEVICE recallPreset 1500\n")]
    public void RecallPreset_RecallsThePreset(int presetNumber, string expectedCommand)
    {
        _dsp.RecallPreset(presetNumber);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData("EWIS_ON", "DEVICE recallPresetByName \"EWIS_ON\"\n")]
    [InlineData("EWIS_OFF","DEVICE recallPresetByName \"EWIS_OFF\"\n")]
    [InlineData("EWIS OFF","DEVICE recallPresetByName \"EWIS OFF\"\n")]
    public void RecallPresetByName_RecallsThePreset(string presetName, string expectedCommand)
    {
        _dsp.RecallPreset(presetName);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(true, 5, "mic_press set state 5 true\n")]
    [InlineData(false,3, "mic_press set state 3 false\n")]
    public void SetState_SendsTheCommand(bool state, int index, string expectedCommand)
    {
        _dsp.SetState("mic_press", index, state);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(MuteState.On, "true")]
    [InlineData(MuteState.Off, "false")]
    public void SetAudioMute_DefaultsToIndexOne(MuteState state, string expectedValue)
    {
        _dsp.SetAudioMute(MuteName, state);
        _mockClient.Verify(x => x.Send($"{MuteName} set mute 1 {expectedValue}\n"));
    }

    [Theory]
    [InlineData(MuteState.On, 5, "room_mic_level set mute 5 true\n")]
    [InlineData(MuteState.Off, 5, "room_mic_level set mute 5 false\n")]
    [InlineData(MuteState.On, 3, "room_mic_level set mute 3 true\n")]
    public void SetAudioMute_AtIndex_SendsTheCommand(MuteState state, int index, string expectedCommand)
    {
        _dsp.SetAudioMute("room_mic_level", index, state);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void ToggleAudioMute_AtIndex_TogglesFromOnToOff()
    {
        _dsp.AddControl(Mock.Of<MuteStateHandler>(), "room_mic_level", 5);
        _mockClient.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersMute-room_mic_level-5\" \"value\":true\n");

        _dsp.ToggleAudioMute("room_mic_level", 5);

        _mockClient.Verify(x => x.Send("room_mic_level set mute 5 false\n"));
    }

    [Fact]
    public void BiampVolumeControl_MuteCommands_UseTheConfiguredIndex()
    {
        var vc = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);

        vc.SetAudioMute(MuteState.On);
        _mockClient.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersMute-room_mic_level-5\" \"value\":true\n");
        vc.ToggleAudioMute();

        _mockClient.Verify(x => x.Send("room_mic_level set mute 5 true\n"));
        _mockClient.Verify(x => x.Send("room_mic_level set mute 5 false\n"));
    }

    [Fact]
    public void BiampVolumeControl_SetLevel_UsesTheConfiguredIndex()
    {
        _mockClient.Setup(x => x.Send("room_mic_level get maxLevel 5\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":0.000000\n"));
        _mockClient.Setup(x => x.Send("room_mic_level get minLevel 5\n"))
            .Callback(() => _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":-100.000000\n"));
        var vc = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);

        vc.SetLevel(50);

        _mockClient.Verify(x => x.Send("room_mic_level set level 5 -50\n"));
    }

    [Fact]
    public void BiampVolumeControl_Construction_SubscribesAtTheConfiguredIndex()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);

        _ = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, dsp);

        Assert.Contains("room_mic_level subscribe level 5 AvCodersLevel-room_mic_level-5\n", client.Sent);
        Assert.Contains("room_mic_level subscribe mute 5 AvCodersMute-room_mic_level-5\n", client.Sent);
    }

    [Fact]
    public void ClientDisconnectedOnRequest_ReportsNotAttempted()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);

        client.Disconnect();

        Assert.Equal(CommunicationState.NotAttempted, dsp.CommunicationState);
    }

    [Fact]
    public async Task MatchedReply_IsHandledUnderTheQuerysTriggerId()
    {
        var client = new ReplyingTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);
        var handled = new ConcurrentQueue<(int Level, string? TriggerId)>();
        Trigger.Start();
        dsp.AddControl(level => handled.Enqueue((level, Trigger.Id)), GainName);

        await Wait.UntilAsync(_time, Step, () => handled.Any(x => x.Level > 0), "the level reply was never handled");

        var levelReply = handled.First(x => x.Level > 0);
        Assert.NotNull(levelReply.TriggerId);
        Assert.NotEqual(ReplyingTcpClient.ReceiveTriggerId, levelReply.TriggerId);
        Assert.Equal(client.SentUnder($"{GainName} get level 1\n"), levelReply.TriggerId);
    }

    [Fact]
    public async Task MatchedReply_ToAQueryAddedWithNoTriggerId_KeepsTheReceiveTriggerId()
    {
        const string unansweredName = "Unanswered";
        var client = new ReplyingTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);
        var handled = new ConcurrentQueue<(int Level, string? TriggerId)>();
        dsp.AddControl(_volumeLevelHandler.Object, unansweredName);
        await Wait.UntilAsync(_time, Step, () => client.Sent.Contains($"{unansweredName} get maxLevel 1\n"), "the unanswered query was never sent");

        Assert.Null(Trigger.Id);
        dsp.AddControl(level => handled.Enqueue((level, Trigger.Id)), GainName);

        await Wait.UntilAsync(_time, Step, () => handled.Any(x => x.Level > 0), "the level reply was never handled");
        Assert.Equal(ReplyingTcpClient.ReceiveTriggerId, handled.First(x => x.Level > 0).TriggerId);
    }

    [Fact]
    public void HandleResponse_AfterTheFaultListReply_HandlesTheRestOfTheLines()
    {
        var (client, dsp) = CreateConnectedDsp();
        _ = dsp.StartPoll();
        dsp.AddControl(_volumeLevelHandler.Object, GainName);

        client.ResponseHandlers?.Invoke($"{TesiraReplies.NoFaults}\n! \"publishToken\":\"AvCodersLevel-Gain-1\" \"value\":-2.000000\n");

        Assert.Equal(98, dsp.GetLevel(GainName));
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == FaultsIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenCrLfLines_StoresTheQueriedMuteState()
    {
        SetPrivateField(_dsp, "_currentQuery", new Query($"AvCodersMute-{MuteName}-1", BiampQuery.Mute, $"{MuteName} get mute 1\n"));

        _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":true\r\n");

        Assert.Equal(MuteState.On, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public async Task SetLevel_WhileAQueryIsInFlight_AsksTheQueryAgain()
    {
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();

        dsp.SetLevel(GainName, 50);
        await AnswerTheQueriesAfterTheInterruptedOne(client, dsp);

        Assert.Equal(2, client.Sent.Count(s => s == MaxLevelQuery));
    }

    [Fact]
    public async Task RecallPreset_WhileAQueryIsInFlight_AsksTheQueryAgain()
    {
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();

        dsp.RecallPreset(1001);
        await AnswerTheQueriesAfterTheInterruptedOne(client, dsp);

        Assert.Contains("DEVICE recallPreset 1001\n", client.Sent);
        Assert.Equal(2, client.Sent.Count(s => s == MaxLevelQuery));
    }

    [Fact]
    public async Task SetLevel_WhileAQueryIsInFlight_DropsItsLateReply()
    {
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();
        dsp.SetLevel(GainName, 50);
        await dsp.PollNow();

        client.ResponseHandlers?.Invoke(MaxLevelReply);
        dsp.SetLevel(GainName, 0);
        dsp.SetLevel(GainName, 100);

        Assert.DoesNotContain(MinLevelQuery, client.Sent);
        Assert.Contains($"{GainName} set level 1 -100\n", client.Sent);
        Assert.Contains($"{GainName} set level 1 0\n", client.Sent);
    }

    [Fact]
    public async Task SetLevel_WhileAQueryIsInFlight_AsksTheNextQueryOnceTheLateReplyArrives()
    {
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();
        dsp.SetLevel(GainName, 50);
        await dsp.PollNow();

        client.ResponseHandlers?.Invoke(MaxLevelReply);
        await dsp.PollNow();

        Assert.Equal(MinLevelQuery, client.Sent[^1]);
    }

    [Fact]
    public async Task HandleResponse_GivenAnErrorReply_RaisesAnIssueAndAsksTheNextQuery()
    {
        const string errorReply = "-ERR address not found:{\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}";
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();

        client.ResponseHandlers?.Invoke($"{errorReply}\r\n");
        await dsp.PollNow();

        Assert.Contains(MinLevelQuery, client.Sent);
        var issue = Assert.Single(dsp.GetIssues(), i => i.Key == "error-reply");
        Assert.Equal($"The Tesira returned an error to {MaxLevelQuery.Trim()}: {errorReply}", issue.Message);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == "unanswered-query");
    }

    [Fact]
    public async Task HandleResponse_GivenAnErrorToACommandAfterTheNextQueryWasSent_KeepsThatQuery()
    {
        const string errorReply = "-ERR INVALID_PARAMETER";
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();
        dsp.SetLevel(GainName, 50);
        client.ResponseHandlers?.Invoke(MaxLevelReply);
        await dsp.PollNow();

        client.ResponseHandlers?.Invoke($"{errorReply}\r\n");
        client.ResponseHandlers?.Invoke("+OK \"value\":-80.000000\r\n");
        dsp.SetLevel(GainName, 0);

        Assert.Contains($"{GainName} set level 1 -80\n", client.Sent);
        var issue = Assert.Single(dsp.GetIssues(), i => i.Key == "error-reply");
        Assert.Equal($"The Tesira returned {errorReply} to {GainName} set level 1 -50", issue.Message);
    }

    [Fact]
    public async Task HandleResponse_GivenASubscribesOkWhileACommandIsOutstanding_KeepsTheQuery()
    {
        const string errorReply = "-ERR INVALID_PARAMETER";
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();
        dsp.SetLevel(GainName, 50);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        client.ResponseHandlers?.Invoke($"! \"publishToken\":\"AvCodersMute-{MuteName}-1\" \"value\":false\r\n+OK\r\n");
        client.ResponseHandlers?.Invoke(MaxLevelReply);
        await dsp.PollNow();

        client.ResponseHandlers?.Invoke($"{errorReply}\r\n");
        client.ResponseHandlers?.Invoke("+OK \"value\":-80.000000\r\n");
        dsp.SetLevel(GainName, 0);

        Assert.Contains($"{MuteName} subscribe mute 1 AvCodersMute-{MuteName}-1\n", client.Sent);
        Assert.Contains($"{GainName} set level 1 -80\n", client.Sent);
    }

    [Fact]
    public async Task HandleResponse_AfterACommandIsAcknowledged_GivesAnErrorToTheQuery()
    {
        const string errorReply = "-ERR address not found";
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();
        dsp.SetLevel(GainName, 50);
        client.ResponseHandlers?.Invoke("+OK\r\n");
        client.ResponseHandlers?.Invoke(MaxLevelReply);
        await dsp.PollNow();

        client.ResponseHandlers?.Invoke($"{errorReply}\r\n");

        var issue = Assert.Single(dsp.GetIssues(), i => i.Key == "error-reply");
        Assert.Equal($"The Tesira returned an error to {MinLevelQuery.Trim()}: {errorReply}", issue.Message);
    }

    [Fact]
    public async Task HandleResponse_AfterACommandIsAcknowledged_GivesAnErrorToTheInterruptedQuery()
    {
        const string errorReply = "-ERR address not found";
        var (client, dsp) = CreatePollingDsp();
        await dsp.PollNow();
        dsp.SetLevel(GainName, 50);
        client.ResponseHandlers?.Invoke("+OK\r\n");

        client.ResponseHandlers?.Invoke($"{errorReply}\r\n");
        await dsp.PollNow();

        Assert.Equal(MinLevelQuery, client.Sent[^1]);
        var issue = Assert.Single(dsp.GetIssues(), i => i.Key == "error-reply");
        Assert.Equal($"The Tesira returned an error to {MaxLevelQuery.Trim()}: {errorReply}", issue.Message);
    }

    [Fact]
    public void HandleResponse_GivenAlreadySubscribedToASubscription_RaisesNoIssue()
    {
        var (client, dsp) = CreateConnectedDsp();
        dsp.AddControl(_volumeLevelHandler.Object, RecMicLevel, 1);
        Assert.Contains(RecMicLevelSubscription, client.Sent);

        client.ResponseHandlers?.Invoke(TesiraReplies.AlreadySubscribed);

        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == "error-reply");
    }

    [Fact]
    public async Task HandleResponse_AfterAlreadySubscribed_GivesTheNextErrorToTheQuery()
    {
        const string errorReply = "-ERR address not found";
        var (client, dsp) = CreatePollingDsp();
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        client.ResponseHandlers?.Invoke(TesiraReplies.AlreadySubscribed);
        await dsp.PollNow();

        client.ResponseHandlers?.Invoke($"{errorReply}\r\n");

        var issue = Assert.Single(dsp.GetIssues(), i => i.Key == "error-reply");
        Assert.Equal($"The Tesira returned an error to {MaxLevelQuery.Trim()}: {errorReply}", issue.Message);
    }

    [Fact]
    public void HandleResponse_GivenTheVersionReply_ShowsTheFirmware()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, FirmwareQuery, VersionReply);

        Assert.Equal("5.2.0.8", Detail(dsp, "Firmware")?.Value);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == FirmwareIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenTheSerialNumberReply_ShowsTheSerial()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, SerialQuery, SerialReply);

        Assert.Equal("04351854", Detail(dsp, "Serial")?.Value);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == SerialIssueKey);
    }

    [Theory]
    [InlineData(BiampQuery.Firmware, FirmwareQueryCommand, TesiraReplies.UnsupportedDeviceAttribute, new[] { "Firmware" })]
    [InlineData(BiampQuery.Serial, SerialQueryCommand, TesiraReplies.UnsupportedDeviceAttribute, new[] { "Serial" })]
    [InlineData(BiampQuery.DeviceInfo, DeviceInfoQueryCommand, TesiraReplies.AttributeNotSupported, new[] { "Model", "Revision" })]
    [InlineData(BiampQuery.NetworkStatus, NetworkStatusQueryCommand, TesiraReplies.AttributeNotSupported, new[] { "Hostname", "DNS" })]
    [InlineData(BiampQuery.Aliases, AliasesQueryCommand, TesiraReplies.UnsupportedSessionAttribute, new[] { "Missing Controls" })]
    public void HandleResponse_GivenANotSupportedErrorToADeviceQuery_ShowsItIsNotSupportedWithNoIssue(BiampQuery query, string command, string error, string[] labels)
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);
        var communicationState = dsp.CommunicationState;

        AnswerTheQuery(client, dsp, new Query("DEVICE", query, command), error);

        foreach (var label in labels)
            Assert.Equal(new DeviceDetail(label, $"Not supported: {error}", DetailTone.Normal), Detail(dsp, label));
        Assert.Empty(dsp.GetIssues());
        Assert.Equal(communicationState, dsp.CommunicationState);
    }

    [Theory]
    [InlineData("-ERR INVALID_PARAMETER")]
    [InlineData("-ERR address not found:{\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}")]
    [InlineData("-ERR GENERAL_FAILURE")]
    public void HandleResponse_GivenAnyOtherErrorToADeviceQuery_CountsAMissAndRaisesTheReadIssueOnTheThird(string error)
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, FirmwareQuery, error);
        AnswerTheQuery(client, dsp, FirmwareQuery, error);

        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == FirmwareIssueKey);

        AnswerTheQuery(client, dsp, FirmwareQuery, error);

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == FirmwareIssueKey);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal($"The Tesira did not give a real reply to {FirmwareQueryCommand.Trim()} 3 times in a row, the last was {error}", issue.Message);
        Assert.Null(Detail(dsp, "Firmware"));
    }

    [Theory]
    [InlineData(BiampQuery.Firmware, FirmwareQueryCommand, FirmwareIssueKey, NotAStringReply, VersionReply, "Firmware")]
    [InlineData(BiampQuery.DeviceInfo, DeviceInfoQueryCommand, DeviceInfoIssueKey, NoModelReply, TesiraReplies.DeviceInfo, "Model")]
    [InlineData(BiampQuery.NetworkStatus, NetworkStatusQueryCommand, NetworkStatusIssueKey, NoInterfacesReply, TesiraReplies.NetworkStatus, "Hostname")]
    [InlineData(BiampQuery.Aliases, AliasesQueryCommand, AliasesIssueKey, NotAStringReply, TesiraReplies.Aliases, "Missing Controls")]
    public void HandleResponse_GivenThreeUnreadableDeviceRepliesInARow_ShowsTheRawLineAndRaisesTheReadIssueOnTheThird(
        BiampQuery query, string command, string issueKey, string unreadable, string goodReply, string label)
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);
        var deviceQuery = new Query("DEVICE", query, command);

        AnswerTheQuery(client, dsp, deviceQuery, unreadable);
        AnswerTheQuery(client, dsp, deviceQuery, unreadable);

        Assert.Equal(new DeviceDetail(label, unreadable, DetailTone.Warning), Detail(dsp, label));
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == issueKey);

        AnswerTheQuery(client, dsp, deviceQuery, unreadable);

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == issueKey);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal($"The Tesira did not give a real reply to {command.Trim()} 3 times in a row, the last was {unreadable}", issue.Message);

        AnswerTheQuery(client, dsp, deviceQuery, goodReply);

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == issueKey);
    }

    [Fact]
    public void HandleResponse_GivenAnEmptySerialNumber_ShowsTheRawLineWithNoIssue()
    {
        const string emptyReply = "+OK \"value\":\"\"";
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, SerialQuery, emptyReply);

        Assert.Equal(new DeviceDetail("Serial", emptyReply), Detail(dsp, "Serial"));
        Assert.Empty(dsp.GetIssues());
    }

    [Fact]
    public void HandleResponse_GivenAnErrorAfterTheSerial_KeepsTheLastSerial()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, SerialQuery, SerialReply);
        AnswerTheQuery(client, dsp, SerialQuery, GeneralFailureError);

        Assert.Equal("04351854", Detail(dsp, "Serial")?.Value);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == SerialIssueKey);
    }

    [Theory]
    [InlineData(BiampQuery.Firmware, FirmwareQueryCommand, FirmwareIssueKey, VersionReply)]
    [InlineData(BiampQuery.Serial, SerialQueryCommand, SerialIssueKey, SerialReply)]
    [InlineData(BiampQuery.DeviceInfo, DeviceInfoQueryCommand, DeviceInfoIssueKey, TesiraReplies.DeviceInfo)]
    [InlineData(BiampQuery.NetworkStatus, NetworkStatusQueryCommand, NetworkStatusIssueKey, TesiraReplies.NetworkStatus)]
    [InlineData(BiampQuery.Aliases, AliasesQueryCommand, AliasesIssueKey, TesiraReplies.Aliases)]
    public void HandleResponse_GivenAGoodReplyAfterAnError_ResolvesTheIssue(BiampQuery query, string command, string issueKey, string reply)
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        for (int i = 0; i < 3; i++)
            AnswerTheQuery(client, dsp, new Query("DEVICE", query, command), GeneralFailureError);
        AnswerTheQuery(client, dsp, new Query("DEVICE", query, command), reply);

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == issueKey);
        Assert.Contains(dsp.GetIssues(), i => i.Key == issueKey && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void HandleResponse_GivenTheSerialAfterAFirmwareError_KeepsTheFirmwareIssue()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        for (int i = 0; i < 3; i++)
            AnswerTheQuery(client, dsp, FirmwareQuery, GeneralFailureError);
        AnswerTheQuery(client, dsp, SerialQuery, SerialReply);

        Assert.Contains(dsp.GetOngoingIssues(), i => i.Key == FirmwareIssueKey);
    }

    [Fact]
    public async Task ConnectingAndReinitialising_ReadTheDeviceEachTime()
    {
        string[] deviceQueries = [FirmwareQueryCommand, SerialQueryCommand, DeviceInfoQueryCommand, NetworkStatusQueryCommand, AliasesQueryCommand];
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new TestableBiampTtp(client, _time);
        client.SetConnectionState(ConnectionState.Connected);

        await dsp.PollNow();
        await ReadTheDevice(client, dsp, VersionReply);
        Assert.Equal(deviceQueries, client.Sent);
        await Finish(dsp.Reinitialise());
        await ReadTheDevice(client, dsp, OlderVersionReply);

        Assert.Equal(deviceQueries.Concat(deviceQueries), client.Sent);
        Assert.Equal("4.2.1.23", Detail(dsp, "Firmware")?.Value);
        Assert.Equal("04351854", Detail(dsp, "Serial")?.Value);
        Assert.Equal("TesiraFORTÉ AVB VT", Detail(dsp, "Model")?.Value);
        Assert.Equal("TesiraForte04351854", Detail(dsp, "Hostname")?.Value);
    }

    [Fact]
    public void HandleResponse_GivenTheDeviceInfoReply_ShowsTheModelAndRevision()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, DeviceInfoQuery, TesiraReplies.DeviceInfo);

        Assert.Equal("TesiraFORTÉ AVB VT", Detail(dsp, "Model")?.Value);
        Assert.Equal("Rev. A", Detail(dsp, "Revision")?.Value);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == DeviceInfoIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenTheNetworkStatusReply_ShowsTheHostnameInterfacesAndDns()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, NetworkStatusQuery, TesiraReplies.NetworkStatus);

        Assert.Equal("TesiraForte04351854", Detail(dsp, "Hostname")?.Value);
        Assert.Equal("LINK_1_GB, 10.1.3.105/255.255.252.0, gateway 10.1.0.1, DHCP, MAC 78:45:01:0a:ee:9f", Detail(dsp, "control")?.Value);
        Assert.Equal("LINK_DOWN, 169.254.7.24/255.255.0.0, gateway 0.0.0.0, DHCP, MAC 78:45:01:0a:ee:a0", Detail(dsp, "media_avb_0")?.Value);
        Assert.Equal("primary 10.1.0.1, secondary , domain millpark.acarbone.com", Detail(dsp, "DNS")?.Value);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == NetworkStatusIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenAnInterfaceThisDriverDoesNotRecognise_ShowsItsRawText()
    {
        const string oddInterfaceReply = """+OK "value":{"hostname":"TesiraForte04351854" "networkInterfaceStatusWithName":[{"interfaceId":"control" "networkInterfaceStatus":{"ip":"10.1.3.105" "linkStatus":LINK_1_GB}}] "dnsStatus":{"primaryDNSServer":"10.1.0.1" "secondaryDNSServer":"" "domainName":"millpark.acarbone.com"}}""";
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, NetworkStatusQuery, oddInterfaceReply);

        Assert.Equal("""{"interfaceId":"control","networkInterfaceStatus":{"ip":"10.1.3.105","linkStatus":"LINK_1_GB"}}""", Detail(dsp, "control")?.Value);
    }

    [Fact]
    public void HandleResponse_GivenTheAliasesWithEveryInstanceTag_ShowsNoMissingControls()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);
        _ = new BiampVolumeControl(new BiampAudioBlockInfo("Level 1", "InputLevels", 1), VolumeType.Speaker, dsp);

        AnswerTheQuery(client, dsp, AliasesQuery, TesiraReplies.Aliases);

        Assert.Null(Detail(dsp, "Missing Controls"));
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key.StartsWith("bad-control:"));
    }

    [Fact]
    public void HandleResponse_GivenTheAliasesWithoutAnInstanceTag_ShowsItAsMissing()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        _ = new BiampVolumeControl(new BiampAudioBlockInfo("Level 1", "InputLevels", 1), VolumeType.Speaker, dsp);

        AnswerTheQuery(client, dsp, AliasesQuery, TesiraReplies.Aliases);

        Assert.Equal(new DeviceDetail("Missing Controls", $"{GainName}, {MuteName}", DetailTone.Warning), Detail(dsp, "Missing Controls"));
        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == $"bad-control:{GainName}");
        Assert.Equal($"The Tesira's configuration has no instance tag {GainName}", issue.Message);
        Assert.Contains(dsp.GetOngoingIssues(), i => i.Key == $"bad-control:{MuteName}");
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == "bad-control:InputLevels");
    }

    [Fact]
    public void HandleResponse_GivenTheAliasesAfterAConfigLoadRestoresAnInstanceTag_ResolvesIt()
    {
        const string restoredAliases = $$"""+OK "list":["Aec1" "DEVICE" "{{GainName}}" "InputLevels" "{{MuteName}}"]""";
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        AnswerTheQuery(client, dsp, AliasesQuery, TesiraReplies.Aliases);

        AnswerTheQuery(client, dsp, AliasesQuery, restoredAliases);

        Assert.Null(Detail(dsp, "Missing Controls"));
        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key.StartsWith("bad-control:"));
        Assert.Contains(dsp.GetIssues(), i => i.Key == $"bad-control:{GainName}" && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void KeepAlive_GivenAFault_RaisesAnOngoingIssueForIt()
    {
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, FanFaultReply);

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == FanFaultIssueKey);
        Assert.Equal("Cooling fan malfunction.  Check filter and fan. (FAULT_FAN_MALFUNCTION) on 04351854, Minor Fault in Device (INDICATOR_MINOR_IN_DEVICE)", issue.Message);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
    }

    [Fact]
    public void KeepAlive_GivenFaultsOnAnExpander_RaisesAnIssuePerDevice()
    {
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, ExpanderFaultReply);

        Assert.Contains(dsp.GetOngoingIssues(), i => i.Key == FanFaultIssueKey && i.Severity == IssueSeverity.Minor);
        var expanderIssue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == "fault:01882935:FAULT_DSP_CARD");
        Assert.Equal("One or more DSP cards failed to boot (FAULT_DSP_CARD) on 01882935, Major Fault in Device (INDICATOR_MAJOR_IN_DEVICE)", expanderIssue.Message);
        Assert.Equal(IssueSeverity.Major, expanderIssue.Severity);
    }

    [Fact]
    public void KeepAlive_GivenAFaultHasCleared_ResolvesItsIssue()
    {
        var (client, dsp) = CreateConnectedDsp();
        AnswerTheKeepAlive(client, dsp, ExpanderFaultReply);

        AnswerTheKeepAlive(client, dsp, FanFaultReply);

        Assert.Contains(dsp.GetOngoingIssues(), i => i.Key == FanFaultIssueKey);
        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == "fault:01882935:FAULT_DSP_CARD");
        Assert.Contains(dsp.GetIssues(), i => i.Key == "fault:01882935:FAULT_DSP_CARD" && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void KeepAlive_GivenAFaultEntryThisDriverDoesNotRecognise_RaisesItsRawText()
    {
        const string noSerialReply = """+OK "value":[{"id":INDICATOR_MINOR_IN_DEVICE "faults":[]}]""";
        const string rawEntry = """{"id":"INDICATOR_MINOR_IN_DEVICE","faults":[]}""";
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, noSerialReply);

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == $"fault:{rawEntry}");
        Assert.Equal($"The Tesira reported a fault entry this driver does not recognise: {rawEntry}", issue.Message);
    }

    [Fact]
    public void KeepAlive_GivenAFaultListThatIsNotAListThreeTimesInARow_RaisesTheReadIssueAndStillCountsAsAlive()
    {
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, VersionReply);
        AnswerTheKeepAlive(client, dsp, VersionReply);

        Assert.Equal(new DeviceDetail("Active Faults", VersionReply, DetailTone.Warning), Detail(dsp, "Active Faults"));
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == FaultsIssueKey);

        AnswerTheKeepAlive(client, dsp, VersionReply);

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == FaultsIssueKey);
        Assert.Equal($"The Tesira did not give a real reply to DEVICE get activeFaultList 3 times in a row, the last was {VersionReply}", issue.Message);
        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);
    }

    [Fact]
    public void KeepAlive_GivenANotSupportedError_ShowsItIsNotSupportedWithNoIssueAndCountsAsAlive()
    {
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, TesiraReplies.AttributeNotSupported);

        Assert.Equal(new DeviceDetail("Active Faults", $"Not supported: {TesiraReplies.AttributeNotSupported}", DetailTone.Normal),
            Detail(dsp, "Active Faults"));
        Assert.NotNull(Detail(dsp, "Reply Time"));
        Assert.Empty(dsp.GetIssues());
        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);
    }

    [Fact]
    public void KeepAlive_GivenAnyOtherErrorThreeTimesInARow_RaisesTheReadIssueWithTheRawLine()
    {
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, GeneralFailureError);
        AnswerTheKeepAlive(client, dsp, GeneralFailureError);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == FaultsIssueKey);
        AnswerTheKeepAlive(client, dsp, GeneralFailureError);

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == FaultsIssueKey);
        Assert.Equal($"The Tesira did not give a real reply to DEVICE get activeFaultList 3 times in a row, the last was {GeneralFailureError}", issue.Message);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == "error-reply");
    }

    [Fact]
    public void KeepAlive_GivenAFaultListAfterAnError_ResolvesTheIssue()
    {
        var (client, dsp) = CreateConnectedDsp();
        for (int i = 0; i < 3; i++)
            AnswerTheKeepAlive(client, dsp, GeneralFailureError);

        AnswerTheKeepAlive(client, dsp, TesiraReplies.NoFaults);

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == FaultsIssueKey);
        Assert.Contains(dsp.GetIssues(), i => i.Key == FaultsIssueKey && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void KeepAlive_WithNoPendingQueries_AsksForTheFaultList()
    {
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, TesiraReplies.NoFaults);

        Assert.Equal(FaultsQueryCommand, Assert.Single(client.Sent));
        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == "unanswered-query");
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key.StartsWith("fault"));
    }

    [Fact]
    public void KeepAlive_GivenAFastReply_ShowsTheReplyTimeAndRaisesNoIssue()
    {
        var (client, dsp) = CreateConnectedDsp();

        AnswerTheKeepAlive(client, dsp, TesiraReplies.NoFaults);

        Assert.Equal(new DeviceDetail("Reply Time", "0 ms", DetailTone.Normal), Detail(dsp, "Reply Time"));
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == SlowRepliesIssueKey);
    }

    [Fact]
    public async Task KeepAlive_GivenASlowReply_RaisesAnIssueWithTheTimeAndFirmware()
    {
        var (client, dsp) = CreateConnectedDsp();
        AnswerTheQuery(client, dsp, FirmwareQuery, VersionReply);

        await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == SlowRepliesIssueKey);
        Assert.Equal("The Tesira took 700 ms to answer DEVICE get activeFaultList, over the 500 ms limit. Firmware: 5.2.0.8", issue.Message);
        Assert.Equal(new DeviceDetail("Reply Time", "700 ms", DetailTone.Warning), Detail(dsp, "Reply Time"));
    }

    [Fact]
    public async Task KeepAlive_GivenAFastReplyAfterASlowOne_ResolvesTheSlowIssue()
    {
        var (client, dsp) = CreateConnectedDsp();
        await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);

        AnswerTheKeepAlive(client, dsp, TesiraReplies.NoFaults);

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == SlowRepliesIssueKey);
        Assert.Contains(dsp.GetIssues(), i => i.Key == SlowRepliesIssueKey && i.Status == IssueStatus.Resolved);
        Assert.Equal(DetailTone.Normal, Detail(dsp, "Reply Time")?.Tone);
    }

    [Fact]
    public async Task KeepAlive_GivenNoReply_IsFlaggedAtTheEndOfTheWindow()
    {
        var (client, dsp) = CreateConnectedDsp();

        var poll = dsp.StartPoll();
        _time.Advance(KeepAliveReplyWindow - OneTick);
        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == "unanswered-query");
        _time.Advance(OneTick);
        await Wait.UntilAsync(() => dsp.GetIssues().Any(i => i.Key == "unanswered-query"), "the missed keep-alive was never flagged");
        await FinishTheKeepAlive(_time, poll);

        var issue = Assert.Single(dsp.GetIssues(), i => i.Key == "unanswered-query");
        Assert.Equal("The query DEVICE get activeFaultList was not answered within 1000 ms", issue.Message);
        Assert.Equal(FaultsQueryCommand, Assert.Single(client.Sent));
        Assert.Equal(0, client.Reconnects);
    }

    [Fact]
    public async Task KeepAlive_GivenMissesInARow_Reconnects()
    {
        var (client, dsp) = CreateConnectedDsp();
        SetPrivateField<BiampTtp>(dsp, "_missedKeepAlives", BiampTtp.MaxMissedKeepAlives - 1);

        await FinishTheKeepAlive(_time, dsp.StartPoll());

        Assert.Equal(1, client.Reconnects);
        Assert.Contains(dsp.Events, e => e.Info == "Reconnecting: 3 keep-alives in a row were not answered within 1000 ms");
    }

    [Fact]
    public async Task KeepAlive_GivenAFastReply_StartsTheMissedCountAgain()
    {
        var (client, dsp) = CreateConnectedDsp();
        SetPrivateField<BiampTtp>(dsp, "_missedKeepAlives", BiampTtp.MaxMissedKeepAlives - 1);
        await AnswerTheKeepAliveAfter(_time, client, dsp, TimeSpan.Zero);

        await FinishTheKeepAlive(_time, dsp.StartPoll());

        Assert.Contains(dsp.GetIssues(), i => i.Key == "unanswered-query");
        Assert.Equal(0, client.Reconnects);
    }

    [Fact]
    public async Task KeepAlive_GivenAFastReply_StartsTheSlowCountAgain()
    {
        var (client, dsp) = CreateConnectedDsp();

        await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);
        await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);
        await AnswerTheKeepAliveAfter(_time, client, dsp, TimeSpan.Zero);
        await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);
        await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);

        Assert.Equal(0, client.Reconnects);
    }

    [Fact]
    public async Task KeepAlive_AfterAReconnect_StartsTheSlowCountAgain()
    {
        var (client, dsp) = CreateConnectedDsp();
        for (int i = 0; i < BiampTtp.MaxSlowKeepAlives; i++)
            await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);
        Assert.Equal(1, client.Reconnects);

        for (int i = 1; i < BiampTtp.MaxSlowKeepAlives; i++)
            await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);

        Assert.Equal(1, client.Reconnects);
    }

    [Theory]
    [InlineData("3.18.0.12", "Firmware 3.18.0.12 is below 4.11, so it is not supported", IssueSeverity.Major)]
    [InlineData("4.2", "Firmware 4.2 is below 4.11, so it is not supported", IssueSeverity.Major)]
    [InlineData("4.10.9", "Firmware 4.10.9 is below 4.11, so it is not supported", IssueSeverity.Major)]
    [InlineData("5.2.0.8", "Firmware 5.2.0.8 is below 5.7, the minimum for version 5", IssueSeverity.Minor)]
    [InlineData("5.6.99", "Firmware 5.6.99 is below 5.7, the minimum for version 5", IssueSeverity.Minor)]
    [InlineData("5.2.x", "Firmware 5.2.x is not a version number this driver can check", IssueSeverity.Major)]
    public void HandleResponse_GivenUnhealthyFirmware_RaisesAnIssue(string firmware, string message, IssueSeverity severity)
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, FirmwareQuery, $"+OK \"value\":\"{firmware}\"");

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == FirmwareHealthIssueKey);
        Assert.Equal(message, issue.Message);
        Assert.Equal(severity, issue.Severity);
        Assert.Equal(firmware, Detail(dsp, "Firmware")?.Value);
    }

    [Theory]
    [InlineData("4.11.0")]
    [InlineData("4.15")]
    [InlineData("5.7.0")]
    [InlineData("6.0")]
    public void HandleResponse_GivenHealthyFirmware_RaisesNoIssue(string firmware)
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);

        AnswerTheQuery(client, dsp, FirmwareQuery, $"+OK \"value\":\"{firmware}\"");

        Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == FirmwareHealthIssueKey);
    }

    [Fact]
    public void HandleResponse_GivenHealthyFirmwareAfterUnhealthy_ResolvesTheIssue()
    {
        var client = new TestTcpClient();
        var dsp = new TestableBiampTtp(client, _time);
        AnswerTheQuery(client, dsp, FirmwareQuery, VersionReply);

        AnswerTheQuery(client, dsp, FirmwareQuery, "+OK \"value\":\"5.7.0\"");

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == FirmwareHealthIssueKey);
        Assert.Contains(dsp.GetIssues(), i => i.Key == FirmwareHealthIssueKey && i.Status == IssueStatus.Resolved);
    }

    private BiampTtp Create(TcpClient client) => new(client, "Test", PollIntervalMs, FaderCurve.Linear, _time);

    private async Task Finish(Task task)
    {
        await Wait.UntilAsync(_time, Step, () => task.IsCompleted, "the task never finished");
        await task;
    }

    private (TestTcpClient Client, TestableBiampTtp Dsp) CreatePollingDsp()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new TestableBiampTtp(client, _time);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        SetPrivateField<CommunicationClient>(client, "_connectionState", ConnectionState.Connected);
        return (client, dsp);
    }

    private (TestTcpClient Client, TestableBiampTtp Dsp) CreateConnectedDsp()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new TestableBiampTtp(client, _time);
        SetPrivateField<CommunicationClient>(client, "_connectionState", ConnectionState.Connected);
        return (client, dsp);
    }

    private static async Task AnswerTheQueriesAfterTheInterruptedOne(TestTcpClient client, TestableBiampTtp dsp)
    {
        client.ResponseHandlers?.Invoke(MaxLevelReply);
        await dsp.PollNow();
        client.ResponseHandlers?.Invoke(MinLevelReply);
        await dsp.PollNow();
        client.ResponseHandlers?.Invoke(LevelReply);
        await dsp.PollNow();
    }

    private static void AnswerTheQuery(TestTcpClient client, TestableBiampTtp dsp, Query query, string reply)
    {
        SetPrivateField<BiampTtp>(dsp, "_currentQuery", query);
        client.ResponseHandlers?.Invoke($"{reply}\r\n");
    }

    private static async Task ReadTheDevice(TestTcpClient client, TestableBiampTtp dsp, string versionReply)
    {
        string[] replies = [versionReply, SerialReply, TesiraReplies.DeviceInfo, TesiraReplies.NetworkStatus, TesiraReplies.Aliases];
        foreach (var reply in replies)
        {
            await dsp.PollNow();
            client.ResponseHandlers?.Invoke($"{reply}\r\n");
        }
    }

    private static void AnswerTheKeepAlive(TestTcpClient client, TestableBiampTtp dsp, string reply)
    {
        _ = dsp.StartPoll();
        client.ResponseHandlers?.Invoke($"{reply}\r\n");
    }

    private static async Task AnswerTheKeepAliveAfter(FakeTimeProvider time, TestTcpClient client, TestableBiampTtp dsp, TimeSpan replyTime)
    {
        var poll = dsp.StartPoll();
        time.Advance(replyTime);
        client.ResponseHandlers?.Invoke($"{TesiraReplies.NoFaults}\r\n");
        await FinishTheKeepAlive(time, poll);
    }

    private static async Task FinishTheKeepAlive(FakeTimeProvider time, Task poll)
    {
        await Wait.UntilAsync(time, KeepAliveInterval, () => poll.IsCompleted, "the keep-alive poll never finished");
        await poll;
    }

    private static DeviceDetail? Detail(BiampTtp dsp, string label) => dsp.Details.SingleOrDefault(d => d.Label == label);

    private static void SetPrivateField<T>(T target, string fieldName, object value) =>
        typeof(T).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static bool IsSubscribe(string message) => message.Contains(" subscribe ");

    private class TestableBiampTtp : BiampTtp
    {
        private readonly FakeTimeProvider _time;

        public TestableBiampTtp(TcpClient client, FakeTimeProvider time) : base(client, "Test", NeverPollMs, FaderCurve.Linear, time)
        {
            _time = time;
        }

        public Task StartPoll() => Poll(CancellationToken.None);

        public async Task PollNow()
        {
            var poll = StartPoll();
            await Wait.UntilAsync(_time, Step, () => poll.IsCompleted, "the poll never finished");
            await poll;
        }
    }

    private class ReplyingTcpClient : TestTcpClient
    {
        public const string ReceiveTriggerId = "0000000f";
        private readonly ConcurrentDictionary<string, string?> _sentUnder = new();
        private readonly Dictionary<string, string> _replies = new()
        {
            [$"{GainName} get maxLevel 1\n"] = "+OK \"value\":12.000000\n",
            [$"{GainName} get minLevel 1\n"] = "+OK \"value\":0.000000\n",
            [$"{GainName} get level 1\n"] = "+OK \"value\":3.000000\n",
            [FirmwareQueryCommand] = $"{TesiraReplies.Version}\n",
            [SerialQueryCommand] = $"{TesiraReplies.SerialNumber}\n",
            [DeviceInfoQueryCommand] = $"{TesiraReplies.DeviceInfo}\n",
            [NetworkStatusQueryCommand] = $"{TesiraReplies.NetworkStatus}\n",
            [AliasesQueryCommand] = $"{TesiraReplies.Aliases}\n",
            [FaultsQueryCommand] = $"{TesiraReplies.NoFaults}\n"
        };

        public string? SentUnder(string message) => _sentUnder[message];

        public override void Send(string message)
        {
            base.Send(message);
            _sentUnder[message] = Trigger.Id;
            if (!_replies.TryGetValue(message, out var reply))
                return;
            using (Trigger.Resume(ReceiveTriggerId))
                ResponseHandlers?.Invoke(reply);
        }
    }

    [Collection(LoggerFactoryCollection.Name)]
    public class LogTest
    {
        private readonly FakeTimeProvider _time = new();
        private readonly ScopeCapturingLoggerFactory _logs = new();
        private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
        private readonly Mock<StringValueHandler> _stringValueHandler = new();

        [Fact]
        public async Task PolledQuery_LogsUnderTheQuerysTriggerId()
        {
            var client = new ReplyingTcpClient();
            client.SetConnectionState(ConnectionState.Connected);
            var dsp = Logged.Create(_logs, () => new BiampTtp(client, "Test", PollIntervalMs, FaderCurve.Linear, _time));
            var levelQuery = $"{GainName} get level 1\n";
            Trigger.Start();
            dsp.AddControl(_volumeLevelHandler.Object, GainName);

            await Wait.UntilAsync(_time, Step, () => _logs.Logs.Any(x => x.Message == $"Polling for {levelQuery}"), "the level query was never polled");

            Assert.Equal(client.SentUnder(levelQuery), _logs.Logs.First(x => x.Message == $"Polling for {levelQuery}").TriggerId);
        }

        [Fact]
        public async Task SetLevel_WhileAQueryIsInFlight_AsksTheNextQueryAfterTheInterruptedReplyTimeout()
        {
            var client = new TestTcpClient();
            client.SetConnectionState(ConnectionState.Disconnected);
            var dsp = Logged.Create(_logs, () => new TestableBiampTtp(client, _time));
            dsp.AddControl(_volumeLevelHandler.Object, GainName);
            SetPrivateField<CommunicationClient>(client, "_connectionState", ConnectionState.Connected);
            await dsp.PollNow();
            dsp.SetLevel(GainName, 50);
            await dsp.PollNow();
            Assert.DoesNotContain(MinLevelQuery, client.Sent);

            _time.Advance(InterruptedReplyTimeout);
            await dsp.PollNow();

            Assert.Equal(MinLevelQuery, client.Sent[^1]);
            Assert.Contains(_logs.Logs, x => x.Message == $"The interrupted query {MaxLevelQuery} was not answered, polling the next query");
        }

        [Fact]
        public async Task KeepAlive_GivenSlowRepliesInARow_ReconnectsAndLogsTheTimes()
        {
            var (client, dsp) = CreateConnectedDsp();

            for (int i = 1; i < BiampTtp.MaxSlowKeepAlives; i++)
                await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);
            Assert.Equal(0, client.Reconnects);
            await AnswerTheKeepAliveAfter(_time, client, dsp, SlowReply);

            Assert.Equal(1, client.Reconnects);
            Assert.Contains(_logs.Logs, x => x.Message == "The Tesira answered 3 keep-alives in a row slower than 500 ms (700 ms, 700 ms, 700 ms), reconnecting");
            Assert.Contains(dsp.Events, e => e.Info == "Reconnecting: 3 keep-alive replies in a row were slower than 500 ms (700 ms, 700 ms, 700 ms)");
        }

        [Fact]
        public async Task KeepAlive_GivenALateReply_DoesNotGiveItToTheNextQuery()
        {
            var (client, dsp) = CreateConnectedDsp();
            _ = dsp.StartPoll();
            _time.Advance(KeepAliveReplyWindow);
            await Wait.UntilAsync(() => dsp.GetIssues().Any(i => i.Key == "unanswered-query"), "the keep-alive was never counted as missed");

            dsp.AddControl(_volumeLevelHandler.Object, GainName);
            _ = dsp.StartPoll();
            Assert.Equal(FaultsQueryCommand, client.Sent[^1]);
            Assert.DoesNotContain(MaxLevelQuery, client.Sent);

            client.ResponseHandlers?.Invoke($"{TesiraReplies.NoFaults}\r\n+OK\r\n{TesiraReplies.NoFaults}\r\n");
            _ = dsp.StartPoll();

            Assert.Equal(MaxLevelQuery, client.Sent[^1]);
            Assert.Contains(_logs.Logs, x => x.Message == "The DEVICE get activeFaultList reply took 1000 ms, so it was already counted as missed");
            Assert.DoesNotContain(dsp.GetIssues(), i => i.Key == "error-reply");
        }

        [Fact]
        public void AlreadySubscribed_LogsAtVerboseUnderTheSubscriptionsTriggerId()
        {
            var (client, dsp) = CreateConnectedDsp();
            var subscribeTrigger = Trigger.Start();
            dsp.AddControl(_volumeLevelHandler.Object, RecMicLevel, 1);

            using (Trigger.Resume(ReplyingTcpClient.ReceiveTriggerId))
                client.ResponseHandlers?.Invoke(TesiraReplies.AlreadySubscribed);

            var log = Assert.Single(_logs.Logs,
                x => x.Message == $"The subscription {RecMicLevelSubscription.Trim()} is still in place: {TesiraReplies.AlreadySubscribed}");
            Assert.Equal(LogLevel.Trace, log.Level);
            Assert.Equal(subscribeTrigger, log.TriggerId);
            Assert.DoesNotContain(_logs.Logs, x => x.Level >= LogLevel.Warning);
        }

        [Fact]
        public void OkReply_LogsAtVerboseUnderTheCommandsTriggerId()
        {
            var (client, dsp) = CreateConnectedDsp();
            var subscribeTrigger = Trigger.Start();
            dsp.AddControl(_volumeLevelHandler.Object, RecMicLevel, 1);

            using (Trigger.Resume(ReplyingTcpClient.ReceiveTriggerId))
                client.ResponseHandlers?.Invoke(TesiraReplies.Ok);

            var log = Assert.Single(_logs.Logs, x => x.Message == $"The Tesira answered {RecMicLevelSubscription.Trim()} with {TesiraReplies.Ok}");
            Assert.Equal(LogLevel.Trace, log.Level);
            Assert.Equal(subscribeTrigger, log.TriggerId);
        }

        [Fact]
        public void ErrorReplyToACommand_LogsTheIssueUnderTheCommandsTriggerId()
        {
            const string errorReply = "-ERR INVALID_PARAMETER Value out of range: attr:level min:-100 max:12 val:20";
            var (client, dsp) = CreateConnectedDsp();
            dsp.AddControl(_volumeLevelHandler.Object, GainName);
            client.ResponseHandlers?.Invoke(TesiraReplies.Ok);
            var setTrigger = Trigger.Start();
            dsp.SetLevel(GainName, 50);

            using (Trigger.Resume(ReplyingTcpClient.ReceiveTriggerId))
                client.ResponseHandlers?.Invoke(errorReply);

            var log = Assert.Single(_logs.Logs, x => x.Message == $"The Tesira returned {errorReply} to {GainName} set level 1 -50");
            Assert.Equal(LogLevel.Warning, log.Level);
            Assert.Equal(setTrigger, log.TriggerId);
            Assert.Single(dsp.GetIssues(), i => i.Key == "error-reply" && i.Status == IssueStatus.Momentary);
        }

        [Fact]
        public void StringValues_AreNotSupported_AndSayWhy()
        {
            var client = new TestTcpClient();
            var dsp = Logged.Create(_logs, () => new TestableBiampTtp(client, _time));

            dsp.AddControl(_stringValueHandler.Object, StringName);
            dsp.SetValue(StringName, "5");
            var value = dsp.GetValue(StringName);

            Assert.Equal("", value);
            Assert.Empty(client.Sent);
            Assert.Contains(_logs.Logs, x => x.Message == $"Test does not support string values, {StringName} will never be reported");
            Assert.Contains(_logs.Logs, x => x.Message == $"Test does not support string values, not setting {StringName} to 5");
            Assert.Contains(_logs.Logs, x => x.Message == $"Test does not support string values, returning an empty value for {StringName}");
        }

        [Fact]
        public void Commands_GivenAnUnknownControl_SendNothingAndLogTheValues()
        {
            var client = new TestTcpClient();
            var dsp = Logged.Create(_logs, () => new TestableBiampTtp(client, _time));

            dsp.SetLevel(UnknownControl, 40);
            dsp.LevelUp(UnknownControl, 3);
            dsp.LevelDown(UnknownControl, 2);
            dsp.ToggleAudioMute(UnknownControl);

            Assert.Empty(client.Sent);
            Assert.Contains(_logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, not setting it to 40");
            Assert.Contains(_logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, not raising it by 3");
            Assert.Contains(_logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, not lowering it by 2");
            Assert.Contains(_logs.Logs, x => x.Message == $"Unknown mute {UnknownControl} at index 1, not toggling it");
        }

        [Fact]
        public void Gets_GivenAnUnknownControl_ReturnTheDefaultAndLog()
        {
            var dsp = Logged.Create(_logs, () => new TestableBiampTtp(new TestTcpClient(), _time));

            Assert.Equal(0, dsp.GetLevel(UnknownControl));
            Assert.Equal(MuteState.Unknown, dsp.GetAudioMute(UnknownControl));
            Assert.Contains(_logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, returning 0");
            Assert.Contains(_logs.Logs, x => x.Message == $"Unknown mute {UnknownControl} at index 1, returning Unknown");
        }

        private (TestTcpClient Client, TestableBiampTtp Dsp) CreateConnectedDsp()
        {
            var client = new TestTcpClient();
            client.SetConnectionState(ConnectionState.Disconnected);
            var dsp = Logged.Create(_logs, () => new TestableBiampTtp(client, _time));
            SetPrivateField<CommunicationClient>(client, "_connectionState", ConnectionState.Connected);
            return (client, dsp);
        }
    }
}
