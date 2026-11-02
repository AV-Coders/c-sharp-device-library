using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.MediaPlayer.Tests;

public class ExterityTciTest
{
    private readonly FakeTimeProvider _time = new();
    private readonly ExterityTci _interface;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly string _password = "Password1";
    private static readonly TimeSpan LoginSettle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HalfLoginSettle = LoginSettle / 2;

    private static readonly RemoteButton[] _expectedButtons =
    [
        RemoteButton.Enter, RemoteButton.Button1, RemoteButton.Button2, RemoteButton.Button3, RemoteButton.Button4,
        RemoteButton.Button5, RemoteButton.Button6, RemoteButton.Button7, RemoteButton.Button8, RemoteButton.Button9,
        RemoteButton.Button0, RemoteButton.Up, RemoteButton.Down, RemoteButton.Left, RemoteButton.Right,
        RemoteButton.Subtitle, RemoteButton.Back, RemoteButton.VolumeUp, RemoteButton.VolumeDown, RemoteButton.Mute,
        RemoteButton.Power, RemoteButton.ChannelUp, RemoteButton.ChannelDown, RemoteButton.Play, RemoteButton.Pause,
        RemoteButton.Stop, RemoteButton.Rewind, RemoteButton.FastForward, RemoteButton.Next, RemoteButton.Previous,
        RemoteButton.Home, RemoteButton.Blue, RemoteButton.Yellow, RemoteButton.Green, RemoteButton.Red,
        RemoteButton.Guide, RemoteButton.Menu, RemoteButton.Eject, RemoteButton.TopMenu, RemoteButton.PopupMenu,
        RemoteButton.Display, RemoteButton.PowerOn, RemoteButton.PowerOff
    ];

    private static readonly string[] IdentityAndHealthReads =
    [
        "^get:softwareVersion!\n", "^get:serialNumber!\n", "^get:macAddress!\n", "^get:name!\n",
        "^get:IPAddress!\n", "^get:productType!\n", "^get:upTime!\n", "^get:hdmiState!\n"
    ];

    public ExterityTciTest()
    {
        _interface = new ExterityTci(_mockClient.Object, _password, "test device", _time);
    }

    private void Reply(string reply) => _mockClient.Object.ResponseHandlers!.Invoke(reply);

    private string? Detail(string label) => _interface.Details.SingleOrDefault(detail => detail.Label == label)?.Value;

    private Issue? Ongoing(string key) => _interface.GetOngoingIssues().SingleOrDefault(issue => issue.Key == key);

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private List<string> Reads() => _mockClient.Invocations
        .Where(invocation => invocation.Method.Name == nameof(CommunicationClient.Send))
        .Select(invocation => (string)invocation.Arguments[0])
        .Where(command => command.StartsWith("^get:"))
        .ToList();

    private int Sends(string command) => Reads().Count(read => read == command);

