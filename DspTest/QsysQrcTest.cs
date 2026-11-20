using System.Text;
using System.Text.Json.Nodes;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Dsp.Tests;

public class QsysQrcTest
{
    // Invented QRC traffic, shaped on QSC's QRC protocol as the legacy QscQsys and QSYSControlV4_2 libraries use it and on the
    // examples in help.qsys.com QRC_Commands; nothing here was captured from a Core. Check it against the Core.
    private const string EngineStatus =
        """{"jsonrpc":"2.0","method":"EngineStatus","params":{"Platform":"Core 110f","State":"Active","DesignName":"Test Design","DesignCode":"aBcD1234eFgH","IsRedundant":false,"IsEmulator":false}}""";
    private const string StandbyStatus =
        """{"jsonrpc":"2.0","method":"EngineStatus","params":{"Platform":"Core 110f","State":"Standby","DesignName":"Test Design","DesignCode":"aBcD1234eFgH","IsRedundant":true,"IsEmulator":false}}""";
    private const string RoomAt42AndMuted =
        """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","params":{"Id":"AVCoders","Changes":[{"Component":"Room","Name":"gain","String":"-10.0dB","Value":-10.0,"Position":0.42},{"Component":"Room","Name":"mute","String":"muted","Value":1.0,"Position":1.0}]}}""";
    private const string RoomUnmuted =
        """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","params":{"Id":"AVCoders","Changes":[{"Component":"Room","Name":"mute","String":"unmuted","Value":0.0,"Position":0.0}]}}""";
    private const string Mic1At50 =
        """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","params":{"Id":"AVCoders","Changes":[{"Component":"Mic 1","Name":"gain","String":"-6.0dB","Value":-6.0,"Position":0.5}]}}""";
    private const string MainMixerInput1At42 =
        """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","params":{"Id":"AVCoders","Changes":[{"Component":"Main Mixer","Name":"input.1.gain","String":"-10.0dB","Value":-10.0,"Position":0.42}]}}""";
    private const string PollWithAnArrayForParams = """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","params":["AVCoders"]}""";
    private const string PollWithADuplicateKey =
        """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","method":"ChangeGroup.Poll","params":{"Id":"AVCoders","Changes":[]}}""";
    private const string PollWithADuplicateKeyInAChange =
        """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","params":{"Id":"AVCoders","Changes":[{"Component":"Room","Name":"mute","Name":"mute","String":"unmuted","Value":0.0,"Position":0.0}]}}""";
    private const string Username = "tech";
    private const string Password = "s3cret-pw";
    private const string RoomGain = "Room.gain";
    private const string RoomMute = "Room.mute";
    private const string MicGain = "Mic 1.gain";
    private const string MainMixerInputGain = "Main Mixer.input.1.gain";
    private const string LobbyZoneGain = "Lobby.Zone 1.gain";
    private const string SnapshotComponent = "G05-RoomModeSS";
    private const string CommunicationIssueKey = "communication";
    private const string MissingControlsLabel = "Missing Controls";
    private const int NeverPollMs = 3_600_000;
    private const char Delimiter = '\0';
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new();
    private readonly TestTcpClient _client = new();
    private readonly Mock<VolumeLevelHandler> _levelHandler = new();
    private readonly Mock<MuteStateHandler> _muteHandler = new();
    private readonly TestableQsysQrc _dsp;

    public QsysQrcTest()
    {
        _client.SetConnectionState(ConnectionState.Connected);
        _dsp = new TestableQsysQrc(_client, Username, Password, _time);
    }

    private TimeSpan QueueTimeout => TimeSpan.FromSeconds(_client.QueueTimeoutFor(false));

    private static TimeSpan ReplyTimeout => QsysQrc.ReplyTimeout;

    private void Receive(string message) => _client.Reply(message + Delimiter);

    private static List<JsonObject> RequestsOf(TestTcpClient client) =>
        [..client.Sent.Select(sent => (JsonObject)JsonNode.Parse(sent.TrimEnd(Delimiter))!)];

    private static List<string> MethodsOf(TestTcpClient client) => [..RequestsOf(client).Select(request => request["method"]!.ToString())];

    private static List<string> ParamsOf(TestTcpClient client, string method) =>
        [..RequestsOf(client).Where(request => request["method"]!.ToString() == method).Select(request => request["params"]!.ToJsonString())];

    private static int IdIn(TestTcpClient client, string method) =>
        (int)RequestsOf(client).Last(request => request["method"]!.ToString() == method)["id"]!;

    private List<JsonObject> Requests => RequestsOf(_client);

    private List<string> Methods => MethodsOf(_client);

    private List<string> Params(string method) => ParamsOf(_client, method);

    private int IdOf(string method) => IdIn(_client, method);

    private List<int> IdsOf(string method) =>
        [..Requests.Where(request => request["method"]!.ToString() == method).Select(request => (int)request["id"]!)];

