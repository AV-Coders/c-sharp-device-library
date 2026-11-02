using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Dsp.Tests;

public class BoseCspSoIPTest
{
    private readonly FakeTimeProvider _time = new();
    private readonly BoseCspSoIP _dsp;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<VolumeLevelHandler> _volumeLevelHandler = new();
    private readonly Mock<MuteStateHandler> _muteStateHandler = new();
    private readonly Mock<StringValueHandler> _stringValueHandler = new();

    private const string GainName = "Fitness";
    private const string MuteName = "Fitness";
    private const string StringName = "Source";
    private const string UnknownControl = "Nowhere";
    private const int NeverPollMs = 500000;
    private static readonly TimeSpan QueryGap = TimeSpan.FromMilliseconds(30);

    public BoseCspSoIPTest()
    {
        _dsp = Create(_mockClient.Object);

        _mockClient.Setup(x => x.Send($"GA\"{GainName} Gain\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=0\r"));
        _mockClient.Setup(x => x.Send($"GA\"{MuteName} Gain\">2\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{MuteName} Gain\">2=F\r"));
        _mockClient.Setup(x => x.Send($"GA\"{StringName} Selector\">1\r"))
            .Callback(() => _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{StringName} Selector\">1=2\r"));

        _dsp.AddControl(_volumeLevelHandler.Object, GainName);
        _dsp.AddControl(_muteStateHandler.Object, MuteName);
        _dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    [Fact]
    public void HandleResponse_StoresThePercentage()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        Assert.Equal(100, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData($"GA\"{MuteName} Gain\">2=F\r", MuteState.Off)]
    [InlineData($"GA\"{MuteName} Gain\">2=O\r", MuteState.On)]
    public void HandleResponse_StoresTheMuteState(string response, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Equal(expectedMuteState, _dsp.GetAudioMute(MuteName));
    }