    private void Poll() =>
        ((Task)typeof(ExterityTci).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_interface, [CancellationToken.None])!).GetAwaiter().GetResult();

    private ThreadWorker PollWorker => (ThreadWorker)typeof(ExterityTci)
        .GetField("_pollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_interface)!;

    private async Task ConnectAndWaitForTheReadsAsync(int reads)
    {
        await PollWorker.Stop();
        SetConnection(ConnectionState.Connected);
        _time.Advance(LoginSettle);
        await Wait.UntilAsync(() => Reads().Count == reads, $"{reads} reads were never sent");
    }

    private void AnswerEveryReadExcept(params string[] unanswered)
    {
        foreach (var reply in new[]
                 {
                     "^softwareVersion:3.2.1!", "^serialNumber:120A0001!", "^macAddress:00:0e:5d:12:34:56!", "^name:lobby-tv!",
                     "^IPAddress:10.1.2.3!", "^productType:r9210!", "^upTime:1 day 02:03:04!", "^hdmiState:connected!"
                 }.Where(reply => !unanswered.Any(param => reply.StartsWith($"^{param}:"))))
            Reply(reply);
    }

    [Fact]
    public async Task Connection_ReadsTheIdentityAndHealthOnce()
    {
        await ConnectAndWaitForTheReadsAsync(IdentityAndHealthReads.Length);

        Assert.Equal(IdentityAndHealthReads, Reads());
    }

    [Fact]
    public async Task Login_SendsTheFirstReadsOnceTheSettleAfterTheLoginHasPassed()
    {
        await PollWorker.Stop();
        SetConnection(ConnectionState.Connected);
        _time.Advance(HalfLoginSettle);
        Reply("ctrl@10.1.1.1's password:");

        _time.Advance(HalfLoginSettle);

        Assert.False(PollWorker.IsRunning);
        Assert.Empty(Reads());

        _time.Advance(HalfLoginSettle);

        Assert.True(PollWorker.IsRunning);
        await Wait.UntilAsync(() => Reads().Count == IdentityAndHealthReads.Length, "The reads were never sent");
        Assert.Equal(IdentityAndHealthReads, Reads());
    }

    [Theory]
    [InlineData("^softwareVersion:3.2.1!", "Software", "3.2.1")]
    [InlineData("^hdmiState:NULL!", "HDMI", "NULL")]
    public void HandleResponse_ParameterReply_SetsTheDetailAsReceived(string reply, string label, string value)
    {
        Reply(reply);

        Assert.Equal(value, Detail(label));
        Assert.Empty(_interface.GetOngoingIssues());
        Assert.Equal(CommunicationState.Okay, _interface.CommunicationState);
    }

    [Fact]
    public void HandleResponse_ReplySplitAcrossReads_IsHeldUntilItsClosingBang()
    {
        Reply("^name:lob");

        Assert.Null(Detail("Name"));

        Reply("by-tv!\r\n");

        Assert.Equal("lobby-tv", Detail("Name"));
        Assert.Empty(_interface.GetIssues());
    }

    [Fact]
    public void HandleResponse_SeveralRepliesInOneMessage_SetsEachDetail()
    {
        Reply("^name:lobby-tv!^IPAddress:10.1.2.3!\r\n");

        Assert.Equal("lobby-tv", Detail("Name"));
        Assert.Equal("10.1.2.3", Detail("IP Address"));
    }

    [Fact]
    public async Task HandleResponse_ReplyWithNoValue_ShowsTheRawReplyAndCountsAsAnswered()
    {
        await ConnectAndWaitForTheReadsAsync(IdentityAndHealthReads.Length);
        AnswerEveryReadExcept("serialNumber");
        Reply("^serialNumber:!");

        Poll();

        Assert.Equal(new DeviceDetail("Serial", "^serialNumber:!"), _interface.Details.Single(detail => detail.Label == "Serial"));
        Assert.Empty(_interface.GetIssues());
        Assert.Equal(1, Sends("^get:serialNumber!\n"));
    }

    [Theory]
    [InlineData("^get:name!")]
    [InlineData("^currentMode:av!")]
    public void HandleResponse_ReplyThisDriverDoesNotRead_ChangesNoDetail(string reply)
    {
        Reply(reply);

        Assert.Empty(_interface.Details);
        Assert.Empty(_interface.GetIssues());
    }

    [Fact]
    public void HandleResponse_UnreadableText_IsHeldUntilALaterBangThenRaisesAMomentaryIssueWithTheRawText()
    {
        Reply("^garbage\r\n");

        Assert.Empty(_interface.GetIssues());

        Reply("^name:lobby-tv!");

        var issue = Assert.Single(_interface.GetIssues());
        Assert.Equal("unrecognised-reply", issue.Key);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Contains("^garbage", issue.Message);
        Assert.Equal("lobby-tv", Detail("Name"));
    }

    [Fact]
    public async Task Poll_ReadNotAnswered_RaisesItsIssueWithTheIgnoredReplies()
    {
        await ConnectAndWaitForTheReadsAsync(IdentityAndHealthReads.Length);
        AnswerEveryReadExcept("productType");
        Poll();
        Poll();
        Reply("^error:busy!");

        Poll();

        var issue = Assert.Single(_interface.GetOngoingIssues());
        Assert.Equal("productType-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Contains("^get:productType!", issue.Message);
        Assert.Contains("^error:busy!", issue.Message);
    }

    [Fact]
    public async Task HandleResponse_ErrorNamingAnUnansweredRead_ShowsItIsNotSupportedWithNoIssueAndStopsAsking()
    {
        await ConnectAndWaitForTheReadsAsync(IdentityAndHealthReads.Length);
        AnswerEveryReadExcept("hdmiState");

        Reply("^error:unknown parameter hdmiState!");
        Poll();

        Assert.Contains(new DeviceDetail("HDMI", "Not supported: ^error:unknown parameter hdmiState!", DetailTone.Normal), _interface.Details);
        Assert.Empty(_interface.GetIssues());
        Assert.Equal(CommunicationState.Okay, _interface.CommunicationState);
        Assert.Equal(1, Sends("^get:hdmiState!\n"));
    }

    [Fact]
    public async Task Poll_UnansweredRead_IsAskedAgainAndRaisesItsIssueOnTheThirdMiss()
    {
        await ConnectAndWaitForTheReadsAsync(IdentityAndHealthReads.Length);
        AnswerEveryReadExcept("productType");

        Poll();
        Poll();

        Assert.Null(Ongoing("productType-read"));
        Assert.Equal(3, Sends("^get:productType!\n"));
        Assert.Equal(1, Sends("^get:softwareVersion!\n"));

        Poll();

        Assert.Equal(IssueSeverity.Minor, Ongoing("productType-read")!.Severity);
        Assert.Equal("The receiver did not answer ^get:productType! 3 times in a row, the last was no reply",
            Ongoing("productType-read")!.Message);

        Reply("^productType:r9210!");
        Poll();

        Assert.Null(Ongoing("productType-read"));
        Assert.Equal("r9210", Detail("Model"));
        Assert.Equal(4, Sends("^get:productType!\n"));
    }

    [Fact]
    public async Task Reconnection_ReadsEverythingAgain()
    {
        await ConnectAndWaitForTheReadsAsync(IdentityAndHealthReads.Length);
        AnswerEveryReadExcept("hdmiState");
        Poll();

        SetConnection(ConnectionState.Disconnected);
        await ConnectAndWaitForTheReadsAsync(IdentityAndHealthReads.Length * 2 + 2);

        Assert.Equal(3, Sends("^get:hdmiState!\n"));
        Assert.Equal(2, Sends("^get:softwareVersion!\n"));
    }

    [Fact]
    public void Module_EntersThePassword()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("ctrl@10.1.1.1's password:");
        
        _mockClient.Verify(x => x.Send("Password1\n"));
    }

    [Fact]
    public void Module_IgnoresTheHostPartOfThePasswordPrompt()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("ctrl@192.168.0.1's password:");
        
        _mockClient.Verify(x => x.Send("Password1\n"));
    }

    [Fact]
    public void Module_EntersTheUsername()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("login as:");
        
        _mockClient.Verify(x => x.Send("ctrl\n"));
    }

    
    [Fact]
    public void PowerOn_SetsTheModeToAV()
    {
        string expectedCommand = "^set:currentMode:av!\n";
        _interface.PowerOn();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SetsTheModeToOff()
    {
        string expectedCommand = "^set:currentMode:off!\n";
        _interface.PowerOff();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void ChannelUp_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_chup!\n";
        _interface.ChannelUp();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
        
    [Fact]
    public void ChannelDown_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_chdown!\n";
        _interface.ChannelDown();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
        
    [Fact]
    public void VolumeUp_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_volup!\n";
        _interface.VolumeUp();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
        
    [Fact]
    public void VolumeDown_SendsTheCommand()
    {
        string expectedCommand = "^send:rm_voldown!\n";
        _interface.VolumeDown();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, "^set:mute:on!\n")]
    [InlineData(MuteState.Off, "^set:mute:off!\n")]
    public void SetAudioMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _interface.SetAudioMute(state);
        
        _mockClient.Verify(x=> x.Send(expectedCommand));
    }

    [Fact]
    public void SetChannel_SendsTheCommands()
    {
        _interface.SetChannel(123);
        
        _mockClient.Verify(x => x.Send("^send:rm_1!\n"));
        _mockClient.Verify(x => x.Send("^send:rm_2!\n"));
        _mockClient.Verify(x => x.Send("^send:rm_3!\n"));
        _mockClient.Verify(x => x.Send("^send:rm_enter!\n"));
    }

    [Fact]
    public void SetAudioMute_Unknown_LogsAndSendsNothing()
    {
        var client = TestFactory.CreateCommunicationClient();
        var logs = LogCapture.Create(() => new ExterityTci(client.Object, _password, "logged device"),
            out var exterity);

        exterity.SetAudioMute(MuteState.Unknown);

        client.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Contains(logs.Logs, log => log.Message == "Can't set the audio mute to Unknown");
    }

    [Fact]
    public void SetChannel_WithANegativeChannel_LogsAndSendsNothing()
    {
        var client = TestFactory.CreateCommunicationClient();
        var logs = LogCapture.Create(() => new ExterityTci(client.Object, _password, "logged device"),
            out var exterity);

        exterity.SetChannel(-3);

        client.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Contains(logs.Logs, log => log.Message == "Channel -3 is invalid, it can't be negative");
    }

    [Fact]
    public void SendIRCode_SendsTheButtonNameAsARemoteKey()
    {
        _interface.SendIRCode(RemoteButton.Guide);

        _mockClient.Verify(x => x.Send("^send:rm_Guide!\n"), Times.Once);
    }

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(_expectedButtons.OrderBy(b => b), _interface.SupportedButtons.OrderBy(b => b));
        Assert.Equal(_expectedButtons.OrderBy(b => b), Enum.GetValues<RemoteButton>().OrderBy(b => b));
    }
}