    private static string ResultReply(int id, string result) => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}";

    private static string ErrorReply(int id, int code, string text) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":\"{text}\"}}}}";

    private static string ComponentList(params string[] names) =>
        $"[{string.Join(",", names.Select(name => $$"""{"ID":"{{name}}","Name":"{{name}}","Type":"mixer","Properties":[],"Controls":null,"ControlSource":2}"""))}]";

    private void LogOn()
    {
        Receive(EngineStatus);
        Receive(ResultReply(IdOf("Logon"), "true"));
    }

    private DeviceDetail? Detail(string label) => _dsp.Details.SingleOrDefault(detail => detail.Label == label);

    private string Answer(string method, string result)
    {
        var reply = ResultReply(IdOf(method), result);
        Receive(reply);
        return reply;
    }

    private string Fail(string method, int code, string text)
    {
        var error = ErrorReply(IdOf(method), code, text);
        Receive(error);
        return error;
    }

    private string FailId(int id, int code)
    {
        var error = ErrorReply(id, code, "Logon required");
        Receive(error);
        return error;
    }

    private static string StatusResult(string designCode) =>
        $"{{\"Platform\":\"Core 110f\",\"State\":\"Active\",\"DesignName\":\"Test Design\",\"DesignCode\":\"{designCode}\",\"IsRedundant\":false,\"IsEmulator\":false}}";

    [Fact]
    public async Task EveryRequest_IsJsonRpcTwoWithANumericId_EndingInItsOnlyNul()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        LogOn();
        _dsp.SetLevel(RoomGain, 42);
        _dsp.RecallSnapshot(SnapshotComponent, 2);
        await _dsp.PollNow();
        await _dsp.Reinitialise();

        Assert.Equal(["Logon", "ChangeGroup.AddComponentControl", "ChangeGroup.AutoPoll", "Component.Set", "Component.Set", "StatusGet",
            "ChangeGroup.AddComponentControl", "ChangeGroup.AutoPoll", "ChangeGroup.Invalidate"], Methods);
        Assert.All(_client.Sent, sent => Assert.Equal(sent.Length - 1, sent.IndexOf(Delimiter)));
        Assert.All(Requests, request =>
        {
            Assert.Equal("2.0", request["jsonrpc"]!.ToString());
            Assert.Equal(System.Text.Json.JsonValueKind.Number, request["id"]!.GetValueKind());
        });
    }

    [Fact]
    public void EngineStatus_LogsOnWithTheConfiguredLogin()
    {
        Receive(EngineStatus);

        Assert.Equal([$$"""{"User":"{{Username}}","Password":"{{Password}}"}"""], Params("Logon"));
    }

    [Fact]
    public void ABlankLogin_SkipsTheLogon_AndStartsTheChangeGroup()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, "", "", _time);
        dsp.AddControl(_levelHandler.Object, RoomGain);

        client.Reply(EngineStatus + Delimiter);

        Assert.Equal(["ChangeGroup.AddComponentControl", "ChangeGroup.AutoPoll"], MethodsOf(client));
    }

    [Fact]
    public void TheLogonResult_AddsEachComponentsControls_ThenAutoPolls_ThenSendsWhatWaitedForTheLogon()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        _dsp.AddControl(_levelHandler.Object, MicGain);
        _dsp.AddControl(_muteHandler.Object, RoomMute);
        _dsp.SetLevel(RoomGain, 42);
        Assert.Empty(_client.Sent);

        LogOn();

        Assert.Equal(["Logon", "ChangeGroup.AddComponentControl", "ChangeGroup.AddComponentControl", "ChangeGroup.AutoPoll", "Component.Set"], Methods);
        Assert.Equal(
            [
                """{"Id":"AVCoders","Component":{"Name":"Room","Controls":[{"Name":"gain"},{"Name":"mute"}]}}""",
                """{"Id":"AVCoders","Component":{"Name":"Mic 1","Controls":[{"Name":"gain"}]}}"""
            ],
            Params("ChangeGroup.AddComponentControl"));
        Assert.Equal(["""{"Id":"AVCoders","Rate":0.5}"""], Params("ChangeGroup.AutoPoll"));
        Assert.Equal(["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}"""], Params("Component.Set"));
    }

    [Fact]
    public async Task AStatusResult_LogsOn_WhenNoEngineStatusCame()
    {
        await _dsp.PollNow();

        Receive($$$$"""{"jsonrpc":"2.0","id":{{{{IdOf("StatusGet")}}}},"result":{"Platform":"Core 110f","State":"Active","DesignName":"Test Design","DesignCode":"aBcD1234eFgH","IsRedundant":false,"IsEmulator":false,"Status":{"Code":0,"String":"OK"}}}""");

        Assert.Equal(["StatusGet", "Logon"], Methods);
        Assert.Equal(new DeviceDetail("Design", "Test Design"), Detail("Design"));
        Assert.Equal(new DeviceDetail("Design code", "aBcD1234eFgH"), Detail("Design code"));
        Assert.Equal(new DeviceDetail("Core", "Active"), Detail("Core"));
    }

    [Fact]
    public void AStandbyCore_RaisesACriticalIssue_UntilItIsActive()
    {
        Receive(StandbyStatus);

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.CoreStandbyIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal(new DeviceDetail("Core", "Standby", DetailTone.Error), Detail("Core"));

        Receive(EngineStatus);

        Assert.DoesNotContain(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.CoreStandbyIssueKey);
    }

    [Fact]
    public void ChangeGroupPolls_SetTheLevelAndMute()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        _dsp.AddControl(_muteHandler.Object, RoomMute);

        Receive(RoomAt42AndMuted);

        _levelHandler.Verify(handler => handler.Invoke(42));
        _muteHandler.Verify(handler => handler.Invoke(MuteState.On));
        Assert.Equal(42, _dsp.GetLevel(RoomGain));
        Assert.Equal(MuteState.On, _dsp.GetAudioMute(RoomMute));
        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Fact]
    public void AnAutoPollResultWithChanges_SetsTheLevel()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);

        Receive("""{"jsonrpc":"2.0","id":7,"result":{"Id":"AVCoders","Changes":[{"Component":"Room","Name":"gain","String":"-3.0dB","Value":-3.0,"Position":0.7}]}}""");

        Assert.Equal(70, _dsp.GetLevel(RoomGain));
    }

    [Fact]
    public void AMessageSplitAcrossReads_AndTwoInOneRead_AreEachHandled()
    {
        _dsp.AddControl(_muteHandler.Object, RoomMute);
        var both = RoomAt42AndMuted + Delimiter + RoomUnmuted + Delimiter;

        _client.Reply(both[..40]);
        _client.Reply(both[40..]);

        Assert.Equal(MuteState.Off, _dsp.GetAudioMute(RoomMute));
        _muteHandler.Verify(handler => handler.Invoke(MuteState.On));
    }

    [Fact]
    public void APollWhoseParamsIsNotAnObject_DoesNotStopTheRestOfTheRead()
    {
        _dsp.AddControl(_muteHandler.Object, RoomMute);

        _client.Reply(PollWithAnArrayForParams + Delimiter + RoomAt42AndMuted + Delimiter);

        Assert.Equal(MuteState.On, _dsp.GetAudioMute(RoomMute));
    }

    [Theory]
    [InlineData(PollWithADuplicateKey)]
    [InlineData(PollWithADuplicateKeyInAChange)]
    public void AReplyWithADuplicateKey_RaisesTheUnreadableReplyIssue_AndTheRestOfTheReadIsHandled(string duplicateKey)
    {
        _dsp.AddControl(_muteHandler.Object, RoomMute);

        _client.Reply(duplicateKey + Delimiter + RoomAt42AndMuted + Delimiter);

        var issue = Assert.Single(_dsp.GetIssues(), issue => issue.Key == QsysQrc.UnreadableReplyIssueKey);
        Assert.EndsWith($": {duplicateKey}", issue.Message);
        Assert.Equal(MuteState.On, _dsp.GetAudioMute(RoomMute));
        _muteHandler.Verify(handler => handler.Invoke(MuteState.Off), Times.Never);
    }

    [Fact]
    public void AHandlerThatThrows_IsNotReportedAsAnUnreadableReply_AndTheRestOfTheReadIsHandled()
    {
        var throwing = new Mock<VolumeLevelHandler>();
        throwing.Setup(handler => handler.Invoke(It.IsAny<int>())).Throws(new InvalidOperationException("subscriber failed"));
        _dsp.AddControl(throwing.Object, RoomGain);
        _dsp.AddControl(_muteHandler.Object, RoomMute);

        _client.Reply(RoomAt42AndMuted + Delimiter + RoomUnmuted + Delimiter);

        Assert.DoesNotContain(_dsp.GetIssues(), issue => issue.Key == QsysQrc.UnreadableReplyIssueKey);
        Assert.Equal(MuteState.Off, _dsp.GetAudioMute(RoomMute));
    }

    [Fact]
    public void SetAudioMute_SetsTheMuteValue()
    {
        LogOn();

        _dsp.SetAudioMute(RoomMute, MuteState.On);
        _dsp.SetAudioMute(RoomMute, MuteState.Off);

        Assert.Equal(
            ["""{"Name":"Room","Controls":[{"Name":"mute","Value":1}]}""", """{"Name":"Room","Controls":[{"Name":"mute","Value":0}]}"""],
            Params("Component.Set"));
    }

    [Fact]
    public void ToggleAudioMute_SendsTheOppositeOfTheLastFedBackState()
    {
        _dsp.AddControl(_muteHandler.Object, RoomMute);
        LogOn();
        Receive(RoomAt42AndMuted);

        _dsp.ToggleAudioMute(RoomMute);

        Assert.Equal(["""{"Name":"Room","Controls":[{"Name":"mute","Value":0}]}"""], Params("Component.Set"));
    }

    [Fact]
    public void LevelUp_AddsToTheLastFedBackLevel()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        LogOn();
        Receive(RoomAt42AndMuted);

        _dsp.LevelUp(RoomGain, 3);

        Assert.Equal(["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.45}]}"""], Params("Component.Set"));
    }

    [Fact]
    public void RecallSnapshot_ThroughQsysDsp_SetsTheSnapshotsLoadControl()
    {
        LogOn();
        QsysDsp dsp = _dsp;

        dsp.RecallSnapshot(SnapshotComponent, 2);

        Assert.Equal(["""{"Name":"G05-RoomModeSS","Controls":[{"Name":"load_2","Value":1}]}"""], Params("Component.Set"));
    }

    [Fact]
    public void AControlAddedAfterTheLogon_IsAddedToTheChangeGroupAtOnce()
    {
        LogOn();

        _dsp.AddControl(_levelHandler.Object, MicGain);

        Assert.Equal("""{"Id":"AVCoders","Component":{"Name":"Mic 1","Controls":[{"Name":"gain"}]}}""", Params("ChangeGroup.AddComponentControl").Last());
    }

    [Fact]
    public void ARejectedComponent_RaisesAnIssuePerControl_AndListsThemMissing_UntilOneReports()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        _dsp.AddControl(_muteHandler.Object, RoomMute);
        LogOn();
        var rejection = $$$"""{"jsonrpc":"2.0","id":{{{IdOf("ChangeGroup.AddComponentControl")}}},"error":{"code":7,"message":"Unknown component name"}}""";

        Receive(rejection);

        Assert.Equal([$"{QsysQrc.BadControlIssuePrefix}{RoomGain}", $"{QsysQrc.BadControlIssuePrefix}{RoomMute}"],
            _dsp.GetOngoingIssues().Select(issue => issue.Key).Order());
        Assert.Contains(rejection, _dsp.GetOngoingIssues().First().Message);
        Assert.Equal(new DeviceDetail(MissingControlsLabel, $"{RoomGain}, {RoomMute}", DetailTone.Warning), Detail(MissingControlsLabel));

        Receive(RoomUnmuted);

        Assert.Equal([$"{QsysQrc.BadControlIssuePrefix}{RoomGain}"], _dsp.GetOngoingIssues().Select(issue => issue.Key));
        Assert.Equal(new DeviceDetail(MissingControlsLabel, RoomGain, DetailTone.Warning), Detail(MissingControlsLabel));
    }

    [Fact]
    public void AControlNameWithNoComponent_RaisesAnIssue_AndIsNeverSent()
    {
        _dsp.AddControl(_levelHandler.Object, "RoomGain");
        LogOn();
        _dsp.SetLevel("RoomGain", 50);

        Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == $"{QsysQrc.BadControlIssuePrefix}RoomGain");
        Assert.Equal(["Logon", "ChangeGroup.AutoPoll"], Methods);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankControlNames_RaiseOneIssueEach_AndAreNeverSent(string blankName)
    {
        _dsp.AddControl(_levelHandler.Object, blankName);
        _dsp.AddControl(_muteHandler.Object, blankName);
        LogOn();

        Assert.Equal(
            [
                ($"{QsysQrc.BadControlIssuePrefix}\"{blankName}\" level 1", $"The level control name \"{blankName}\" is blank, so it is not sent to the Core (blank control 1)"),
                ($"{QsysQrc.BadControlIssuePrefix}\"{blankName}\" mute 2", $"The mute control name \"{blankName}\" is blank, so it is not sent to the Core (blank control 2)")
            ],
            _dsp.GetOngoingIssues().Select(issue => (issue.Key, issue.Message)));
        Assert.Equal(["Logon", "ChangeGroup.AutoPoll"], Methods);
    }

    [Fact]
    public void ASetRejectedForAnotherReason_RaisesAMomentaryIssue_NamingIt()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 42);

        var rejection = Fail("Component.Set", -32602, "Invalid params");

        var issue = Assert.Single(_dsp.GetIssues(), issue => issue.Key == QsysQrc.RejectedIssueKey);
        Assert.Equal($"The Core rejected Component.Set on {RoomGain}: {rejection}", issue.Message);
    }

    [Fact]
    public void AnUnknownControlErrorToASet_RaisesItsBadControlIssue()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 42);

        var rejection = Fail("Component.Set", QsysQrc.UnknownControlErrorCode, "Unknown control");

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == $"{QsysQrc.BadControlIssuePrefix}{RoomGain}");
        Assert.Equal($"The Core rejected the control {RoomGain}: {rejection}", issue.Message);
        Assert.Equal(new DeviceDetail(MissingControlsLabel, RoomGain, DetailTone.Warning), Detail(MissingControlsLabel));
    }

    [Fact]
    public void AnUnknownComponentErrorToASet_RaisesItsBadControlIssue()
    {
        LogOn();
        _dsp.RecallSnapshot(SnapshotComponent, 2);

        var rejection = Fail("Component.Set", QsysQrc.UnknownComponentErrorCode, "Unknown component name");

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == $"{QsysQrc.BadControlIssuePrefix}{SnapshotComponent}.load_2");
        Assert.Equal($"The Core rejected the control {SnapshotComponent}.load_2: {rejection}", issue.Message);
    }

    [Fact]
    public void ASetReplyReportingItsControlsNewValue_IsNotTakenForAnUnknownControl()
    {
        LogOn();
        _dsp.RecallSnapshot(SnapshotComponent, 7);
        Answer("Component.Set", """["load_7"]""");

        _dsp.RecallSnapshot(SnapshotComponent, 7);
        Answer("Component.Set", """[{"Component":"G05-RoomModeSS","Name":"load_7","String":"true","Value":1,"Position":1.0}]""");

        Assert.Empty(_dsp.GetOngoingIssues());
    }

    [Theory]
    [InlineData("""["load_7"]""")]
    [InlineData("""[{"Name":"load_7"}]""")]
    public void ASetReplyListingItsControlAsUnknown_RaisesItsBadControlIssue(string unknownControls)
    {
        LogOn();
        _dsp.RecallSnapshot(SnapshotComponent, 7);

        var reply = Answer("Component.Set", unknownControls);

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == $"{QsysQrc.BadControlIssuePrefix}{SnapshotComponent}.load_7");
        Assert.Equal($"The Core doesn't know the control {SnapshotComponent}.load_7: {reply}", issue.Message);
        Assert.Equal(new DeviceDetail(MissingControlsLabel, $"{SnapshotComponent}.load_7", DetailTone.Warning), Detail(MissingControlsLabel));
    }

    [Fact]
    public void ASetReplyWithNoUnknownControls_ResolvesTheControlsBadControlIssue()
    {
        LogOn();
        _dsp.RecallSnapshot(SnapshotComponent, 7);
        Answer("Component.Set", """["load_7"]""");

        _dsp.RecallSnapshot(SnapshotComponent, 7);
        Answer("Component.Set", "true");

        Assert.Empty(_dsp.GetOngoingIssues());
        Assert.Null(Detail(MissingControlsLabel));
    }

    [Fact]
    public void AControlNameWithDotsInItsControl_IsSplitAtTheComponentTheCoreLists()
    {
        _dsp.AddControl(_levelHandler.Object, MainMixerInputGain);
        LogOn();
        Assert.Equal(["Logon", "Component.GetComponents", "ChangeGroup.AutoPoll"], Methods);
        Assert.Equal(["\"test\""], Params("Component.GetComponents"));

        Answer("Component.GetComponents", ComponentList("Main Mixer", "Room"));
        Receive(MainMixerInput1At42);
        _dsp.SetLevel(MainMixerInputGain, 60);

        Assert.Equal(["""{"Id":"AVCoders","Component":{"Name":"Main Mixer","Controls":[{"Name":"input.1.gain"}]}}"""],
            Params("ChangeGroup.AddComponentControl"));
        Assert.Equal(["""{"Id":"AVCoders"}"""], Params("ChangeGroup.Invalidate"));
        Assert.Equal(42, _dsp.GetLevel(MainMixerInputGain));
        Assert.Equal(["""{"Name":"Main Mixer","Controls":[{"Name":"input.1.gain","Position":0.6}]}"""], Params("Component.Set"));
    }

    [Fact]
    public void AControlNameWithDotsInItsComponent_IsSplitAtTheLongestComponentTheCoreLists_AfterItsSetWaitsForTheList()
    {
        LogOn();

        _dsp.SetLevel(LobbyZoneGain, 42);
        Assert.Equal("Component.GetComponents", Methods.Last());
        Answer("Component.GetComponents", ComponentList("Lobby", "Lobby.Zone 1"));

        Assert.Equal(["""{"Name":"Lobby.Zone 1","Controls":[{"Name":"gain","Position":0.42}]}"""], Params("Component.Set"));
    }

    [Fact]
    public void ADottedNameThatNoListedComponentStarts_RaisesItsBadControlIssue_AndIsNeverSent()
    {
        _dsp.AddControl(_levelHandler.Object, MainMixerInputGain);
        LogOn();
        Answer("Component.GetComponents", ComponentList("Room"));

        _dsp.SetLevel(MainMixerInputGain, 42);

        var issue = Assert.Single(_dsp.GetOngoingIssues());
        Assert.Equal($"{QsysQrc.BadControlIssuePrefix}{MainMixerInputGain}", issue.Key);
        Assert.Equal($"No component the Core lists (1 of them) starts the control name \"{MainMixerInputGain}\", so it is not sent to the Core",
            issue.Message);
        Assert.DoesNotContain("ChangeGroup.AddComponentControl", Methods);
        Assert.DoesNotContain("Component.Set", Methods);
    }

    [Fact]
    public void ADottedName_WhenTheCoreRejectsTheComponentList_RaisesItsBadControlIssue_WithTheRawReply()
    {
        _dsp.AddControl(_levelHandler.Object, MainMixerInputGain);
        LogOn();

        var rejection = Fail("Component.GetComponents", -32601, "Method not found");

        var issue = Assert.Single(_dsp.GetOngoingIssues());
        Assert.Equal(
            $"The control name \"{MainMixerInputGain}\" has more than one dot, and with no component list from the Core the driver can't tell its component from its control, so it is not sent to the Core: The Core rejected Component.GetComponents: {rejection}",
            issue.Message);
        Assert.Equal(new DeviceDetail(MissingControlsLabel, MainMixerInputGain, DetailTone.Warning), Detail(MissingControlsLabel));
    }

    [Fact]
    public async Task ADottedName_WhenTheCoreDoesNotAnswerTheComponentList_RaisesItsBadControlIssue_ShowingTheRequest()
    {
        _dsp.AddControl(_levelHandler.Object, MainMixerInputGain);
        LogOn();
        var listId = IdOf("Component.GetComponents");

        _time.Advance(ReplyTimeout);
        await _dsp.PollNow();

        var issue = Assert.Single(_dsp.GetOngoingIssues());
        Assert.EndsWith(
            $"The Core did not answer {{\"jsonrpc\":\"2.0\",\"id\":{listId},\"method\":\"Component.GetComponents\",\"params\":\"test\"}} within {ReplyTimeout.TotalSeconds} s",
            issue.Message);
    }

    [Fact]
    public async Task Reinitialise_AsksAgainForAComponentListTheCoreRejected()
    {
        _dsp.AddControl(_levelHandler.Object, MainMixerInputGain);
        LogOn();
        Fail("Component.GetComponents", -32601, "Method not found");

        await _dsp.Reinitialise();
        Answer("Component.GetComponents", ComponentList("Main Mixer"));

        Assert.Equal(2, Methods.Count(method => method == "Component.GetComponents"));
        Assert.Equal(["""{"Id":"AVCoders","Component":{"Name":"Main Mixer","Controls":[{"Name":"input.1.gain"}]}}"""],
            Params("ChangeGroup.AddComponentControl"));
    }

    [Fact]
    public async Task Reinitialise_RebuildsTheChangeGroup_AndInvalidatesIt_SoTheCoreResendsEveryValue()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        LogOn();
        var sentBefore = Methods.Count;

        await _dsp.Reinitialise();

        Assert.Equal(["ChangeGroup.AddComponentControl", "ChangeGroup.AutoPoll", "ChangeGroup.Invalidate"], Methods.Skip(sentBefore));
        Assert.Equal(["""{"Id":"AVCoders"}"""], Params("ChangeGroup.Invalidate"));
    }

    [Fact]
    public async Task Reinitialise_BeforeTheLogon_SendsNothing()
    {
        await _dsp.Reinitialise();

        Assert.Empty(_client.Sent);
    }

    [Fact]
    public void AnUnreadableReply_RaisesAMomentaryIssue_WithTheRawText()
    {
        Receive("{\"jsonrpc\":");

        var issue = Assert.Single(_dsp.GetIssues(), issue => issue.Key == QsysQrc.UnreadableReplyIssueKey);
        Assert.EndsWith(": {\"jsonrpc\":", issue.Message);
    }

    [Fact]
    public void AReconnect_HoldsSetsMadeBeforeTheNextLogon_UntilIt()
    {
        LogOn();
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);

        _dsp.SetLevel(RoomGain, 10);
        Assert.DoesNotContain("Component.Set", Methods);
        LogOn();

        Assert.Equal(["Logon", "ChangeGroup.AutoPoll", "Logon", "ChangeGroup.AutoPoll", "Component.Set"], Methods);
    }

    [Fact]
    public void SetsWaitingForTheLogon_AreSentOnlyWhileYoungerThanTheClientsQueueTimeout()
    {
        _dsp.SetLevel(RoomGain, 10);
        _time.Advance(QueueTimeout - TimeSpan.FromSeconds(1));
        _dsp.SetLevel(MicGain, 20);
        _time.Advance(TimeSpan.FromSeconds(1));

        LogOn();

        Assert.Equal(["""{"Name":"Mic 1","Controls":[{"Name":"gain","Position":0.2}]}"""], Params("Component.Set"));
    }

    [Fact]
    public void ASetDroppedWhileHeldForTheLogon_IsWrittenOntoItsAuditEntry()
    {
        var target = $"QrcHeldSet-{Guid.NewGuid()}";
        Trigger.Start();
        Audit.Record(target, "Set level", "Room.gain 10");
        _dsp.SetLevel(RoomGain, 10);
        _time.Advance(QueueTimeout);

        LogOn();

        var outcome = Assert.Single(Assert.Single(Audit.GetEntries(), entry => entry.Target == target).Outcomes);
        Assert.Equal((AuditOutcomeKind.Dropped, _dsp.Name, "Expired while held for the logon after 5 s, timeout 5 s: Room.gain {\"Position\":0.1}"),
            (outcome.Kind, outcome.Source, outcome.Detail));
    }

    [Fact]
    public async Task ASetDroppedWhileHeldForTheComponentList_SaysSoOnItsAuditEntry()
    {
        LogOn();
        var target = $"QrcComponentListHold-{Guid.NewGuid()}";
        Trigger.Start();
        Audit.Record(target, "Set level", "Lobby.Zone 1.gain 42");
        _dsp.SetLevel(LobbyZoneGain, 42);
        _time.Advance(ReplyTimeout);

        await _dsp.PollNow();

        var outcome = Assert.Single(Assert.Single(Audit.GetEntries(), entry => entry.Target == target).Outcomes,
            outcome => outcome.Kind == AuditOutcomeKind.Dropped);
        Assert.Equal("Expired while held for the Core's component list after 5 s, timeout 5 s: Lobby.Zone 1.gain {\"Position\":0.42}",
            outcome.Detail);
    }

    [Fact]
    public void AnErrorReplyEndingInALineBreak_IsWrittenOntoItsAuditEntryWithTheBreakVisible()
    {
        LogOn();
        var target = $"QrcEscapedRejection-{Guid.NewGuid()}";
        Trigger.Start();
        Audit.Record(target, "Set level", "Room.gain 42");
        _dsp.SetLevel(RoomGain, 42);
        var error = ErrorReply(IdOf("Component.Set"), 8, "Unknown control");

        Receive(error + "\r\n");

        var outcome = Assert.Single(Assert.Single(Audit.GetEntries(), entry => entry.Target == target).Outcomes);
        Assert.Equal((AuditOutcomeKind.Rejected, error + @"\x0D\x0A"), (outcome.Kind, outcome.Detail));
    }

    [Fact]
    public void ASetRejectedForALogon_IsDropped_WhenTheLogonEndsAfterTheClientsQueueTimeout()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 42);
        Fail("Component.Set", QsysQrc.LogonRequiredErrorCode, "Logon required");

        _time.Advance(QueueTimeout);
        Answer("Logon", "true");

        Assert.Single(Params("Component.Set"));
    }

    [Fact]
    public void ASetMadeWhileDisconnected_GoesStraightToTheClient_AndIsNotSentAgainAfterTheNextLogon()
    {
        LogOn();
        _client.SetConnectionState(ConnectionState.Disconnected);

        _dsp.SetLevel(RoomGain, 42);
        Assert.Equal("Component.Set", Methods.Last());
        _client.SetConnectionState(ConnectionState.Connected);
        LogOn();

        Assert.Single(Params("Component.Set"));
    }

    [Fact]
    public void ASetMadeWhileDisconnected_ReplacesAnOlderSetForTheSameControlThatWaitedForTheLogon()
    {
        _dsp.SetLevel(RoomGain, 10);
        _client.SetConnectionState(ConnectionState.Disconnected);

        _dsp.SetLevel(RoomGain, 30);
        _client.SetConnectionState(ConnectionState.Connected);
        LogOn();

        Assert.Equal(["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.3}]}"""], Params("Component.Set"));
    }

    [Fact]
    public void APersonsSet_WhileDisconnectedOnRequest_ReachesTheClientsSend_AndReconnectsIt()
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, "", "", _time);
        client.Reply(EngineStatus + Delimiter);
        AsAPerson.Run(client.Disconnect);

        AsAPerson.Run(() => dsp.SetLevel(RoomGain, 42));

        Assert.Equal(1, client.Connects);
        Assert.Equal(["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}"""], ParamsOf(client, "Component.Set"));
    }

    [Fact]
    public void ASetNoPersonMade_WhileDisconnectedOnRequest_DoesNotReconnectTheClient()
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, "", "", _time);
        client.Reply(EngineStatus + Delimiter);
        AsAPerson.Run(client.Disconnect);

        dsp.SetLevel(RoomGain, 42);

        Assert.Equal(0, client.Connects);
        Assert.DoesNotContain("Component.Set", MethodsOf(client));
    }

    [Fact]
    public async Task APoll_WhileDisconnectedOnRequest_SendsNothing_SoItCannotReconnectTheClient()
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, "", "", _time);
        client.Reply(EngineStatus + Delimiter);
        AsAPerson.Run(client.Disconnect);
        var sentBefore = client.Sent.Count;

        await dsp.PollNow();

        Assert.Equal(0, client.Connects);
        Assert.Equal(sentBefore, client.Sent.Count);
    }

    [Fact]
    public void APersonsSetThatWaitedForTheReconnect_TakesItsReplyWithinTheReplyTimeoutOfItsWrite_AndIsResentAfterTheLogon()
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, Username, Password, _time);
        client.Reply(EngineStatus + Delimiter);
        client.Reply(ResultReply(IdIn(client, "Logon"), "true") + Delimiter);
        AsAPerson.Run(client.Disconnect);
        AsAPerson.Run(() => dsp.SetLevel(RoomGain, 42));

        _time.Advance(TimeSpan.FromSeconds(12));
        client.SetConnectionState(ConnectionState.Connected);
        client.WriteQueued();
        _time.Advance(TimeSpan.FromSeconds(1));
        client.Reply(ErrorReply(IdIn(client, "Component.Set"), QsysQrc.LogonRequiredErrorCode, "Logon required") + Delimiter);
        client.Reply(ResultReply(IdIn(client, "Logon"), "true") + Delimiter);

        Assert.Equal(["Logon", "ChangeGroup.AutoPoll", "Component.Set", "Logon", "ChangeGroup.AutoPoll", "Component.Set"], MethodsOf(client));
    }

    [Theory]
    [InlineData(ActorKind.Schedule)]
    [InlineData(ActorKind.System)]
    public void ASetByTheProgramDuringAPersonsReconnect_KeepsTheQueueTimeout_AndIsDroppedAfterTheLogon(ActorKind kind)
    {
        var client = new OnRequestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, Username, Password, _time);
        client.Reply(EngineStatus + Delimiter);
        client.Reply(ResultReply(IdIn(client, "Logon"), "true") + Delimiter);
        AsAPerson.Run(client.Disconnect);
        AsAPerson.Run(() => dsp.SetLevel(RoomGain, 42));
        using (Actor.Begin(new Actor(kind, string.Empty, string.Empty, null, string.Empty) { Name = "DailyShutdown" }))
            dsp.SetLevel(MicGain, 20);

        _time.Advance(TimeSpan.FromSeconds(12));
        client.SetConnectionState(ConnectionState.Connected);
        client.WriteQueued();
        _time.Advance(TimeSpan.FromSeconds(1));
        foreach (var id in RequestsOf(client).Where(request => request["method"]!.ToString() == "Component.Set").Select(request => (int)request["id"]!).ToList())
            client.Reply(ErrorReply(id, QsysQrc.LogonRequiredErrorCode, "Logon required") + Delimiter);
        client.Reply(ResultReply(IdIn(client, "Logon"), "true") + Delimiter);

        Assert.Equal(
            ["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}""", """{"Name":"Mic 1","Controls":[{"Name":"gain","Position":0.2}]}""",
             """{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}"""],
            ParamsOf(client, "Component.Set"));
    }

    [Fact]
    public void ASetSentJustBeforeADrop_RejectedForALogonAfterTheReconnect_IsSentAgainAfterTheLogon()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 42);
        var set = IdOf("Component.Set");
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);

        FailId(set, QsysQrc.LogonRequiredErrorCode);
        Answer("Logon", "true");

        Assert.DoesNotContain(_dsp.GetIssues(), issue => issue.Key == QsysQrc.RejectedIssueKey);
        Assert.Equal(
            ["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}""", """{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}"""],
            Params("Component.Set"));
    }

    [Fact]
    public async Task Poll_SendsAStatusRequest_AndThreeMissedRepliesReconnect()
    {
        for (var poll = 0; poll < QsysQrc.MaxUnansweredStatusRequests; poll++)
            await _dsp.PollNow();
        Assert.Equal(0, _client.Reconnects);
        Assert.Equal(["StatusGet", "StatusGet", "StatusGet"], Methods);

        await _dsp.PollNow();

        Assert.Equal(1, _client.Reconnects);
        Assert.Equal(CommunicationState.Error, _dsp.CommunicationState);
        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.UnansweredStatusIssueKey);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
    }

    [Fact]
    public async Task Poll_GivenAnsweredStatusRequests_RaisesNothing()
    {
        for (var poll = 0; poll < QsysQrc.MaxUnansweredStatusRequests + 1; poll++)
        {
            await _dsp.PollNow();
            Receive($$$"""{"jsonrpc":"2.0","id":{{{IdOf("StatusGet")}}},"result":{"State":"Active","DesignName":"Test Design","DesignCode":"aBcD1234eFgH"}}""");
        }

        Assert.DoesNotContain(_dsp.GetIssues(), issue => issue.Key == QsysQrc.UnansweredStatusIssueKey);
        Assert.Equal(0, _client.Reconnects);
    }

    [Fact]
    public async Task Poll_WhileDisconnected_SendsNothing()
    {
        _client.SetConnectionState(ConnectionState.Disconnected);

        await _dsp.PollNow();

        Assert.Empty(_client.Sent);
    }

    [Fact]
    public void TheQsysVolumeControls_DriveAQrcCore()
    {
        var room = new QsysVolumeControl(new QscAudioBlockInfo("Room", RoomGain, RoomMute), VolumeType.Speaker, _dsp);
        var mic = new QsysMuteOnlyControl("Mic", "Mic 1.mute", VolumeType.Microphone, _dsp);
        LogOn();
        Receive(RoomAt42AndMuted);

        room.SetLevel(60);
        mic.SetAudioMute(MuteState.On);

        Assert.Equal(42, room.Volume);
        Assert.Equal(MuteState.On, room.MuteState);
        Assert.Equal(
            ["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.6}]}""", """{"Name":"Mic 1","Controls":[{"Name":"mute","Value":1}]}"""],
            Params("Component.Set"));
    }

    [Fact]
    public void ALogonResultOtherThanTrue_RaisesTheCriticalLoginIssue_AndKeepsTheCommunicationStateInError()
    {
        _dsp.SetLevel(RoomGain, 42);
        Receive(EngineStatus);
        var refusal = $"{{\"jsonrpc\":\"2.0\",\"id\":{IdOf("Logon")},\"result\":false}}";

        Receive(refusal);
        Receive(RoomAt42AndMuted);

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal($"The Core refused the logon as {Username}: {refusal}", issue.Message);
        Assert.Equal(CommunicationState.Error, _dsp.CommunicationState);
        Assert.Equal(["Logon"], Methods);
    }

    [Fact]
    public void ARefusedLogon_StaysOneLoginIssueAndOneCommunicationIssue_AcrossReconnects_UntilALogonSucceeds()
    {
        Receive(EngineStatus);
        Answer("Logon", "false");
        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        Receive(EngineStatus);
        Answer("Logon", "false");

        Assert.Equal(2, Methods.Count(method => method == "Logon"));
        Assert.Equal(IssueStatus.Ongoing, Assert.Single(_dsp.GetIssues(), issue => issue.Key == QsysQrc.LoginIssueKey).Status);
        Assert.Equal(IssueStatus.Ongoing, Assert.Single(_dsp.GetIssues(), issue => issue.Key == CommunicationIssueKey).Status);

        _client.SetConnectionState(ConnectionState.Disconnected);
        _client.SetConnectionState(ConnectionState.Connected);
        LogOn();

        Assert.DoesNotContain(_dsp.GetOngoingIssues(), issue => issue.Key is QsysQrc.LoginIssueKey or CommunicationIssueKey);
        Assert.Equal(CommunicationState.Okay, _dsp.CommunicationState);
    }

    [Fact]
    public async Task AnUnansweredLogon_RaisesTheCriticalLoginIssue_ShowingTheMaskedLogon_AndReconnects()
    {
        Receive(EngineStatus);
        var logonId = IdOf("Logon");

        _time.Advance(ReplyTimeout);
        await _dsp.PollNow();

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal(
            $$$"""The Core did not answer the logon as {{{Username}}} within {{{ReplyTimeout.TotalSeconds}}} s, so the driver is reconnecting: {"jsonrpc":"2.0","id":{{{logonId}}},"method":"Logon","params":{"User":"{{{Username}}}","Password":"***"}}""",
            issue.Message);
        Assert.Equal(CommunicationState.Error, _dsp.CommunicationState);
        Assert.Equal(1, _client.Reconnects);
    }

    [Fact]
    public async Task AnAnsweredLogon_IsNotReportedUnanswered_OnceItsReplyTimeoutHasPassed()
    {
        LogOn();

        _time.Advance(ReplyTimeout);
        await _dsp.PollNow();

        Assert.DoesNotContain(_dsp.GetIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
        Assert.Equal(0, _client.Reconnects);
    }

    [Fact]
    public void ALogonRequiredErrorOnALaterSet_LogsOnAgain_AndSendsTheSetAfterIt()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 42);

        Fail("Component.Set", QsysQrc.LogonRequiredErrorCode, "Logon required");
        Assert.Equal("Logon", Methods.Last());
        Answer("Logon", "true");

        Assert.Equal(
            ["""{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}""", """{"Name":"Room","Controls":[{"Name":"gain","Position":0.42}]}"""],
            Params("Component.Set"));
        Assert.Equal("Component.Set", Methods.Last());
        Assert.DoesNotContain(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
    }

    [Fact]
    public void ALogonRequiredErrorWithNoLogin_RaisesTheCriticalLoginIssue()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, "", "", _time);
        client.Reply(EngineStatus + Delimiter);
        dsp.SetLevel(RoomGain, 42);

        client.Reply(ErrorReply(IdIn(client, "Component.Set"), QsysQrc.LogonRequiredErrorCode, "Logon required") + Delimiter);

        var issue = Assert.Single(dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
        Assert.StartsWith("The Core needs a logon, and no username or password is set: ", issue.Message);
        Assert.Equal(CommunicationState.Error, dsp.CommunicationState);
    }

    [Fact]
    public void AnotherActiveEngineStatus_RebuildsTheChangeGroup()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        LogOn();

        Receive(EngineStatus);

        Assert.Equal(["Logon", "ChangeGroup.AddComponentControl", "ChangeGroup.AutoPoll", "ChangeGroup.AddComponentControl", "ChangeGroup.AutoPoll"],
            Methods);
    }

    [Fact]
    public async Task AStatusWithANewDesignCode_RebuildsTheChangeGroup_AndTheSameCodeDoesNot()
    {
        _dsp.AddControl(_levelHandler.Object, RoomGain);
        LogOn();

        await _dsp.PollNow();
        Answer("StatusGet", StatusResult("aBcD1234eFgH"));
        Assert.Equal(1, Methods.Count(method => method == "ChangeGroup.AutoPoll"));

        await _dsp.PollNow();
        Answer("StatusGet", StatusResult("zYxW9876vUtS"));

        Assert.Equal(2, Methods.Count(method => method == "ChangeGroup.AddComponentControl"));
        Assert.Equal(2, Methods.Count(method => method == "ChangeGroup.AutoPoll"));
        Assert.Equal(new DeviceDetail("Design code", "zYxW9876vUtS"), Detail("Design code"));
    }

    [Fact]
    public void ARejectedComponentWithASpaceInItsName_NamesTheWholeControl_UntilItReports()
    {
        _dsp.AddControl(_levelHandler.Object, MicGain);
        LogOn();

        Fail("ChangeGroup.AddComponentControl", QsysQrc.UnknownComponentErrorCode, "Unknown component name");

        Assert.Equal([$"{QsysQrc.BadControlIssuePrefix}{MicGain}"], _dsp.GetOngoingIssues().Select(issue => issue.Key));
        Assert.Equal(new DeviceDetail(MissingControlsLabel, MicGain, DetailTone.Warning), Detail(MissingControlsLabel));

        Receive(Mic1At50);

        Assert.Empty(_dsp.GetOngoingIssues());
        Assert.Null(Detail(MissingControlsLabel));
        Assert.Equal(50, _dsp.GetLevel(MicGain));
    }

    [Fact]
    public void HeldSets_KeepOnlyTheLatestPerControl_InTheOrderTheyWereMade()
    {
        _dsp.SetLevel(RoomGain, 10);
        _dsp.SetLevel(MicGain, 20);
        _dsp.SetLevel(RoomGain, 30);

        LogOn();

        Assert.Equal(
            ["""{"Name":"Mic 1","Controls":[{"Name":"gain","Position":0.2}]}""", """{"Name":"Room","Controls":[{"Name":"gain","Position":0.3}]}"""],
            Params("Component.Set"));
    }

    [Fact]
    public async Task BytesWithNoNulEnd_AreShownInTheUnansweredStatusIssue()
    {
        var unframed = Encoding.ASCII.GetBytes("sr \"Design\" 1 1\r\n");
        _client.ReplyBytes(unframed);

        await _dsp.PollNow();
        await _dsp.PollNow();

        var issue = Assert.Single(_dsp.GetIssues(), issue => issue.Key == QsysQrc.UnansweredStatusIssueKey);
        Assert.Equal($"The Core did not answer the status request (it sent {unframed.Length} bytes with no NUL message end: sr \"Design\" 1 1\\x0D\\x0A)",
            issue.Message);
    }

    [Fact]
    public void Replies_AreDecodedAsUtf8()
    {
        var label = new Mock<StringValueHandler>();
        _dsp.AddControl(label.Object, "Room.label");

        _client.ReplyBytes(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","method":"ChangeGroup.Poll","params":{"Id":"AVCoders","Changes":[{"Component":"Room","Name":"label","String":"Präsentation","Value":0,"Position":0}]}}""" + Delimiter));

        Assert.Equal("Präsentation", _dsp.GetValue("Room.label"));
    }

    [Fact]
    public void ALogonRefusedWithLogonRequired_IsTriedOnceMore_ThenRaisesTheCriticalLoginIssue()
    {
        _dsp.SetLevel(RoomGain, 42);
        Receive(EngineStatus);
        Fail("Logon", QsysQrc.LogonRequiredErrorCode, "Logon required");
        Assert.Equal(["Logon", "Logon"], Methods);

        var refusal = Fail("Logon", QsysQrc.LogonRequiredErrorCode, "Logon required");

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal($"The Core refused the logon as {Username} {QsysQrc.MaxLogonAttempts} times on this connection: {refusal}", issue.Message);
        Assert.Equal(CommunicationState.Error, _dsp.CommunicationState);
        Assert.Equal(["Logon", "Logon"], Methods);
    }

    [Fact]
    public void ALogonRefusedWithAnotherError_RaisesTheCriticalLoginIssue_AtOnce()
    {
        Receive(EngineStatus);

        var refusal = Fail("Logon", -32602, "Invalid params");

        Assert.Equal($"The Core refused the logon as {Username}: {refusal}",
            Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey).Message);
        Assert.Equal(["Logon"], Methods);
    }

    [Fact]
    public void ABurstOfLogonRequiredErrors_SendsOneLogon()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 10);
        _dsp.SetLevel(MicGain, 20);
        var sets = IdsOf("Component.Set");

        FailId(sets[0], QsysQrc.LogonRequiredErrorCode);
        FailId(sets[1], QsysQrc.LogonRequiredErrorCode);

        Assert.Equal(2, Methods.Count(method => method == "Logon"));
        Answer("Logon", "true");
        Assert.Equal(
            [
                """{"Name":"Room","Controls":[{"Name":"gain","Position":0.1}]}""", """{"Name":"Mic 1","Controls":[{"Name":"gain","Position":0.2}]}""",
                """{"Name":"Room","Controls":[{"Name":"gain","Position":0.1}]}""", """{"Name":"Mic 1","Controls":[{"Name":"gain","Position":0.2}]}"""
            ],
            Params("Component.Set"));
    }

    [Fact]
    public async Task LogonsPastTheCapWithNoCommandAnsweredBetween_RaiseTheCriticalLoginIssue_AndStopUntilTheNextPoll()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 10);
        FailId(IdsOf("Component.Set").Last(), QsysQrc.LogonRequiredErrorCode);
        Answer("Logon", "true");
        _dsp.SetLevel(RoomGain, 20);

        var refusal = FailId(IdsOf("Component.Set").Last(), QsysQrc.LogonRequiredErrorCode);
        _dsp.SetLevel(RoomGain, 30);

        var issue = Assert.Single(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal($"The Core asked for a logon again after {QsysQrc.MaxLogonAttempts} logons on this connection: {refusal}", issue.Message);
        Assert.Equal(QsysQrc.MaxLogonAttempts, Methods.Count(method => method == "Logon"));
        Assert.Equal("Component.Set", Methods.Last());

        await _dsp.PollNow();
        Answer("StatusGet", StatusResult("aBcD1234eFgH"));

        Assert.Equal("Logon", Methods.Last());
    }

    [Fact]
    public void ALogonRequiredErrorAfterTheCoreAnsweredACommand_LogsOnAgain_PastTwoLogonsOnTheConnection()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 10);
        FailId(IdOf("Component.Set"), QsysQrc.LogonRequiredErrorCode);
        Answer("Logon", "true");
        Answer("Component.Set", "true");
        _dsp.SetLevel(RoomGain, 20);

        FailId(IdOf("Component.Set"), QsysQrc.LogonRequiredErrorCode);

        Assert.Equal(3, Methods.Count(method => method == "Logon"));
        Assert.DoesNotContain(_dsp.GetIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
    }

    [Fact]
    public async Task ALogonTheCoreRefused_IsTriedAgainAfterTheNextPoll()
    {
        Receive(EngineStatus);
        Answer("Logon", "false");

        await _dsp.PollNow();
        Answer("StatusGet", StatusResult("aBcD1234eFgH"));

        Assert.Equal(["Logon", "StatusGet", "Logon"], Methods);
        Answer("Logon", "true");
        Assert.DoesNotContain(_dsp.GetOngoingIssues(), issue => issue.Key == QsysQrc.LoginIssueKey);
    }

    [Fact]
    public async Task Reinitialise_TriesALogonTheCoreRefusedAgainAtOnce()
    {
        Receive(EngineStatus);
        Answer("Logon", "false");

        await _dsp.Reinitialise();

        Assert.Equal(["Logon", "Logon"], Methods);
    }

    [Fact]
    public void ARejectedSet_DoesNotReplaceANewerHeldSetForTheSameControl()
    {
        LogOn();
        _dsp.SetLevel(RoomGain, 20);
        _dsp.SetLevel(MicGain, 50);
        var sets = IdsOf("Component.Set");
        FailId(sets[1], QsysQrc.LogonRequiredErrorCode);
        _dsp.SetLevel(RoomGain, 30);

        FailId(sets[0], QsysQrc.LogonRequiredErrorCode);
        Answer("Logon", "true");

        Assert.Equal(
            [
                """{"Name":"Room","Controls":[{"Name":"gain","Position":0.2}]}""", """{"Name":"Mic 1","Controls":[{"Name":"gain","Position":0.5}]}""",
                """{"Name":"Mic 1","Controls":[{"Name":"gain","Position":0.5}]}""", """{"Name":"Room","Controls":[{"Name":"gain","Position":0.3}]}"""
            ],
            Params("Component.Set"));
    }

    [Fact]
    public async Task ADisconnectDuringASend_DoesNotDeadlock()
    {
        using var client = new ConnectionLockingTcpClient();
        client.SetConnectionState(ConnectionState.Connected);
        var dsp = new TestableQsysQrc(client, "", "", _time);
        client.Reply(EngineStatus + Delimiter);

        await Task.Run(() => client.DisconnectWhileSending(() => dsp.SetLevel(RoomGain, 42))).WaitAsync(DeadlockTimeout);

        Assert.Equal(ConnectionState.Disconnected, client.ConnectionState);
        Assert.Contains(client.Sent, sent => sent.Contains("\"Position\":0.42"));
    }

    [Collection(LoggerFactoryCollection.Name)]
    public class LogTest
    {
        private readonly FakeTimeProvider _time = new();

        private (T Client, TestableQsysQrc Dsp) Create<T>(ScopeCapturingLoggerFactory logs, Func<T> newClient, string username,
            string password) where T : TestTcpClient =>
            Logged.Create(logs, () =>
            {
                var client = newClient();
                client.SetConnectionState(ConnectionState.Connected);
                return (client, new TestableQsysQrc(client, username, password, _time));
            });

        [Fact]
        public void ThePassword_GoesToTheCore_ButNeverReachesTheLogsOrTheRequestHandlers()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, _) = Create(logs, () => new TestTcpClient(), Username, Password);
            List<string> requests = [];
            List<byte[]> requestBytes = [];
            client.RequestHandlers += requests.Add;
            client.RequestByteHandlers += requestBytes.Add;

            client.Reply(EngineStatus + Delimiter);

            Assert.Contains($"\"Password\":\"{Password}\"", Assert.Single(client.Sent));
            Assert.Contains("\"Password\":\"***\"", Assert.Single(requests));
            Assert.Contains("\"Password\":\"***\"", Encoding.Latin1.GetString(Assert.Single(requestBytes)));
            Assert.Equal(2, logs.Logs.Count(log => log.Level == LogLevel.Trace && log.Message.StartsWith("Sending")));
            var passwordHex = BitConverter.ToString(Encoding.ASCII.GetBytes(Password));
            Assert.DoesNotContain(logs.Logs, log => log.Message.Contains(Password) || log.Message.Contains(passwordHex));
            Assert.DoesNotContain(requests, request => request.Contains(Password));
        }

        [Fact]
        public void ARejectedSet_IsReportedUnderTheTriggerIdOfThePressThatMadeIt()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, dsp) = Create(logs, () => new TestTcpClient(), "", "");
            client.Reply(EngineStatus + Delimiter);
            var press = Trigger.Start();
            dsp.SetLevel(RoomGain, 42);

            Trigger.Start();
            client.Reply(ErrorReply(IdIn(client, "Component.Set"), -32602, "Invalid params") + Delimiter);

            var rejection = Assert.Single(logs.Logs, log => log.Message.StartsWith($"The Core rejected Component.Set on {RoomGain}"));
            Assert.Equal(press, rejection.TriggerId);
        }

        [Fact]
        public void APollWhoseParamsIsNotAnObject_IsLoggedRaw()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, _) = Create(logs, () => new TestTcpClient(), "", "");

            client.Reply(PollWithAnArrayForParams + Delimiter);

            Assert.Contains(logs.Logs, log => log.Level == LogLevel.Warning && log.Message.EndsWith(PollWithAnArrayForParams));
        }

        [Fact]
        public void AHandlerThatThrows_IsLoggedAsAnErrorWithTheRawReply()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, dsp) = Create(logs, () => new TestTcpClient(), "", "");
            dsp.AddControl((VolumeLevelHandler)(_ => throw new InvalidOperationException("subscriber failed")), RoomGain);

            client.Reply(RoomAt42AndMuted + Delimiter);

            Assert.Contains(logs.Logs, log => log.Level == LogLevel.Error && log.Message.Contains(RoomAt42AndMuted));
            Assert.DoesNotContain(dsp.GetIssues(), issue => issue.Key == QsysQrc.UnreadableReplyIssueKey);
        }

        [Fact]
        public async Task ASetTheClientNeverWrote_IsNeverReportedUnanswered()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, dsp) = Create(logs, () => new TestTcpClient(), "", "");
            client.Reply(EngineStatus + Delimiter);
            client.SetConnectionState(ConnectionState.Disconnected);
            dsp.SetLevel(RoomGain, 42);

            client.DropQueued();
            _time.Advance(TimeSpan.FromSeconds(client.QueueTimeoutFor(true)));
            await dsp.PollNow();
            _time.Advance(ReplyTimeout);
            await dsp.PollNow();

            Assert.DoesNotContain(logs.Logs, log => log.Message.StartsWith("No reply from the Core to Component.Set"));
            Assert.DoesNotContain(logs.Logs, log => log.Message.StartsWith("Forgot"));
        }

        [Fact]
        public async Task ASetTheClientWroteAndTheCoreNeverAnswered_IsReportedUnanswered()
        {
            using var logs = new ScopeCapturingLoggerFactory();
            var (client, dsp) = Create(logs, () => new TestTcpClient(), "", "");
            client.Reply(EngineStatus + Delimiter);
            dsp.SetLevel(RoomGain, 42);

            _time.Advance(ReplyTimeout);
            await dsp.PollNow();

            Assert.Contains(logs.Logs, log => log.Message.StartsWith($"No reply from the Core to Component.Set on {RoomGain}"));
        }
    }

    private sealed class ConnectionLockingTcpClient : TestTcpClient, IDisposable
    {
        private static readonly TimeSpan SendStartTimeout = TimeSpan.FromSeconds(5);
        private readonly ManualResetEventSlim _sending = new();

        public override void Send(string message)
        {
            _sending.Set();
            lock (ConnectionRequestLock)
                base.Send(message);
        }

        public async Task DisconnectWhileSending(Action send)
        {
            _sending.Reset();
            Task sending;
            lock (ConnectionRequestLock)
            {
                sending = Task.Run(send);
                Assert.True(_sending.Wait(SendStartTimeout), "the send never reached the client");
                ConnectionState = ConnectionState.Disconnected;
            }
            await sending;
        }

        public void Dispose() => _sending.Dispose();
    }

    private class TestableQsysQrc(TcpClient client, string username, string password, TimeProvider time)
        : QsysQrc(client, "Test", username, password, NeverPollMs, time)
    {
        public Task PollNow() => Poll(CancellationToken.None);
    }
}