    [Fact]
    public void HandleResponse_StoresTheStringValue()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{StringName} Selector\">1=3\r");

        Assert.Equal("3", _dsp.GetValue(StringName));
    }

    [Fact]
    public void HandleResponse_IgnoresUnknownControls()
    {
        var levelBefore = _dsp.GetLevel(GainName);

        _mockClient.Object.ResponseHandlers!.Invoke("GA\"Somewhere else Gain\">1=12\r");

        Assert.Equal(levelBefore, _dsp.GetLevel(GainName));
    }

    [Theory]
    [InlineData(0, $"SA\"{GainName} Gain\">1=-60.5\r")]
    [InlineData(100, $"SA\"{GainName} Gain\">1=12\r")]
    [InlineData(50, $"SA\"{GainName} Gain\">1=-24\r")]
    [InlineData(13, $"SA\"{GainName} Gain\">1=-51\r")]
    public void SetLevel_SendsTheCorrectDB(int percentage, string expectedCommand)
    {
        _dsp.SetLevel(GainName, percentage);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void SetValue_SendsTheCommand()
    {
        _dsp.SetValue(GainName, "3");

        _mockClient.Verify(x => x.Send($"SA\"{GainName} Selector\">1=3\r"));
    }

    [Fact]
    public void HandleResponse_UpdatesTheCommunicationState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Fact]
    public void AddControl_WhileDisconnected_SendsNoQuery()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = Create(client);

        RegisterOneOfEachControl(dsp);

        Assert.Empty(client.Sent);
    }

    [Fact]
    public void AddControl_WhileConnected_QueriesTheControlStraightAway()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);

        RegisterOneOfEachControl(dsp);

        Assert.Contains($"GA\"{GainName} Gain\">1\r", client.Sent);
        Assert.Contains($"GA\"{MuteName} Gain\">2\r", client.Sent);
        Assert.Contains($"GA\"{StringName} Selector\">1\r", client.Sent);
    }

    [Fact]
    public async Task Connecting_QueriesEachControl()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Disconnected);
        var dsp = Create(client);
        RegisterOneOfEachControl(dsp);

        client.SetConnectionState(ConnectionState.Connected);

        await Wait.UntilAsync(_time, QueryGap, () => client.Sent.Contains($"GA\"{StringName} Selector\">1\r"), "the selector query after connecting");
        Assert.Contains($"GA\"{GainName} Gain\">1\r", client.Sent);
        Assert.Contains($"GA\"{MuteName} Gain\">2\r", client.Sent);
    }

    [Fact]
    public void HandleConnectionState_GivenADrop_ReportsUnknown()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);
        client.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");
        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);

        client.SetConnectionState(ConnectionState.Disconnected);

        Assert.Equal(CommunicationState.Unknown, dsp.CommunicationState);
    }

    [Fact]
    public void ClientDisconnectedOnRequest_ReportsNotAttempted()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = Create(client);
        client.ResponseHandlers!.Invoke($"GA\"{GainName} Gain\">1=12\r");

        client.Disconnect();

        Assert.Equal(CommunicationState.NotAttempted, dsp.CommunicationState);
    }

    [Theory]
    [InlineData(1, $"SA\"{GainName} Gain\">3=-1\r")]
    [InlineData(-2, $"SA\"{GainName} Gain\">3=2\r")]
    public void LevelDown_SendsASignedStep(int amount, string expectedCommand)
    {
        _dsp.LevelDown(GainName, amount);

        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void AddControl_ToAnExistingGain_AppliesTheNewRange()
    {
        _dsp.AddControl(_volumeLevelHandler.Object, GainName, -80, 10);

        _dsp.SetLevel(GainName, 0);
        _dsp.SetLevel(GainName, 100);

        _mockClient.Verify(x => x.Send($"SA\"{GainName} Gain\">1=-80\r"));
        _mockClient.Verify(x => x.Send($"SA\"{GainName} Gain\">1=10\r"));
    }

    [Fact]
    public void AddControl_WithTheDefaultRangeAfterARangedCall_KeepsTheRange()
    {
        _dsp.AddControl(_volumeLevelHandler.Object, "Main", -40, 0);
        _ = new BoseCspSoIPVolumeControl(new BoseAudioBlockInfo("Main", "Main"), VolumeType.Speaker, _dsp);

        _dsp.SetLevel("Main", 0);
        _dsp.SetLevel("Main", 100);

        _mockClient.Verify(x => x.Send("SA\"Main Gain\">1=-40\r"));
        _mockClient.Verify(x => x.Send("SA\"Main Gain\">1=0\r"));
    }

    [Fact]
    public async Task Poll_WhileAControlIsAdded_QueriesTheControlsItStartedWith()
    {
        var client = new TestTcpClient();
        var dsp = Create(client);
        RegisterOneOfEachControl(dsp);
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ConnectionState.Connected);
        var poll = (Task)typeof(BoseCspSoIP).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dsp, [CancellationToken.None])!;

        dsp.AddControl(_volumeLevelHandler.Object, "Late");
        await Wait.UntilAsync(_time, QueryGap, () => poll.IsCompleted, "the poll never finished");
        await poll;

        Assert.Contains($"GA\"{GainName} Gain\">1\r", client.Sent);
        Assert.Contains($"GA\"{MuteName} Gain\">2\r", client.Sent);
        Assert.Contains($"GA\"{StringName} Selector\">1\r", client.Sent);
    }

    [Fact]
    public void Nak01_RaisesAMajorIssueOnTheModuleUntilACommandToItSucceeds()
    {
        var (dsp, client) = CreateConnected();
        dsp.SetAudioMute("Bar", MuteState.On);

        client.ResponseHandlers!.Invoke("\u0015 01\r");

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == "nak:Bar Gain");
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Equal("The CSP rejected SA\"Bar Gain\">2=O with NAK 01: no module has that name, or the name is a duplicate",
            issue.Message);

        dsp.SetAudioMute("Bar", MuteState.Off);
        client.ResponseHandlers!.Invoke("\u0006");

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == "nak:Bar Gain");
        Assert.Equal(CommunicationState.Okay, dsp.CommunicationState);
    }

    [Theory]
    [InlineData("02", IssueSeverity.Major, "the index is not valid for that module")]
    [InlineData("03", IssueSeverity.Minor, "the value is out of range")]
    [InlineData("42", IssueSeverity.Minor, "a code the CSP serial control guide does not list")]
    public void Nak_RaisesAnIssueCarryingTheCodeAndItsMeaning(string code, IssueSeverity severity, string meaning)
    {
        var (dsp, client) = CreateConnected();
        dsp.LevelUp("Bar", 2);

        client.ResponseHandlers!.Invoke($"\u0015 {code}\r");

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == "nak:Bar Gain");
        Assert.Equal(severity, issue.Severity);
        Assert.Equal($"The CSP rejected SA\"Bar Gain\">3=2 with NAK {code}: {meaning}", issue.Message);
    }

    [Fact]
    public void Nak_ToAQuery_IsResolvedByTheModulesNextReply()
    {
        var (dsp, client) = CreateConnected();
        dsp.AddControl(_volumeLevelHandler.Object, "Lobby");

        client.ResponseHandlers!.Invoke("\u001501\r");
        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key == "nak:Lobby Gain");
        Assert.Contains("GA\"Lobby Gain\">1", issue.Message);

        client.ResponseHandlers!.Invoke("GA\"Lobby Gain\">1=-10\r");
        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key == "nak:Lobby Gain");
    }

    [Fact]
    public void Replies_AreMatchedToCommandsInTheOrderTheyWereSent()
    {
        var (dsp, client) = CreateConnected();
        dsp.SetAudioMute("Bar", MuteState.On);
        dsp.SetValue("Lobby", "3");

        client.ResponseHandlers!.Invoke("\u0006\u0015 01\r");

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key.StartsWith("nak:"));
        Assert.Equal("nak:Lobby Selector", issue.Key);
    }

    [Fact]
    public void QueryReply_DropsEarlierCommandsTheCspDidNotAnswer()
    {
        var (dsp, client) = CreateConnected();
        dsp.SetAudioMute("Bar", MuteState.On);
        dsp.AddControl(_volumeLevelHandler.Object, "Lobby");
        dsp.SetValue("Hall", "2");

        client.ResponseHandlers!.Invoke("GA\"Lobby Gain\">1=-10\r\u0015 03\r");

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key.StartsWith("nak:"));
        Assert.Equal("nak:Hall Selector", issue.Key);
    }

    [Fact]
    public void Nak_AfterADisconnectAndReconnect_BlamesTheCommandSentSinceReconnecting()
    {
        var (dsp, client) = CreateConnected();
        dsp.SetAudioMute("Bar", MuteState.On);
        client.SetConnectionState(ConnectionState.Disconnected);
        client.SetConnectionState(ConnectionState.Connected);
        dsp.SetValue("Lobby", "3");

        client.ResponseHandlers!.Invoke("\u0015 01\r");

        var issue = Assert.Single(dsp.GetOngoingIssues(), i => i.Key.StartsWith("nak:"));
        Assert.Equal("nak:Lobby Selector", issue.Key);
    }

    [Fact]
    public void Nak_WithNoCommandAwaitingAReply_IsAMomentaryIssue()
    {
        var (dsp, client) = CreateConnected();

        client.ResponseHandlers!.Invoke("\u0015 99\r");

        Assert.DoesNotContain(dsp.GetOngoingIssues(), i => i.Key.StartsWith("nak:"));
        Assert.Contains(dsp.GetIssues(), i => i.Key == "untracked-nak" && i.Message.Contains("NAK 99"));
    }

    private BoseCspSoIP Create(TcpClient client) => new(client, "Test", NeverPollMs, _time);

    private (BoseCspSoIP Dsp, TestTcpClient Client) CreateConnected()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        return (Create(client), client);
    }

    private void RegisterOneOfEachControl(BoseCspSoIP dsp)
    {
        dsp.AddControl(_volumeLevelHandler.Object, GainName);
        dsp.AddControl(_muteStateHandler.Object, MuteName);
        dsp.AddControl(_stringValueHandler.Object, StringName);
    }

    [Collection(LoggerFactoryCollection.Name)]
    public class LogTest
    {
        private readonly FakeTimeProvider _time = new();

        [Fact]
        public void SetLevel_GivenAnUnknownControl_SendsNothingAndLogs()
        {
            var logs = new ScopeCapturingLoggerFactory();
            var client = new TestTcpClient();
            var dsp = Logged.Create(logs, () => new BoseCspSoIP(client, "Test", NeverPollMs, _time));

            dsp.SetLevel(UnknownControl, 40);

            Assert.Empty(client.Sent);
            Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl}, not setting it to 40");
        }

        [Fact]
        public void Gets_GivenAnUnknownControl_ReturnTheDefaultAndLog()
        {
            var logs = new ScopeCapturingLoggerFactory();
            var dsp = Logged.Create(logs, () => new BoseCspSoIP(new TestTcpClient(), "Test", NeverPollMs, _time));

            Assert.Equal(0, dsp.GetLevel(UnknownControl));
            Assert.Equal(MuteState.Unknown, dsp.GetAudioMute(UnknownControl));
            Assert.Equal("", dsp.GetValue(UnknownControl));
            Assert.Contains(logs.Logs, x => x.Message == $"Unknown level {UnknownControl}, returning 0");
            Assert.Contains(logs.Logs, x => x.Message == $"Unknown mute {UnknownControl}, returning Unknown");
            Assert.Contains(logs.Logs, x => x.Message == $"Unknown selector {UnknownControl}, returning an empty value");
        }
    }
}
