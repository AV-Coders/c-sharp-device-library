using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Xunit.Abstractions;

namespace AVCoders.Dsp.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class BiampTtpTest
{
    private readonly ITestOutputHelper _testOutputHelper;
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
    private const int WaitTimeoutMs = 15000;
    private const int WaitStepMs = 50;
    private const int LongAgoSeconds = 60;

    private static readonly string[] Subscriptions =
    [
        $"{GainName} subscribe level 1 AvCodersLevel-{GainName}-1\n",
        $"{MuteName} subscribe mute 1 AvCodersMute-{MuteName}-1\n"
    ];

    public BiampTtpTest(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _dsp = new BiampTtp(_mockClient.Object, "Test", 100);
        
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
    public void AddControl_WhileConnected_SubscribesToTheLevelStraightAway()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);

        dsp.AddControl(_volumeLevelHandler.Object, GainName);

        Assert.Contains($"{GainName} subscribe level 1 AvCodersLevel-{GainName}-1\n", client.Sent);
    }

    [Fact]
    public void AddControl_WhileConnected_SubscribesToTheMuteStraightAway()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);

        dsp.AddControl(_muteStateHandler.Object, MuteName);

        Assert.Contains($"{MuteName} subscribe mute 1 AvCodersMute-{MuteName}-1\n", client.Sent);
    }

    [Fact]
    public void AddControl_WhileDisconnected_SendsNoSubscribe()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);

        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);

        Assert.DoesNotContain(client.Sent, IsSubscribe);
    }

    [Fact]
    public async Task Connecting_SendsEachRegisteredSubscriptionOnce()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        await WaitUntil(() => dsp.Events.Any(e => e.Info == DisconnectedPollEvent));

        client.SetConnectionState(ConnectionState.Connected);
        await WaitUntil(() => client.Sent.Contains(FirstQueryAfterReinitialise));

        Assert.Equal(Subscriptions.Order(), client.Sent.Where(IsSubscribe).Order());
    }

    [Fact]
    public async Task Reinitialise_WhileDisconnected_SendsNothing()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);
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
        var dsp = new BiampTtp(client.Object, "Perceptual", 100, FaderCurve.Perceptual);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);

        dsp.SetLevel(GainName, percentage);

        client.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void HandleResponse_WithPerceptualCurve_StoresThePercentage()
    {
        var client = TestFactory.CreateTcpClient();
        var dsp = new BiampTtp(client.Object, "Perceptual", 100, FaderCurve.Perceptual);
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

    [Theory]
    [InlineData(MuteState.On, "true")]
    [InlineData(MuteState.Off, "false")]
    public void BiampVolumeControl_SetAudioMute_UsesTheConfiguredIndex(MuteState state, string expectedValue)
    {
        var vc = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);

        vc.SetAudioMute(state);

        _mockClient.Verify(x => x.Send($"room_mic_level set mute 5 {expectedValue}\n"));
    }

    [Fact]
    public void BiampVolumeControl_ToggleAudioMute_UsesTheConfiguredIndex()
    {
        var vc = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, _dsp);
        _mockClient.Object.ResponseHandlers?.Invoke("! \"publishToken\":\"AvCodersMute-room_mic_level-5\" \"value\":false\n");

        vc.ToggleAudioMute();

        _mockClient.Verify(x => x.Send("room_mic_level set mute 5 true\n"));
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
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);

        _ = new BiampVolumeControl(new BiampAudioBlockInfo("Bar 1", "room_mic_level", 5), VolumeType.Microphone, dsp);

        Assert.Contains("room_mic_level subscribe level 5 AvCodersLevel-room_mic_level-5\n", client.Sent);
        Assert.Contains("room_mic_level subscribe mute 5 AvCodersMute-room_mic_level-5\n", client.Sent);
    }

    [Fact]
    public void ClientDisconnectedOnRequest_ReportsNotAttempted()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);

        client.Disconnect();

        Assert.Equal(CommunicationState.NotAttempted, dsp.CommunicationState);
    }

    [Fact]
    public async Task MatchedReply_IsHandledUnderTheQuerysTriggerId()
    {
        var client = new ReplyingTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);
        var handled = new ConcurrentQueue<(int Level, string? TriggerId)>();
        Trigger.Start();
        dsp.AddControl(level => handled.Enqueue((level, Trigger.Id)), GainName);

        await WaitUntil(() => handled.Any(x => x.Level > 0));

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
        var dsp = new BiampTtp(client, "Test", PollIntervalMs);
        var handled = new ConcurrentQueue<(int Level, string? TriggerId)>();
        dsp.AddControl(_volumeLevelHandler.Object, unansweredName);
        await WaitUntil(() => client.Sent.Contains($"{unansweredName} get maxLevel 1\n"));

        Assert.Null(Trigger.Id);
        dsp.AddControl(level => handled.Enqueue((level, Trigger.Id)), GainName);

        await WaitUntil(() => handled.Any(x => x.Level > 0));
        Assert.Equal(ReplyingTcpClient.ReceiveTriggerId, handled.First(x => x.Level > 0).TriggerId);
    }

    [Fact]
    public async Task PolledQuery_LogsUnderTheQuerysTriggerId()
    {
        var client = new ReplyingTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        BiampTtp dsp;
        try
        {
            dsp = new BiampTtp(client, "Test", PollIntervalMs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
        var levelQuery = $"{GainName} get level 1\n";
        Trigger.Start();
        dsp.AddControl(_volumeLevelHandler.Object, GainName);

        await WaitUntil(() => logs.Logs.Any(x => x.Message == $"Polling for {levelQuery}"));

        Assert.Equal(client.SentUnder(levelQuery), logs.Logs.First(x => x.Message == $"Polling for {levelQuery}").TriggerId);
    }

    [Fact]
    public void HandleResponse_AfterTheVersionReply_HandlesTheRestOfTheLines()
    {
        SetPrivateField(_dsp, "_lastRequestWasForTheVersion", true);

        _mockClient.Object.ResponseHandlers?.Invoke("+OK \"value\":\"4.2.1.23\"\n! \"publishToken\":\"AvCodersLevel-Gain-1\" \"value\":-2.000000\n");

        Assert.Equal(98, _dsp.GetLevel(GainName));
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
    public async Task SetLevel_WhileAQueryIsInFlight_AsksTheNextQueryAfterTheInterruptedReplyTimeout()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = CreateLogged(logs, client);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        SetPrivateField<CommunicationClient>(client, "_connectionState", ConnectionState.Connected);
        await dsp.PollNow();
        dsp.SetLevel(GainName, 50);
        await dsp.PollNow();
        Assert.DoesNotContain(MinLevelQuery, client.Sent);

        SetPrivateField<BiampTtp>(dsp, "_interruptedAt", Stopwatch.GetTimestamp() - LongAgoSeconds * Stopwatch.Frequency);
        await dsp.PollNow();

        Assert.Equal(MinLevelQuery, client.Sent[^1]);
        Assert.Contains(logs.Logs, x => x.Message == $"The interrupted query {MaxLevelQuery} was not answered, polling the next query");
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
        Assert.Equal($"The Tesira returned an error to a command: {errorReply}", issue.Message);
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
    public void StringValues_AreNotSupported_AndSayWhy()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = new TestTcpClient();
        var dsp = CreateLogged(logs, client);

        dsp.AddControl(_stringValueHandler.Object, StringName);
        dsp.SetValue(StringName, "5");
        var value = dsp.GetValue(StringName);

        Assert.Equal("", value);
        Assert.Empty(client.Sent);
        Assert.Contains(logs.Logs, x => x.Message == $"Test does not support string values, {StringName} will never be reported");
        Assert.Contains(logs.Logs, x => x.Message == $"Test does not support string values, not setting {StringName} to 5");
        Assert.Contains(logs.Logs, x => x.Message == $"Test does not support string values, returning an empty value for {StringName}");
    }

    [Fact]
    public void Commands_GivenAnUnknownControl_SendNothingAndLogTheValues()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var client = new TestTcpClient();
        var dsp = CreateLogged(logs, client);

        dsp.SetLevel(UnknownControl, 40);
        dsp.LevelUp(UnknownControl, 3);
        dsp.LevelDown(UnknownControl, 2);
        dsp.ToggleAudioMute(UnknownControl);

        Assert.Empty(client.Sent);
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, not setting it to 40");
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, not raising it by 3");
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, not lowering it by 2");
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown mute {UnknownControl} at index 1, not toggling it");
    }

    [Fact]
    public void Gets_GivenAnUnknownControl_ReturnTheDefaultAndLog()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var dsp = CreateLogged(logs, new TestTcpClient());

        Assert.Equal(0, dsp.GetLevel(UnknownControl));
        Assert.Equal(MuteState.Unknown, dsp.GetAudioMute(UnknownControl));
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl} at index 1, returning 0");
        Assert.Contains(logs.Logs, x => x.Message == $"Unknown mute {UnknownControl} at index 1, returning Unknown");
    }

    private (TestTcpClient Client, TestableBiampTtp Dsp) CreatePollingDsp()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = new TestableBiampTtp(client);
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
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

    private static TestableBiampTtp CreateLogged(ScopeCapturingLoggerFactory logs, TcpClient client)
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return new TestableBiampTtp(client);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private static void SetPrivateField<T>(T target, string fieldName, object value) =>
        typeof(T).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private class TestableBiampTtp(TcpClient client) : BiampTtp(client, "Test", NeverPollMs)
    {
        public Task PollNow() => Poll(CancellationToken.None);
    }

    private class ReplyingTcpClient : TestTcpClient
    {
        public const string ReceiveTriggerId = "0000000f";
        private readonly ConcurrentDictionary<string, string?> _sentUnder = new();
        private readonly Dictionary<string, string> _replies = new()
        {
            [$"{GainName} get maxLevel 1\n"] = "+OK \"value\":12.000000\n",
            [$"{GainName} get minLevel 1\n"] = "+OK \"value\":0.000000\n",
            [$"{GainName} get level 1\n"] = "+OK \"value\":3.000000\n"
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

    private static bool IsSubscribe(string message) => message.Contains(" subscribe ");

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(WaitTimeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(WaitStepMs);
        Assert.True(condition());
    }
}