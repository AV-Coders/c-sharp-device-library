using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronIn16xxTest
{
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan VideoMuteStepGap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ReconcileGap = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan QueryReplyTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] SettingsQueries =
    [
        "\u001bMSSAV\r", "\u001bCSSAV\r", "\u001bTSSAV\r", "\u001bNHDCP\r", "\u001bMDUR\r", "\u001bAUSW\r",
        "1*B", "2*B", "3*B", "\u001bSHDCP\r"
    ];
    private const string TemperatureQuery = "\u001b20STAT\r";
    private static readonly string[] IdentityQueries = ["*Q", "N", "\u001bCN\r", "\u001bCH\r"];
    internal static readonly string[] ConnectReplies = ["Vrb3", "Vid3 Aud3 Typ6 Std0 Blk0 Hrt031.5 Vrt060.0"];
    internal static readonly string[] StatusReplies =
    [
        "In00 0*0*1*1*0*0", "In03 All", "HdcpI03*2", "HdcpI04*0", "HdcpI05*0", "HdcpI06*0", "HdcpO1*2", "HdcpO2*0", "HdcpO3*0"
    ];
    internal static readonly string[] InputNameReplies =
        ["Nmi01,Input 1", "Nmi02,Input 2", "Nmi03,Codec", "Nmi04,BLURAY", "Nmi05,DOCCAM", "Nmi06,SHARELINK"];
    internal static readonly string[] SettingsReplies =
        ["SsavM1", "SsavC000000", "SsavT501", "HdcpN1", "Mdur003", "Ausw0", "Vmt1*0", "Vmt2*0", "Vmt3*0", "HdcpS0"];
    internal const string TemperatureReply = "20Stat 36";
    internal static readonly string[] IdentityReplies = ["Bld1.20.0003", "Pno60-1081-01", "Ipn IN1608-07-8C-EC", "Iph 00-05-A6-07-8C-EC"];
    private readonly FakeTimeProvider _time = new();
    private readonly ExtronIn16Xx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronIn16xxTest()
    {
        _switcher = new ExtronIn16Xx(_mockClient.Object, 6, "test matrix", _time);
    }

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_mockClient.Object, state);

    private void Respond(string response) => _mockClient.Object.ResponseHandlers!.Invoke(response);

    private void RespondLines(IEnumerable<string> replies)
    {
        foreach (var reply in replies)
            Respond($"{reply}\r\n");
    }

    private Task Poll() => (Task)typeof(ExtronIn16Xx)
        .GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    private bool Sent(string command) => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], command));

    private async Task Connect()
    {
        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap, () => Sent("\u001bCH\r"), "The identity was never asked for");
    }

    private void AnswerThePollUpToTheTemperature() => RespondLines(["Vrb3", ..StatusReplies, ..SettingsReplies]);

    private ConcurrentQueue<string> RecordSends(string heldCommand, ManualResetEventSlim release)
    {
        var sent = new ConcurrentQueue<string>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(command =>
        {
            sent.Enqueue(command);
            if (command == heldCommand)
                release.Wait(HoldTimeout);
        });
        return sent;
    }

    private List<string> RecordSends()
    {
        var sent = new List<string>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(sent.Add);
        return sent;
    }

    private DeviceDetail? Detail(string label) => _switcher.Details.SingleOrDefault(detail => detail.Label == label);

    [Fact]
    public void FailedSend_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        _switcher.RouteAV(1, 1);

        Assert.Equal(CommunicationState.Error, _switcher.CommunicationState);
    }

    [Theory]
    [InlineData(1, "1%")]
    [InlineData(6, "6%")]
    public void RouteVideo_SendsTheCommandWhateverTheOutput(int input, string expectedRouteCommand)
    {
        _switcher.RouteVideo(input, 6);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void RouteVideo_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteVideo(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1$")]
    [InlineData(6, "6$")]
    public void RouteAudio_SendsTheCommandWhateverTheOutput(int input, string expectedRouteCommand)
    {
        _switcher.RouteAudio(input, 10);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void RouteAudio_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAudio(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1!")]
    [InlineData(6, "6!")]
    public void RouteAV_SendsTheCommandWhateverTheOutputAndReportsCommunicationIsOkay(int input, string expectedRouteCommand)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public void RouteAV_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SetSyncTimeout_SendsTheCommand()
    {
        string expectedCommand = "\u001bT0SSAV\r";
        _switcher.SetSyncTimeout(0);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_RefusesALongerTimeoutAndStatesTheLimit()
    {
        _switcher.SetSyncTimeout(502);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Contains(_switcher.Events,
            e => e.Type == EventType.Error && e.Info == "The sync timeout can't be longer than 501 seconds");
    }

    [Fact]
    public void SetSyncTimeout_SetsToNeverDropSync()
    {
        string expectedCommand = "\u001bT501SSAV\r";
        _switcher.SetSyncTimeout(501);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SignalPresence_DiscoversTheInputsAndSetsTheirSignalState()
    {
        Respond("In00 1*0*0*1*0*1*0*0\r\n");

        Assert.Equal(8, _switcher.NumberOfInputs);
        Assert.Equal(Enumerable.Range(1, 8), _switcher.Inputs.Select(input => input.Number));
        Assert.Equal(
            [
                ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Disconnected, ConnectionState.Connected,
                ConnectionState.Disconnected, ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Disconnected
            ],
            _switcher.GetInputs().Select(input => input.InputConnectionStatus));
    }

    [Fact]
    public void SignalPresence_KeepsTheInputsAndNotifiesSubscribersOnlyWhenTheCountChanges()
    {
        var seen = new List<int>();
        _switcher.EndpointsChangedHandlers += () => seen.Add(_switcher.GetInputs().Count);
        Respond("In00 1*0*0*0*0*0\r\n");
        var first = _switcher.GetInputs()[0];

        Respond("In00 0*0*0*0*0*0\r\n");

        Assert.Equal([6], seen);
        Assert.Same(first, _switcher.GetInputs()[0]);
        Assert.Equal(ConnectionState.Disconnected, first.InputConnectionStatus);
    }

    [Theory]
    [InlineData("In1 All")]
    [InlineData("Vid1 Aud1 Typ6")]
    [InlineData("Reconfig")]
    public void OtherResponses_LeaveTheInputsAlone(string response)
    {
        Respond(response);

        Assert.Empty(_switcher.GetInputs());
    }

    [Fact]
    public async Task Heartbeat_QueriesSignalPresenceOnlyWhileConnected()
    {
        await Poll();
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);

        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001b0LS\r")),
            "The signal presence was never asked for");
        _mockClient.Invocations.Clear();
        await Poll();

        _mockClient.Verify(x => x.Send("\u001bCV\r"), Times.Once);
        _mockClient.Verify(x => x.Send("\u001b0LS\r"), Times.Once);
    }

    [Fact]
    public async Task Connect_EnablesVerboseModeThenRequestsInformationWithoutBlockingTheClient()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("\u001b3CV\r", release);

        var connect = Task.Run(() => SetConnection(ConnectionState.Connected));

        await connect.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, ConnectStepGap, () => sent.Count == 26, "The identity was never asked for");
        Assert.Equal(
            [
                "\u001b3CV\r", "I", "\u001b0LS\r", "!",
                "\u001bI3HDCP\r", "\u001bI4HDCP\r", "\u001bI5HDCP\r", "\u001bI6HDCP\r",
                "\u001bO1HDCP\r", "\u001bO2HDCP\r", "\u001bO3HDCP\r",
                ..SettingsQueries, TemperatureQuery, ..IdentityQueries
            ],
            sent);
    }

    [Fact]
    public async Task Heartbeat_ReadsTheSettingsTheTemperatureAndOnlyTheIdentityNotYetShown()
    {
        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001bCH\r")),
            "The identity was never asked for");
        Respond("Bld1.20.0003\r\nPno60-1081-01\r\nIpn IN1608-07-8C-EC\r\n");
        var sent = RecordSends();
        _time.Advance(QueryReplyTimeout);

        await Poll();

        Assert.Equal([..SettingsQueries, TemperatureQuery, "\u001bCH\r"], sent.SkipWhile(command => command != "\u001bO3HDCP\r").Skip(1));
    }

    [Theory]
    [InlineData("(c) Copyright 2015, Extron Electronics, IN1608, V1.20, 60-1081-01", "IN1608")]
    [InlineData("© Copyright 2016, Extron Electronics, IN1606, V1.20, 60-1080-01", "IN1606")]
    public void CopyrightBanner_ShowsTheModel(string banner, string expectedModel)
    {
        Respond($"\r\n{banner}\r\nMon, 18 May 2015 11:27:33\r\n");

        Assert.Equal(new DeviceDetail("Model", expectedModel), Detail("Model"));
    }

    [Fact]
    public void IdentityReplies_AreShownAsReceived()
    {
        Respond("Bld1.20.0003\r\nPno60-1081-01\r\nIpn IN1608-07-8C-EC\r\nIph 00-05-A6-07-8C-EC\r\n20Stat 36\r\n");

        Assert.Equal(
            ["1.20.0003", "60-1081-01", "IN1608-07-8C-EC", "00-05-A6-07-8C-EC", "36"],
            new[] { "Firmware", "Part Number", "Unit Name", "MAC", "Temperature" }.Select(label => Detail(label)?.Value));
    }

    [Fact]
    public void TemperatureReply_WithoutASpace_IsShown()
    {
        Respond("20Stat41\r\n");

        Assert.Equal("41", Detail("Temperature")?.Value);
    }

    [Theory]
    [InlineData("SsavM0", "Screen Saver", "CustomColour")]
    [InlineData("SsavM1", "Screen Saver", "Black")]
    [InlineData("SsavM2", "Screen Saver", "Blue")]
    [InlineData("SsavC00ff00", "Screen Saver Colour", "00FF00")]
    [InlineData("SsavT501", "Sync Timeout", "Never")]
    [InlineData("SsavT010", "Sync Timeout", "10 s")]
    [InlineData("HdcpN0", "HDCP Notification", "Black")]
    [InlineData("HdcpN1", "HDCP Notification", "Green")]
    [InlineData("Mdur003", "OSD Duration", "3 s")]
    [InlineData("Mdur0", "OSD Duration", "Off")]
    [InlineData("Mdur501", "OSD Duration", "Never times out")]
    [InlineData("Ausw0", "Auto Switch", "Off")]
    [InlineData("Ausw1", "Auto Switch", "HighestActiveInput")]
    [InlineData("Ausw2", "Auto Switch", "LowestActiveInput")]
    [InlineData("Vmt1*0", "Video Mute 1A", "Unmuted")]
    [InlineData("Vmt2*1", "Video Mute 1B", "Video muted")]
    [InlineData("Vmt3*2", "Video Mute 1C", "Video and sync muted")]
    [InlineData("HdcpS0", "HDCP Mode", "FollowInput")]
    [InlineData("HdcpS3", "HDCP Mode", "AlwaysEncryptOutputContinuousDviTrials")]
    [InlineData("HdcpS4", "HDCP Mode", "DisableAuthentication")]
    public void SettingReply_IsShownAsADetail(string reply, string label, string expected)
    {
        Respond($"{reply}\r\n");

        Assert.Equal(new DeviceDetail(label, expected), Detail(label));
    }

    [Fact]
    public void AllOutputsVideoMuteReply_SetsEveryOutput()
    {
        Respond("Vmt2\r\n");

        Assert.All(new[] { "Video Mute 1A", "Video Mute 1B", "Video Mute 1C" },
            label => Assert.Equal("Video and sync muted", Detail(label)?.Value));
    }

    [Theory]
    [InlineData("SsavM7", "Screen Saver")]
    [InlineData("SsavCGREEN", "Screen Saver Colour")]
    [InlineData("Mdur502", "OSD Duration")]
    [InlineData("HdcpS", "HDCP Mode")]
    public void UnrecognisedSettingReply_IsShownRaw(string reply, string label)
    {
        Respond($"{reply}\r\n");

        Assert.Equal(new DeviceDetail(label, reply, DetailTone.Warning), Detail(label));
    }

    [Fact]
    public async Task UnsetDesiredValues_NeverSendTheSettings()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        Respond("SsavM2\r\nSsavC00FF00\r\nSsavT0\r\nHdcpN0\r\nMdur0\r\nAusw2\r\nVmt2*2\r\nHdcpS4\r\n");
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*0B", "2*0B", "3*0B"], sent);
        Assert.Null(_switcher.DesiredScreenSaver);
        Assert.Null(_switcher.DesiredScreenSaverColour);
        Assert.Null(_switcher.DesiredSyncTimeout);
        Assert.Null(_switcher.DesiredHdcpNotification);
        Assert.Null(_switcher.DesiredOsdDuration);
        Assert.Null(_switcher.DesiredAutoSwitch);
        Assert.Null(_switcher.DesiredHdcpMode);
    }

    [Fact]
    public void Setters_SendTheDesiredValueAndKeepIt()
    {
        var sent = RecordSends();

        _switcher.SetScreenSaver(ExtronIn16Xx.ScreenSaverMode.CustomColour);
        _switcher.SetScreenSaverColour("ff8000");
        _switcher.SetSyncTimeout(30);
        _switcher.SetHdcpNotification(ExtronIn16Xx.HdcpNotificationMode.Black);
        _switcher.SetOsdDuration(0);
        _switcher.SetAutoSwitch(ExtronIn16Xx.AutoSwitchMode.LowestActiveInput);
        _switcher.SetHdcpMode(ExtronIn16Xx.HdcpOutputMode.DisableAuthentication);

        Assert.Equal(
            [
                "\u001bM0SSAV\r", "\u001bCFF8000SSAV\r", "\u001bT30SSAV\r", "\u001bN0HDCP\r", "\u001b0MDUR\r",
                "\u001b2AUSW\r", "\u001bS4HDCP\r"
            ],
            sent);
        Assert.Equal(ExtronIn16Xx.ScreenSaverMode.CustomColour, _switcher.DesiredScreenSaver);
        Assert.Equal("FF8000", _switcher.DesiredScreenSaverColour);
        Assert.Equal(30, _switcher.DesiredSyncTimeout);
        Assert.Equal(ExtronIn16Xx.HdcpNotificationMode.Black, _switcher.DesiredHdcpNotification);
        Assert.Equal(0, _switcher.DesiredOsdDuration);
        Assert.Equal(ExtronIn16Xx.AutoSwitchMode.LowestActiveInput, _switcher.DesiredAutoSwitch);
        Assert.Equal(ExtronIn16Xx.HdcpOutputMode.DisableAuthentication, _switcher.DesiredHdcpMode);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("GG0000")]
    public void SetScreenSaverColour_RefusesAValueThatIsNotSixHexDigits(string colour)
    {
        _switcher.SetScreenSaverColour(colour);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Null(_switcher.DesiredScreenSaverColour);
        Assert.Contains(_switcher.Events, e => e.Type == EventType.Error && e.Info.EndsWith($"it's {colour}"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(502)]
    public void SetOsdDuration_RefusesAValueOutsideZeroTo501(int seconds)
    {
        _switcher.SetOsdDuration(seconds);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        Assert.Null(_switcher.DesiredOsdDuration);
    }

    [Fact]
    public void DesiredValue_IsShownNextToTheReportedValue()
    {
        _switcher.SetOsdDuration(10);

        Assert.Equal(new DeviceDetail("OSD Duration", "Not read yet, desired 10 s", DetailTone.Warning), Detail("OSD Duration"));

        Respond("Mdur010\r\n");

        Assert.Equal(new DeviceDetail("OSD Duration", "10 s, desired 10 s"), Detail("OSD Duration"));
    }

    [Theory]
    [InlineData("SsavM1", "\u001bM2SSAV\r", "Screen Saver", "Black, desired Blue")]
    [InlineData("SsavC000000", "\u001bC0000FFSSAV\r", "Screen Saver Colour", "000000, desired 0000FF")]
    [InlineData("SsavT501", "\u001bT30SSAV\r", "Sync Timeout", "Never, desired 30 s")]
    [InlineData("HdcpN0", "\u001bN1HDCP\r", "HDCP Notification", "Black, desired Green")]
    [InlineData("Mdur003", "\u001b0MDUR\r", "OSD Duration", "3 s, desired Off")]
    [InlineData("Ausw0", "\u001b1AUSW\r", "Auto Switch", "Off, desired HighestActiveInput")]
    [InlineData("HdcpS0", "\u001bS1HDCP\r", "HDCP Mode", "FollowInput, desired AlwaysEncryptOutput")]
    [InlineData("HdcpS", "\u001bS1HDCP\r", "HDCP Mode", "HdcpS, desired AlwaysEncryptOutput")]
    public async Task MismatchedReply_SendsTheDesiredValue(string reply, string expectedCommand, string label, string expectedDetail)
    {
        _switcher.SetScreenSaver(ExtronIn16Xx.ScreenSaverMode.Blue);
        _switcher.SetScreenSaverColour("0000FF");
        _switcher.SetSyncTimeout(30);
        _switcher.SetHdcpNotification(ExtronIn16Xx.HdcpNotificationMode.Green);
        _switcher.SetOsdDuration(0);
        _switcher.SetAutoSwitch(ExtronIn16Xx.AutoSwitchMode.HighestActiveInput);
        _switcher.SetHdcpMode(ExtronIn16Xx.HdcpOutputMode.AlwaysEncryptOutput);
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        Respond($"{reply}\r\n");

        await Wait.UntilAsync(_time, ReconcileGap, () => sent.Count == 1, "The desired value was never sent");
        Assert.Equal([expectedCommand], sent);
        Assert.Equal(new DeviceDetail(label, expectedDetail, DetailTone.Warning), Detail(label));
        Assert.Contains(_switcher.Events, e => e.Type == EventType.DriverState && e.Info.StartsWith($"Reconciling {label}"));
    }

    [Fact]
    public async Task MismatchedVideoMute_IsSentToThatOutputOnly()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);
        _switcher.SetVideoMute(MuteState.On);
        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");

        Respond("Vmt1*2\r\nVmt2*2\r\nVmt3*0\r\n");

        await Wait.UntilAsync(_time, ReconcileGap, () => sent.Count == 4, "The desired mute was never sent to output 3");
        Assert.Equal(["1*2B", "2*2B", "3*2B", "3*2B"], sent);
        Assert.Equal(MuteState.On, _switcher.DesiredVideoMute);
    }

    [Fact]
    public async Task MatchingReply_SendsNothing()
    {
        _switcher.SetScreenSaverColour("00ff00");
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        Respond("SsavC00FF00\r\n");
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*0B", "2*0B", "3*0B"], sent);
        Assert.Equal(new DeviceDetail("Screen Saver Colour", "00FF00, desired 00FF00"), Detail("Screen Saver Colour"));
    }

    [Fact]
    public async Task ClearedDesiredValue_StopsReconcilingAndSendsNothing()
    {
        _switcher.SetSyncTimeout(30);
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        _switcher.SetSyncTimeout(null);
        _switcher.SetVideoMute(MuteState.Unknown);
        Respond("SsavT501\r\nVmt1*2\r\n");
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*0B", "2*0B", "3*0B"], sent);
        Assert.Null(_switcher.DesiredSyncTimeout);
        Assert.Equal(new DeviceDetail("Sync Timeout", "Never"), Detail("Sync Timeout"));
    }

    [Fact]
    public async Task Heartbeat_QueriesTheTieAndTheHdmiInputsAndEveryOutputConnector()
    {
        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001bO3HDCP\r")),
            "The output connectors were never asked for");
        _mockClient.Invocations.Clear();

        await Poll();

        _mockClient.Verify(x => x.Send("!"), Times.Once);
        _mockClient.Verify(x => x.Send("\u001bI1HDCP\r"), Times.Never);
        _mockClient.Verify(x => x.Send("\u001bI2HDCP\r"), Times.Never);
        foreach (var input in Enumerable.Range(3, 4))
            _mockClient.Verify(x => x.Send($"\u001bI{input}HDCP\r"), Times.Once);
        foreach (var connector in Enumerable.Range(1, 3))
            _mockClient.Verify(x => x.Send($"\u001bO{connector}HDCP\r"), Times.Once);
    }

    [Fact]
    public void Outputs_AreTheThreeConnectorsOfTheOneOutput()
    {
        Assert.Equal(1, _switcher.NumberOfOutputs);
        Assert.Equal(["Output 1A", "Output 1B", "Output 1C"], _switcher.GetOutputs().Select(output => output.Name));
    }

    [Theory]
    [InlineData("In03 All\r\n", "3")]
    [InlineData("In3 All\r\n", "3")]
    [InlineData("In05 RGB\r\n", "5")]
    public void VideoTie_SetsTheSourceOnEveryOutputConnector(string response, string input)
    {
        Respond(response);

        Assert.All(_switcher.GetOutputs(), output => Assert.Equal(input, output.StreamAddress));
    }

    [Fact]
    public void VideoTie_NamesTheDiscoveredInput()
    {
        Respond("In00 0*0*1*1*0*0*0*0\r\n");

        Respond("In03 All\r\n");

        var routed = _switcher.GetOutputs()[0].StreamAddress;
        Assert.Same(_switcher.Inputs[2], _switcher.GetInputs().Find(input => input.StreamAddress == routed));
    }

    [Fact]
    public void AudioTie_LeavesTheVideoSourceAlone()
    {
        Respond("In03 All\r\n");

        Respond("In04 Aud\r\n");

        Assert.All(_switcher.GetOutputs(), output => Assert.Equal("3", output.StreamAddress));
    }

    [Fact]
    public void OutputHdcp_SetsEachConnectorsSinkAndHdcp()
    {
        Respond("HdcpO1*2\r\nHdcpO2*0\r\nHdcpO3*1\r\n");

        var outputs = _switcher.GetOutputs();
        Assert.Equal(
            [ConnectionState.Connected, ConnectionState.Disconnected, ConnectionState.Connected],
            outputs.Select(output => output.OutputConnectionStatus));
        Assert.Equal(
            [HdcpStatus.NotSupported, HdcpStatus.Unknown, HdcpStatus.Available],
            outputs.Select(output => output.OutputHdcpStatus));
    }

    [Fact]
    public void OutputHdcp_ForAConnectorItDoesNotHave_IsIgnored()
    {
        Respond("HdcpO4*1\r\n");

        Assert.All(_switcher.GetOutputs(), output => Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus));
    }

    [Fact]
    public void InputHdcp_SetsTheHdcpAndLeavesTheSignalToSignalPresence()
    {
        Respond("In00 0*0*1*1*0*0*0*0\r\n");

        Respond("HdcpI03*2\r\nHdcpI04*1\r\nHdcpI05*2\r\n");

        Assert.Equal(HdcpStatus.NotSupported, _switcher.Inputs[2].InputHdcpStatus);
        Assert.Equal(HdcpStatus.Active, _switcher.Inputs[3].InputHdcpStatus);
        Assert.Equal(ConnectionState.Disconnected, _switcher.Inputs[4].InputConnectionStatus);
    }

    [Fact]
    public void InputHdcp_BeforeTheInputsAreDiscovered_IsIgnored()
    {
        Respond("HdcpI03*2\r\n");

        Assert.Empty(_switcher.GetInputs());
    }

    [Fact]
    public void SignalPresence_QueriesTheInputNamesOnlyWhenItDiscoversInputs()
    {
        Respond("In00 0*0*1*1*0*1*0*0\r\n");
        Respond("In00 0*0*1*1*0*1*0*0\r\n");

        foreach (var input in Enumerable.Range(1, 8))
            _mockClient.Verify(x => x.Send($"\u001b{input}NI\r"), Times.Once);
    }

    [Fact]
    public void InputNames_RenameTheInputs()
    {
        Respond("In00 0*0*1*1*0*1*0*0\r\n");

        Respond("Nmi01,Input 1\r\nNmi02,Input 2\r\nNmi03,Codec\r\nNmi04,BLURAY\r\nNmi05,DOCCAM\r\nNmi06,SHARELINK\r\nNmi07,Input 7\r\nNmi08,BAR LAPTOP\r\n");

        Assert.Equal(
            ["Input 1", "Input 2", "Codec", "BLURAY", "DOCCAM", "SHARELINK", "Input 7", "BAR LAPTOP"],
            _switcher.GetInputs().Select(input => input.Name));
    }

    [Fact]
    public void InputName_BeforeTheInputsAreDiscovered_IsIgnored()
    {
        Respond("Nmi03,Codec\r\n");

        Assert.Empty(_switcher.GetInputs());
    }

    [Fact]
    public async Task Reconnect_QueriesTheNamesOfTheKnownInputs()
    {
        Respond("In00 0*0*1*1*0*1*0*0\r\n");
        _mockClient.Invocations.Clear();

        SetConnection(ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _mockClient.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001b8NI\r")),
            "The input names were never asked for");

        foreach (var input in Enumerable.Range(1, 8))
            _mockClient.Verify(x => x.Send($"\u001b{input}NI\r"), Times.Once);
    }

    [Fact]
    public void Hotplug_QueriesThatConnectorsHdcp()
    {
        Respond("HplgO2\r\n");

        _mockClient.Verify(x => x.Send("\u001bO2HDCP\r"), Times.Once);
    }

    [Fact]
    public void Reconfig_QueriesTheSignalPresence()
    {
        Respond("Reconfig\r\n");

        _mockClient.Verify(x => x.Send("\u001b0LS\r"), Times.Once);
    }

    [Fact]
    public void Disconnect_ClearsTheSignalHdcpAndSource()
    {
        Respond("In00 0*0*1*1*0*0*0*0\r\nHdcpI03*2\r\nHdcpO1*2\r\nIn03 All\r\n");

        SetConnection(ConnectionState.Disconnected);

        Assert.All(_switcher.Inputs, input =>
        {
            Assert.Equal(ConnectionState.Unknown, input.InputConnectionStatus);
            Assert.Equal(HdcpStatus.Unknown, input.InputHdcpStatus);
        });
        Assert.All(_switcher.Outputs, output =>
        {
            Assert.Equal(ConnectionState.Unknown, output.OutputConnectionStatus);
            Assert.Equal(HdcpStatus.Unknown, output.OutputHdcpStatus);
            Assert.Equal(string.Empty, output.StreamAddress);
        });
    }

    [Fact]
    public async Task SetVideoMute_ReturnsBeforeTheSpacedSends()
    {
        using var release = new ManualResetEventSlim();
        var sent = RecordSends("1*2B", release);

        var mute = Task.Run(() => _switcher.SetVideoMute(MuteState.On));

        await mute.WaitAsync(ReturnTimeout);
        release.Set();
        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 3, "The video mute never reached output 3");
        Assert.Equal(["1*2B", "2*2B", "3*2B"], sent);
    }

    [Fact]
    public async Task SetVideoMute_SendsSuccessiveCallsInTheOrderTheyWereMade()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);

        _switcher.SetVideoMute(MuteState.On);
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Count == 6, "The video mutes never reached output 3");
        Assert.Equal(["1*2B", "2*2B", "3*2B", "1*0B", "2*0B", "3*0B"], sent);
    }

    [Fact]
    public async Task SetVideoMute_SendsUnderTheCallersTriggerId()
    {
        var ids = new ConcurrentQueue<string?>();
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback(() => ids.Enqueue(Trigger.Id));
        var id = Trigger.Start();

        _switcher.SetVideoMute(MuteState.On);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => ids.Count == 3, "The video mute never reached output 3");
        Assert.All(ids, sentUnder => Assert.Equal(id, sentUnder));
    }

    [Fact]
    public async Task SetVideoMute_KeepsSendingAfterAnEarlierSequenceFails()
    {
        using var release = new ManualResetEventSlim(true);
        var sent = RecordSends("", release);
        var changes = 0;
        _switcher.CommunicationStateHandlers += _ =>
        {
            if (Interlocked.Increment(ref changes) <= 2)
                throw new InvalidOperationException("A subscriber failed");
        };

        _switcher.SetVideoMute(MuteState.On);
        _switcher.SetVideoMute(MuteState.Off);

        await Wait.UntilAsync(_time, VideoMuteStepGap, () => sent.Contains("3*0B"), "The second video mute never reached output 3");
        Assert.Equal(["1*2B", "1*0B", "2*0B", "3*0B"], sent);
        Assert.Contains(_switcher.Errors, error => error.Message == "The video mute sends failed");
    }

    [Fact]
    public async Task RejectedIdentityQuery_ShowsItIsNotSupportedWithNoIssueAndIsNotAskedAgain()
    {
        await Connect();
        RespondLines([..ConnectReplies, ..StatusReplies, ..InputNameReplies, ..SettingsReplies, TemperatureReply, ..IdentityReplies[..3]]);
        var communicationState = _switcher.CommunicationState;

        Respond("E10\r\n");

        Assert.Equal(new DeviceDetail("MAC", "Not supported: E10", DetailTone.Normal), Detail("MAC"));
        Assert.Empty(_switcher.GetIssues());
        Assert.Equal(communicationState, _switcher.CommunicationState);
        _time.Advance(QueryReplyTimeout);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send("\u001bCH\r"), Times.Never);
    }

    [Fact]
    public async Task RejectedSettingQuery_ShowsItIsNotSupportedAndIsNotAskedAgainUntilTheSwitcherReconnects()
    {
        await Connect();
        RespondLines([..ConnectReplies, ..StatusReplies, ..InputNameReplies, ..SettingsReplies[..4]]);

        Respond("E10\r\n");

        Assert.Equal(new DeviceDetail("OSD Duration", "Not supported: E10"), Detail("OSD Duration"));
        Assert.Empty(_switcher.GetIssues());
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send("\u001bMDUR\r"), Times.Never);
        _mockClient.Verify(client => client.Send("\u001bAUSW\r"), Times.Once);

        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send("\u001bMDUR\r"), Times.Once);
    }

    [Fact]
    public void RejectionWithNoOutstandingQuery_RaisesNoIssue()
    {
        Respond("E13\r\n");

        Assert.Empty(_switcher.GetIssues());
        Assert.Empty(_switcher.Details);
    }

    [Fact]
    public void RejectedRoute_RaisesAMomentaryIssue()
    {
        _switcher.RouteAV(4, 1);

        Respond("E01\r\n");

        var issue = Assert.Single(_switcher.GetIssues());
        Assert.Equal(("rejected-command", IssueStatus.Momentary, "The switcher rejected 4! with E01"), (issue.Key, issue.Status, issue.Message));
    }

    [Fact]
    public async Task UnansweredIdentityQuery_IsSentAgainOnlyOnceItsReplyIsOverdue()
    {
        await Connect();
        Respond("Bld1.20.0003\r\nPno60-1081-01\r\nIph 00-05-A6-07-8C-EC\r\n");
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send("\u001bCN\r"), Times.Never);

        _time.Advance(QueryReplyTimeout);
        await Poll();

        _mockClient.Verify(client => client.Send("\u001bCN\r"), Times.Once);
        foreach (var answered in new[] { "*Q", "N", "\u001bCH\r" })
            _mockClient.Verify(client => client.Send(answered), Times.Never);
    }

    [Fact]
    public async Task RejectedTemperatureQuery_IsNotSentAgainUntilTheSwitcherReconnects()
    {
        await Connect();
        RespondLines([..ConnectReplies, ..StatusReplies, ..InputNameReplies, ..SettingsReplies]);

        Respond("E10\r\n");

        Assert.Equal(new DeviceDetail("Temperature", "Not supported: E10", DetailTone.Normal), Detail("Temperature"));
        Assert.Empty(_switcher.GetIssues());
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Never);

        SetConnection(ConnectionState.Disconnected);
        SetConnection(ConnectionState.Connected);
        _mockClient.Invocations.Clear();
        await Poll();
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Once);
    }

    [Fact]
    public async Task BusyTemperatureQuery_IsSentAgainAndRaisesTheReadIssueOnTheThirdInARow()
    {
        await Connect();
        RespondLines([..ConnectReplies, ..StatusReplies, ..InputNameReplies, ..SettingsReplies, TemperatureReply, ..IdentityReplies]);
        _mockClient.Invocations.Clear();
        await Poll();
        AnswerThePollUpToTheTemperature();
        Respond("E22\r\n");
        await Poll();
        AnswerThePollUpToTheTemperature();
        Respond("E22\r\n");

        Assert.Empty(_switcher.GetIssues());
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Exactly(2));

        await Poll();
        AnswerThePollUpToTheTemperature();
        Respond("E22\r\n");

        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("temperature-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The switcher did not answer Esc 20STAT (Temperature) 3 times in a row, the last was E22", issue.Message);
        Respond($"{TemperatureReply}\r\n");
        Assert.Empty(_switcher.GetOngoingIssues());
    }

    [Fact]
    public async Task UnansweredTemperatureQuery_IsSentAgainAndRaisesTheReadIssueOnTheThirdMiss()
    {
        await Connect();
        RespondLines([..ConnectReplies, ..StatusReplies, ..InputNameReplies, ..SettingsReplies, ..IdentityReplies]);
        _time.Advance(QueryReplyTimeout);
        await Poll();
        AnswerThePollUpToTheTemperature();
        _time.Advance(QueryReplyTimeout);
        await Poll();
        AnswerThePollUpToTheTemperature();

        Assert.Empty(_switcher.GetIssues());

        _time.Advance(QueryReplyTimeout);
        await Poll();

        var issue = Assert.Single(_switcher.GetOngoingIssues());
        Assert.Equal("temperature-read", issue.Key);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("The switcher did not answer Esc 20STAT (Temperature) 3 times in a row, the last was no 20Stat reply within 5 s", issue.Message);
        _mockClient.Verify(client => client.Send(TemperatureQuery), Times.Exactly(4));
        Respond($"{TemperatureReply}\r\n");
        Assert.Empty(_switcher.GetOngoingIssues());
        Assert.Equal("36", Detail("Temperature")?.Value);
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronIn16xxReplyTest
{
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<CommunicationClient> _client = TestFactory.CreateCommunicationClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronIn16Xx _switcher;

    public ExtronIn16xxReplyTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronIn16Xx(_client.Object, 6, "Replying matrix", _time);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Respond(IEnumerable<string> replies)
    {
        foreach (var reply in replies)
            _client.Object.ResponseHandlers!.Invoke($"{reply}\r\n");
    }

    private Task Poll() => (Task)typeof(ExtronIn16Xx)
        .GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(_switcher, [CancellationToken.None])!;

    [Fact]
    public async Task ConnectPollAndRoutes_MatchEveryReplyToTheCommandItAnswers()
    {
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(_client.Object, ConnectionState.Connected);
        await Wait.UntilAsync(_time, ConnectStepGap,
            () => _client.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001bCH\r")), "The identity was never asked for");
        Respond([
            ..ExtronIn16xxTest.ConnectReplies, ..ExtronIn16xxTest.StatusReplies, ..ExtronIn16xxTest.InputNameReplies,
            ..ExtronIn16xxTest.SettingsReplies, ExtronIn16xxTest.TemperatureReply, ..ExtronIn16xxTest.IdentityReplies
        ]);
        _switcher.RouteAV(3, 1);
        _switcher.RouteVideo(4, 1);
        _switcher.RouteAudio(5, 1);
        Respond(["In03 All", "In04 RGB", "In05 Aud"]);
        await Poll();
        Respond(["Vrb3", ..ExtronIn16xxTest.StatusReplies, ..ExtronIn16xxTest.SettingsReplies, ExtronIn16xxTest.TemperatureReply]);
        _time.Advance(ReplyTimeout);

        await Poll();

        Assert.Equal(["1.20.0003", "60-1081-01", "IN1608-07-8C-EC", "00-05-A6-07-8C-EC", "36"],
            new[] { "Firmware", "Part Number", "Unit Name", "MAC", "Temperature" }
                .Select(label => _switcher.Details.SingleOrDefault(detail => detail.Label == label)?.Value));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.StartsWith("The switcher didn't answer"));
        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("asking again"));
        Assert.DoesNotContain(_logs.Logs, log => log.Level >= LogLevel.Warning);
        Assert.Empty(_switcher.GetIssues());
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronIn16xxLogTest
{
    private readonly Mock<CommunicationClient> _client = TestFactory.CreateCommunicationClient();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ExtronIn16Xx _switcher;

    public ExtronIn16xxLogTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _switcher = new ExtronIn16Xx(_client.Object, 8, "Logged matrix");
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    private void Respond(string response) => _client.Object.ResponseHandlers!.Invoke(response);

    [Fact]
    public void UnrecognisedSettingReply_IsLoggedRawOnceInTheDriversScope()
    {
        Respond("SsavM9\r\n");
        Respond("SsavM9\r\n");

        var log = Assert.Single(_logs.Logs, log => log.Message.Contains("SsavM9"));
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal(nameof(ExtronIn16Xx), log.Scope["Class"]);
    }

    [Fact]
    public async Task MismatchedReply_IsLoggedWithTheSettingTheReplyAndTheDesiredValue()
    {
        _switcher.SetAutoSwitch(ExtronIn16Xx.AutoSwitchMode.LowestActiveInput);
        _client.Invocations.Clear();

        Respond("Ausw1\r\n");

        var log = Assert.Single(_logs.Logs, log => log.Message.StartsWith("Reconciling"));
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Contains("Auto Switch", log.Message);
        Assert.Contains("Ausw1", log.Message);
        Assert.Contains("LowestActiveInput", log.Message);
        Assert.Equal(nameof(ExtronIn16Xx), log.Scope["Class"]);
        await Wait.UntilAsync(() => _client.Invocations.Any(invocation => Equals(invocation.Arguments[0], "\u001b2AUSW\r")),
            "The desired auto switch mode was never sent");
    }

    [Fact]
    public void MatchingReply_IsNotLogged()
    {
        _switcher.SetAutoSwitch(ExtronIn16Xx.AutoSwitchMode.Off);

        Respond("Ausw0\r\n");

        Assert.DoesNotContain(_logs.Logs, log => log.Message.Contains("Auto Switch"));
    }
}
