using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Display.Tests;

public class PjLinkTest
{
    private readonly PjLink _display;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();

    public PjLinkTest()
    {
        _display = new PjLink(_mockClient.Object, "Test display", null);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        string expectedPowerCommand = "%1POWR 0\r";
        _display.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        string expectedPowerCommand = "%1POWR 1\r";
        _display.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerCommand), Times.Once);
    }

    [Theory]
    [InlineData(Input.Hdmi1, "%1INPT 31\r")]
    [InlineData(Input.Hdmi2, "%1INPT 32\r")]
    public void SetInput_SendsTheExpectedCommand(Input source, string command)
    {
        _display.SetInput(source);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Fact]
    public void SetVolume_SendsTheExpectedCommand()
    {
        _display.SetVolume(1);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(MuteState.On, "%1AVMT 21\r")]
    [InlineData(MuteState.Off, "%1AVMT 30\r")]
    public void SetAudioMute_SendsTheExpectedCommand(MuteState state, string command)
    {
        _display.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, "%1AVMT 11\r")]
    [InlineData(MuteState.Off, "%1AVMT 30\r")]
    public void SetPictureMute_SendsTheExpectedCommand(MuteState state, string command)
    {
        _display.SetPictureMute(state);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Theory]
    [InlineData(MuteState.On, MuteState.On, "%1AVMT 31\r")]
    [InlineData(MuteState.Off, MuteState.Off, "%1AVMT 30\r")]
    [InlineData(MuteState.On, MuteState.Off, "%1AVMT 21\r")]
    [InlineData(MuteState.Off, MuteState.On, "%1AVMT 11\r")]
    public void SetPictureAndAudioMutes_SendTheExpectedCommand(MuteState audioState, MuteState videoState,
        string command)
    {
        _display.SetPictureMute(videoState);
        _display.SetAudioMute(audioState);

        Assert.Equal(command, _mockClient.Invocations.Last().Arguments[0]);
    }

    [Theory]
    [InlineData("%1POWR=1", PowerState.On)]
    [InlineData("%1POWR=0", PowerState.Off)]
    public void HandleResponse_UpdatesPowerState(string input, PowerState expectedPowerState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(input + "\r");

        Assert.Equal(_display.PowerState, expectedPowerState);
    }

    [Theory]
    [InlineData("%1INPT=31", Input.Hdmi1)]
    [InlineData("%1INPT=32", Input.Hdmi2)]
    public void HandleResponse_UpdatesInput(string input, Input expectedInput)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(input + "\r");

        Assert.Equal(_display.Input, expectedInput);
    }

    [Fact]
    public void HandleResponse_ForcesPowerState()
    {
        _display.PowerOff();
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=1\r");

        _mockClient.Verify(x => x.Send("%1POWR 0\r"), Times.Exactly(2));
    }

    [Theory]
    [InlineData(PowerState.On, "%1POWR=3", "%1POWR 1\r")]
    [InlineData(PowerState.Off, "%1POWR=2", "%1POWR 0\r")]
    public void HandleResponse_DoesntForcePowerWhileTransitioningTowardsIt(PowerState desired, string response, string command)
    {
        if (desired == PowerState.On) _display.PowerOn(); else _display.PowerOff();
        _mockClient.Object.ResponseHandlers?.Invoke(response + "\r");

        _mockClient.Verify(x => x.Send(command), Times.Exactly(1));
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "power-state");
    }

    [Theory]
    [InlineData(PowerState.On, "%1POWR=2", "%1POWR 1\r")]
    [InlineData(PowerState.Off, "%1POWR=3", "%1POWR 0\r")]
    public void HandleResponse_DoesntForcePowerDuringAnyTransition(PowerState desired, string response, string command)
    {
        if (desired == PowerState.On) _display.PowerOn(); else _display.PowerOff();
        _mockClient.Object.ResponseHandlers?.Invoke(response + "\r");

        _mockClient.Verify(x => x.Send(command), Times.Exactly(1));
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "power-state");
    }

    [Fact]
    public void HandleResponse_ForcesPowerOnceTheTransitionEndsWrong()
    {
        _display.PowerOff();
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=3\r%1POWR=1\r");

        _mockClient.Verify(x => x.Send("%1POWR 0\r"), Times.Exactly(2));
    }

    [Theory]
    [InlineData("%1POWR=3", "%1POWR 0\r")]
    [InlineData("%1POWR=2", "%1POWR 1\r")]
    public void TogglePower_TreatsWarmingAsOnAndCoolingAsOff(string response, string command)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response + "\r");
        _display.TogglePower();

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Fact]
    public void HandleResponse_DoesntForcePowerStateWhenCorrect()
    {
        _display.PowerOn();
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=1\r");

        _mockClient.Verify(x => x.Send("%1POWR 1\r"), Times.Exactly(1));
    }

    [Fact]
    public void HandleResponse_ForcesInput()
    {
        _display.SetInput(Input.Hdmi1);
        _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=32\r");

        _mockClient.Verify(x => x.Send("%1INPT 31\r"), Times.Exactly(2));
    }

    [Fact]
    public void HandleResponse_DoesntForceInputWhenCorrect()
    {
        _display.SetInput(Input.Hdmi1);
        _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=31\r");

        _mockClient.Verify(x => x.Send("%1INPT 31\r"), Times.Exactly(1));
    }

    [Fact]
    public void HandleResponse_ForcesPictureMute()
    {
        _display.SetPictureMute(MuteState.On);
        _mockClient.Object.ResponseHandlers?.Invoke("%1AVMT=30\r");

        _mockClient.Verify(x => x.Send("%1AVMT 11\r"), Times.Exactly(2));
    }

    [Fact]
    public void HandleResponse_LogsInAndPollsPower()
    {
        // AV Coders
        _mockClient.Object.ResponseHandlers!.Invoke("PJLINK 1 3bcc52b3\r");
        byte[] expected = [0x36, 0x35, 0x36, 0x30, 0x34, 0x65, 0x38, 0x63, 0x61, 0x34, 0x32, 0x65, 0x36, 0x34, 0x65, 0x36, 0x64, 0x31, 0x63, 0x39, 0x39, 0x38, 0x66, 0x39, 0x65, 0x39, 0x35, 0x33, 0x35, 0x64, 0x38, 0x38, 0x25, 0x31, 0x50, 0x4f, 0x57, 0x52, 0x20, 0x3f, 0x0d
        ];

        _mockClient.Verify(x => x.Send(expected), Times.Once);
    }

    [Fact]
    public void GetMd5Hash_ReturnsTheHash()
    {
        var actual = _display.GetMd5Hash("3bcc52b3JBMIAProjectorLink");
        var expected = new byte[] { 0x36, 0x35, 0x36, 0x30, 0x34, 0x65, 0x38, 0x63, 0x61, 0x34, 0x32, 0x65, 0x36, 0x34, 0x65, 0x36, 0x64, 0x31, 0x63, 0x39, 0x39, 0x38, 0x66, 0x39, 0x65, 0x39, 0x35, 0x33, 0x35, 0x64, 0x38, 0x38 };
        Assert.Equal(expected, actual);

    }

    [Fact]
    public void GetMd5Hash_PadsBytesBelow0x10()
    {
        // MD5("1d651e5bJBMIAProjectorLink") = 83a3e3f02cea00e5215c5b0ea8495dfd, which contains a
        // 0x02 and a 0x00 byte. The previous single-digit formatting rendered those as "2"/"0"
        // instead of "02"/"00", producing a 30-char hash that the projector rejected with ERRA.
        var actual = _display.GetMd5Hash("1d651e5bJBMIAProjectorLink");
        var expected = System.Text.Encoding.ASCII.GetBytes("83a3e3f02cea00e5215c5b0ea8495dfd");

        Assert.Equal(32, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void HandleResponse_ReassemblesASplitFrame()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR");
        Assert.Equal(PowerState.Unknown, _display.PowerState);

        _mockClient.Object.ResponseHandlers?.Invoke("=1\r");
        Assert.Equal(PowerState.On, _display.PowerState);
    }

    [Fact]
    public void HandleResponse_ProcessesCoalescedFrames()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=1\r%1INPT=31\r");

        Assert.Equal(PowerState.On, _display.PowerState);
        Assert.Equal(Input.Hdmi1, _display.Input);
    }

    [Fact]
    public void HandleResponse_IgnoresUnparseableParameter()
    {
        _display.PowerOn();
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=garbage\r");

        Assert.Equal(PowerState.On, _display.PowerState);
    }

    [Fact]
    public void HandleResponse_DoesNotAuthenticateWhenNoPasswordIsRequired()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("PJLINK 0\r");

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
        _mockClient.Verify(x => x.Send("%1POWR ?\r"), Times.Once);
    }

    [Fact]
    public void HandleResponse_QueriesDeviceInformationAfterLogin()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("PJLINK 1 3bcc52b3\r");

        foreach (var command in new[] { "CLSS", "INF1", "INF2", "INFO", "NAME", "INST" })
            _mockClient.Verify(x => x.Send($"%1{command} ?\r"), Times.Once);
        _mockClient.Verify(x => x.Send("%2SNUM ?\r"), Times.Never);
    }

    [Fact]
    public void HandleResponse_BannerErraRaisesThePasswordIssueOnly()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("PJLINK ERRA\r");

        Assert.Single(_display.GetOngoingIssues(), i => i.Key == PjLink.PasswordIssueKey);
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "communication");
        Assert.Equal(CommunicationState.NotAttempted, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_ParsesDeviceInformation()
    {
        _mockClient.Object.ResponseHandlers?.Invoke(
            "%1INF1=EPSON\r%1INF2=EPSON 685Wi/685WT\r%1INFO=107.107.---\r%1NAME=EBF4AB78\r" +
            "%2SNUM=X3QR9700019\r%2SVER=8Y0080709MWWV107\r%2RLMP=ELPLP91\r%2RFIL=ELPAF49\r%2RRES=1280x800\r");

        Assert.Equal("EPSON", _display.ManufacturerName);
        Assert.Equal("EPSON 685Wi/685WT", _display.ProductName);
        Assert.Equal("107.107.---", _display.OtherInformation);
        Assert.Equal("EBF4AB78", _display.ProjectorName);
        Assert.Equal("X3QR9700019", _display.SerialNumber);
        Assert.Equal("8Y0080709MWWV107", _display.SoftwareVersion);
        Assert.Equal("ELPLP91", _display.LampReplacementModelNumber);
        Assert.Equal("ELPAF49", _display.FilterReplacementModelNumber);
        Assert.Equal("1280x800", _display.RecommendedResolution);
    }

    [Fact]
    public void HandleResponse_Class2RequestsClass2Information()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=2\r");

        Assert.Equal(2, _display.PjLinkClass);
        foreach (var command in new[] { "SNUM", "SVER", "RLMP", "RFIL", "RRES", "FILT" })
            _mockClient.Verify(x => x.Send($"%2{command} ?\r"), Times.Once);
    }

    [Fact]
    public void HandleResponse_Class1DoesNotRequestClass2Information()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=11 31\r");

        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.StartsWith("%2"))), Times.Never);
    }

    [Fact]
    public void HandleResponse_ResolvesInputNamesOneAtATime()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=2\r%1INST=11 32 33\r");
        _mockClient.Verify(x => x.Send("%2INNM ?11\r"), Times.Once);
        _mockClient.Verify(x => x.Send("%2INNM ?32\r"), Times.Never);

        _mockClient.Object.ResponseHandlers?.Invoke("%2INNM=Computer1\r");
        _mockClient.Verify(x => x.Send("%2INNM ?32\r"), Times.Once);

        _mockClient.Object.ResponseHandlers?.Invoke("%2INNM=ERR2\r");
        _mockClient.Verify(x => x.Send("%2INNM ?33\r"), Times.Once);

        _mockClient.Object.ResponseHandlers?.Invoke("%2INNM=HDMI2\r");

        Assert.Equal([11, 32, 33], _display.AvailableInputCodes);
        Assert.Equal("Computer1", _display.InputNames[11]);
        Assert.False(_display.InputNames.ContainsKey(32));
        Assert.Equal("HDMI2", _display.InputNames[33]);
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_ParsesLamps()
    {
        IReadOnlyList<PjLinkLamp>? raised = null;
        _display.OnLampsChanged += lamps => raised = lamps;

        _mockClient.Object.ResponseHandlers?.Invoke("%1LAMP=974 1 120 0\r");

        Assert.Equal([new PjLinkLamp(974, true), new PjLinkLamp(120, false)], _display.Lamps);
        Assert.NotNull(raised);
    }

    [Fact]
    public void HandleResponse_ParsesErrorStatusAndRaisesAnIssue()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1ERST=000000\r");
        Assert.NotNull(_display.ErrorStatus);
        Assert.False(_display.ErrorStatus!.HasError);
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == PjLink.HardwareIssueKey);

        _mockClient.Object.ResponseHandlers?.Invoke("%1ERST=012000\r");
        Assert.Equal(PjLinkErrorState.Warning, _display.ErrorStatus!.Lamp);
        Assert.Equal(PjLinkErrorState.Error, _display.ErrorStatus.Temperature);
        var issue = Assert.Single(_display.GetOngoingIssues(), i => i.Key == PjLink.HardwareIssueKey);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Contains("Temperature error", issue.Message);

        _mockClient.Object.ResponseHandlers?.Invoke("%1ERST=000000\r");
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == PjLink.HardwareIssueKey);
    }

    [Fact]
    public void HandleResponse_ParsesFilterResolutionAndFreeze()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%2FILT=42\r%2IRES=1920x1080\r%2FREZ=1\r");

        Assert.Equal(42, _display.FilterUsageHours);
        Assert.Equal("1920x1080", _display.InputResolution);
        Assert.True(_display.FreezeActive);
    }

    [Theory]
    [InlineData("%1XXXX=ERR1")]
    [InlineData("%1INPT=ERR2")]
    [InlineData("%2FREZ=ERR3")]
    public void HandleResponse_RejectedCommandsAreNotCommunicationErrors(string response)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response + "\r");

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_Err4RaisesAMomentaryIssueNotACommunicationError()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1LAMP=ERR4\r");

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
        var issue = Assert.Single(_display.GetIssues(), i => i.Key == "projector-failure-LAMP");
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public void HandleResponse_RepeatedErr4EscalatesAndASuccessResolves()
    {
        for (int i = 0; i < 3; i++)
            _mockClient.Object.ResponseHandlers?.Invoke("%1LAMP=ERR4\r");

        var ongoing = Assert.Single(_display.GetOngoingIssues());
        Assert.Equal("projector-failure-LAMP", ongoing.Key);
        Assert.Equal(IssueSeverity.Critical, ongoing.Severity);

        _mockClient.Object.ResponseHandlers?.Invoke("%1LAMP=974 1\r");

        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public void HandleResponse_Err2RaisesAMomentaryIssue()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=ERR2\r");

        var issue = Assert.Single(_display.GetIssues(), i => i.Key == "err2-INPT");
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void HandleResponse_Err1StopsThatCommandBeingSent()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%2FREZ=ERR1\r");
        _display.Freeze();

        Assert.Contains("FREZ", _display.UnsupportedCommands);
        _mockClient.Verify(x => x.Send("%2FREZ 1\r"), Times.Never);
        Assert.Empty(_display.GetIssues());
    }

    [Fact]
    public void HandleResponse_PopulatesDetails()
    {
        _mockClient.Object.ResponseHandlers?.Invoke(
            "%1CLSS=2\r%1INF1=EPSON\r%1INF2=EPSON 685Wi/685WT\r%2SNUM=X3QR9700019\r%1LAMP=974 1\r" +
            "%2FILT=12\r%2IRES=-\r%2FREZ=0\r%1ERST=000000\r");

        var details = _display.Details.ToDictionary(d => d.Label, d => d);
        Assert.Equal("2", details["PJLink Class"].Value);
        Assert.Equal("EPSON", details["Manufacturer"].Value);
        Assert.Equal("EPSON 685Wi/685WT", details["Model"].Value);
        Assert.Equal("X3QR9700019", details["Serial Number"].Value);
        Assert.Equal("974 h, on", details["Lamp"].Value);
        Assert.Equal("12 h", details["Filter Usage"].Value);
        Assert.Equal("No signal", details["Input Resolution"].Value);
        Assert.Equal("No", details["Frozen"].Value);
        Assert.Equal(new DeviceDetail("Errors", "None"), details["Errors"]);

        _mockClient.Object.ResponseHandlers?.Invoke("%1ERST=012000\r%2IRES=ERR3\r%1LAMP=974 0 20 1\r");

        details = _display.Details.ToDictionary(d => d.Label, d => d);
        Assert.Equal(new DeviceDetail("Errors", "Lamp warning, Temperature error", DetailTone.Error), details["Errors"]);
        Assert.False(details.ContainsKey("Input Resolution"));
        Assert.Equal("974 h, off", details["Lamp 1"].Value);
        Assert.Equal("20 h, on", details["Lamp 2"].Value);
        Assert.False(details.ContainsKey("Lamp"));
    }

    private class TestTcpClient() : TcpClient("host", 4352, "Test", CommandStringFormat.Ascii)
    {
        public int Reconnects;
        public readonly List<string> Sent = [];
        public override void Send(string message) { lock (Sent) Sent.Add(message); }
        public override void Send(byte[] bytes) { lock (Sent) Sent.Add(System.Text.Encoding.ASCII.GetString(bytes)); }
        public override void Connect() { }
        public override void Reconnect() => Reconnects++;
        public override void Disconnect() { }
        protected override Task ProcessSendQueue(CancellationToken token) => Task.CompletedTask;
        protected override Task CheckConnectionState(CancellationToken token) => Task.CompletedTask;
        public void SetConnectionState(ConnectionState state) => ConnectionState = state;
        protected override Task Receive(CancellationToken token) => Task.CompletedTask;
        public void Feed(string response) => InvokeResponseHandlers(response);
    }

    private class TestablePjLink(TcpClient client, Input? defaultInput = null, Dictionary<Input, int>? inputMap = null)
        : PjLink(client, "Test", defaultInput, DefaultPassword, inputMap)
    {
        public Task Poll() => DoPoll(CancellationToken.None);

        public TestablePjLink NoDiscoveryThrottle()
        {
            DiscoveryRetryInterval = TimeSpan.Zero;
            return this;
        }

        // Keep the real PollWorker out of these tests so Poll() is the only caller of DoPoll.
        protected override void RestartPolling() { }
    }

    [Fact]
    public void Constructor_ReconnectsWhenTheSocketIsAlreadyOpen()
    {
        var client = new TestTcpClient();
        client.SetConnectionState(ConnectionState.Connected);

        _ = new PjLink(client, "Test", null);

        Assert.Equal(1, client.Reconnects);
    }

    [Fact]
    public void Constructor_DoesNotReconnectWhenNotYetConnected()
    {
        var client = new TestTcpClient();

        _ = new PjLink(client, "Test", null);

        Assert.Equal(0, client.Reconnects);
    }

    [Fact]
    public async Task DoPoll_ReconnectsWhenNoBannerArrives()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);

        await display.Poll();
        Assert.Equal(0, client.Reconnects);
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%1POWR"));

        await display.Poll();
        Assert.Equal(1, client.Reconnects);
    }

    [Fact]
    public async Task DoPoll_PollsOnceTheBannerHasBeenHandled()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r");
        lock (client.Sent) client.Sent.Clear();

        await display.Poll();

        Assert.Equal(0, client.Reconnects);
        Assert.Contains("%1POWR ?\r", client.Sent);
        Assert.Contains("%1INPT ?\r", client.Sent);
        Assert.Contains("%1AVMT ?\r", client.Sent);
        Assert.Contains("%1ERST ?\r", client.Sent);
    }

    private const string EpsonInst = "%1INST=11 12 21 32 33 36 41 52 53\r";
    private static readonly string[] EpsonNames =
        ["Computer1", "Computer2", "Video", "HDMI1", "HDMI2", "HDMI3", "USB", "LAN", "USB Display"];

    private void FeedEpsonInputs(PjLink display, Mock<TcpClient> client)
    {
        client.Object.ResponseHandlers?.Invoke("%1CLSS=2\r" + EpsonInst);
        foreach (var name in EpsonNames)
            client.Object.ResponseHandlers?.Invoke($"%2INNM={name}\r");
    }

    [Fact]
    public void InputMap_IsRebuiltFromTheProjectorsInputList()
    {
        FeedEpsonInputs(_display, _mockClient);

        Assert.Equal([Input.Vga1, Input.Vga2, Input.Composite, Input.Hdmi1, Input.Hdmi2, Input.Hdmi3, Input.Usb1, Input.Network1, Input.UsbDisplay],
            _display.SupportedInputs);
        Assert.Empty(_display.UnmappedInputCodes);
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey);

        _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=33\r");
        Assert.Equal(Input.Hdmi2, _display.Input);

        _display.SetInput(Input.Hdmi1);
        _mockClient.Verify(x => x.Send("%1INPT 32\r"), Times.Once);
    }

    [Fact]
    public void InputMap_UnmappableNameRaisesAnIssueUntilItDisappears()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=2\r%1INST=32 71\r%2INNM=HDMI1\r%2INNM=Whiteboard\r");

        var issue = Assert.Single(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey);
        Assert.Contains("71 (Whiteboard)", issue.Message);
        Assert.Equal([71], _display.UnmappedInputCodes);
        Assert.Equal([Input.Hdmi1], _display.SupportedInputs);
        Assert.Contains(_display.Details, d => d.Label == "Unmapped Inputs" && d.Tone == DetailTone.Warning);

        _mockClient.Object.ResponseHandlers?.Invoke("%1INST=32\r%2INNM=HDMI1\r");

        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey);
        Assert.DoesNotContain(_display.Details, d => d.Label == "Unmapped Inputs");
    }

    [Fact]
    public void InputMap_ExplicitConstructorMapWinsOverNames()
    {
        var client = TestFactory.CreateTcpClient();
        var display = new PjLink(client.Object, "Test", null, PjLink.DefaultPassword,
            new Dictionary<Input, int> { { Input.Hdmi4, 32 } });

        FeedEpsonInputs(display, client);

        Assert.Contains(Input.Hdmi4, display.SupportedInputs);
        Assert.DoesNotContain(Input.Hdmi1, display.SupportedInputs);
        display.SetInput(Input.Hdmi4);
        client.Verify(x => x.Send("%1INPT 32\r"), Times.Once);
    }

    [Fact]
    public void InputMap_Class1FallsBackToCodeClasses()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=11 31 32\r");

        Assert.Equal([Input.Vga1, Input.Hdmi1, Input.Hdmi2], _display.SupportedInputs);
        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.StartsWith("%2INNM"))), Times.Never);
    }

    [Fact]
    public void InputMap_CollidingNamesFallBackToTheCodeClass()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=2\r%1INST=31 32\r%2INNM=HDMI1\r%2INNM=HDMI1\r");

        Assert.Equal([Input.Hdmi1, Input.Hdmi2], _display.SupportedInputs);
        Assert.Empty(_display.UnmappedInputCodes);
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey);
    }

    [Fact]
    public void InputMap_NetworkNamesThatCollideUseTheirCodes()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=2\r%1INST=52 54\r%2INNM=LAN\r%2INNM=Wireless\r");

        Assert.Equal([Input.Network1, Input.Network4], _display.SupportedInputs);
        Assert.Empty(_display.UnmappedInputCodes);
    }

    [Fact]
    public void InputMap_ScreenMirroringHasItsOwnInput()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=2\r%1INST=52 54\r%2INNM=LAN\r%2INNM=Screen Mirroring\r");

        Assert.Equal([Input.Network1, Input.ScreenMirroring], _display.SupportedInputs);
    }

    [Fact]
    public void InputNames_InnmErr1MidPassFallsBackToCodeClasses()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r");
        lock (client.Sent) client.Sent.Clear();

        client.Feed("%2INNM=ERR1\r");

        Assert.Equal([Input.Vga1, Input.Hdmi2], display.SupportedInputs);
        Assert.Empty(client.Sent);
        Assert.Contains("INNM", display.UnsupportedCommands);
    }

    [Fact]
    public async Task InputNames_InnmErr3MidPassRetriesThePassOnTheNextTick()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r%2INNM=Computer1\r%2INNM=ERR3\r");
        Assert.Equal(PjLink.DefaultInputMap.Keys, display.SupportedInputs);
        lock (client.Sent) client.Sent.Clear();

        await display.Poll();
        Assert.Equal(["%2INNM ?11\r"], client.Sent.Where(s => s.StartsWith("%2INNM")));

        client.Feed("%2INNM=Computer1\r%2INNM=HDMI1\r");
        Assert.Equal([Input.Vga1, Input.Hdmi1], display.SupportedInputs);
        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%2INNM"));
    }

    [Fact]
    public async Task DoPoll_RetriesClass2InformationUntilAnswered()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client).NoDiscoveryThrottle();
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11\r%2SNUM=ERR3\r%2SVER=ERR3\r%2RLMP=ERR3\r%2RFIL=ERR3\r%2RRES=ERR3\r");
        lock (client.Sent) client.Sent.Clear();

        await display.Poll();
        foreach (var command in new[] { "SNUM", "SVER", "RLMP", "RFIL", "RRES" })
            Assert.Contains($"%2{command} ?\r", client.Sent);

        client.Feed("%2SNUM=S\r%2SVER=V\r%2RLMP=L\r%2RFIL=F\r%2RRES=R\r");
        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%2SNUM") || s.StartsWith("%2RRES"));
    }

    [Fact]
    public void InputMap_ExplicitMapWinsRegardlessOfInstOrder()
    {
        var client = TestFactory.CreateTcpClient();
        var display = new PjLink(client.Object, "Test", null, PjLink.DefaultPassword,
            new Dictionary<Input, int> { { Input.Hdmi1, 36 } });

        FeedEpsonInputs(display, client);

        Assert.Empty(display.UnmappedInputCodes);
        display.SetInput(Input.Hdmi1);
        client.Verify(x => x.Send("%1INPT 36\r"), Times.Once);
        client.Verify(x => x.Send("%1INPT 32\r"), Times.Never);
    }

    [Fact]
    public void InputMap_DroppedDefaultInputIsReportedOnceAndNotForced()
    {
        var client = TestFactory.CreateTcpClient();
        var display = new PjLink(client.Object, "Test", Input.Hdmi4);

        client.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=11 32\r");
        display.PowerOn();
        for (int i = 0; i < 3; i++)
            client.Object.ResponseHandlers?.Invoke("%1INPT=32\r");

        var issue = Assert.Single(display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey);
        Assert.Contains("Hdmi4", issue.Message);
        Assert.DoesNotContain(display.Events, e => e.Info.Contains("not available"));
        var inputIssue = Assert.Single(display.GetOngoingIssues(), i => i.Key == "input");
        Assert.Equal(IssueSeverity.Minor, inputIssue.Severity);
        Assert.Contains("not supported", inputIssue.Message);
        client.Verify(x => x.Send(It.Is<string>(s => s.StartsWith("%1INPT "))), Times.Never);
    }

    [Fact]
    public void InputMap_DroppedDesiredInputIsResetToUnknown()
    {
        _display.SetInput(Input.Hdmi4);
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=11 32\r");

        Assert.Equal(Input.Unknown, _display.DesiredInput);
        Assert.Contains("Hdmi4", Assert.Single(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey).Message);
    }

    [Fact]
    public void InputMap_RaisesOnSupportedInputsChanged()
    {
        IReadOnlyList<Input>? raised = null;
        _display.OnSupportedInputsChanged += x => raised = x;

        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=11 32\r");

        Assert.Equal([Input.Vga1, Input.Hdmi2], raised);
        Assert.Same(raised, _display.SupportedInputs);
    }

    [Fact]
    public void HandleResponse_Err2IssueResolvesOnTheNextGoodAnswer()
    {
        for (int i = 0; i < 3; i++)
            _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=ERR2\r");
        Assert.Single(_display.GetOngoingIssues(), i => i.Key == "err2-INPT");

        _mockClient.Object.ResponseHandlers?.Invoke("%1INPT=32\r");

        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "err2-INPT");
    }

    [Fact]
    public void HandleResponse_ParseIssuesResolveOnTheNextGoodAnswer()
    {
        for (int i = 0; i < 5; i++)
            _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=garbage\r");
        for (int i = 0; i < 5; i++)
            _mockClient.Object.ResponseHandlers?.Invoke("nonsense\r");
        Assert.Single(_display.GetOngoingIssues(), i => i.Key == "parse-POWR");
        Assert.Single(_display.GetOngoingIssues(), i => i.Key == "parse-frame");

        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=1\r");

        Assert.Empty(_display.GetOngoingIssues());
    }

    [Fact]
    public void HandleResponse_StaleLampRowsAreRemoved()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1LAMP=974 1\r");
        Assert.Contains(_display.Details, d => d.Label == "Lamp");

        _mockClient.Object.ResponseHandlers?.Invoke("%1LAMP=974 0 20 1\r");

        Assert.DoesNotContain(_display.Details, d => d.Label == "Lamp");
        Assert.Contains(_display.Details, d => d.Label == "Lamp 1");
        Assert.Contains(_display.Details, d => d.Label == "Lamp 2");
    }

    [Fact]
    public void HandleResponse_FreezeClearsWhenThePowerLeavesOnOrFrezIsRejected()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%2FREZ=1\r");
        Assert.True(_display.FreezeActive);

        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=0\r");
        Assert.False(_display.FreezeActive);
        Assert.DoesNotContain(_display.Details, d => d.Label == "Frozen");

        _mockClient.Object.ResponseHandlers?.Invoke("%2FREZ=1\r%2FREZ=ERR3\r");
        Assert.False(_display.FreezeActive);
        Assert.DoesNotContain(_display.Details, d => d.Label == "Frozen");
    }

    [Fact]
    public void SetFreeze_AlsoQueriesTheFreezeState()
    {
        _display.Freeze();

        _mockClient.Verify(x => x.Send("%2FREZ 1\r"), Times.Once);
        _mockClient.Verify(x => x.Send("%2FREZ ?\r"), Times.Once);
    }

    [Fact]
    public void HandleResponse_BannerDoesNotReportOkayBeforeThePasswordIsAccepted()
    {
        var states = new List<CommunicationState>();
        _display.CommunicationStateHandlers += s => states.Add(s);

        _mockClient.Object.ResponseHandlers?.Invoke("PJLINK 1 3bcc52b3\r");
        Assert.DoesNotContain(CommunicationState.Okay, states);

        _mockClient.Object.ResponseHandlers?.Invoke("PJLINK ERRA\r");
        Assert.Empty(states);
        Assert.Single(_display.GetOngoingIssues(), i => i.Key == PjLink.PasswordIssueKey);
    }

    [Fact]
    public void HandleResponse_FirstAcceptedAnswerReportsOkay()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("PJLINK 1 3bcc52b3\r%1POWR=1\r");

        Assert.Equal(CommunicationState.Okay, _display.CommunicationState);
    }

    [Fact]
    public void InputNames_ReconnectWithAChangedInputListDoesNotShiftNames()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r%2INNM=Computer1\r%2INNM=HDMI1\r");
        Assert.Equal("HDMI1", display.InputNames[32]);

        client.SetConnectionState(ConnectionState.Disconnected);
        client.SetConnectionState(ConnectionState.Connected);
        lock (client.Sent) client.Sent.Clear();
        client.Feed("PJLINK 0\r%1CLSS=2\r");
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%2INNM"));

        client.Feed("%1INST=11 32 33\r");
        Assert.Equal(["%2INNM ?11\r"], client.Sent.Where(s => s.StartsWith("%2INNM")));
        client.Feed("%2INNM=Computer1\r%2INNM=HDMI1\r%2INNM=HDMI2\r");

        Assert.Equal("Computer1", display.InputNames[11]);
        Assert.Equal("HDMI1", display.InputNames[32]);
        Assert.Equal("HDMI2", display.InputNames[33]);
        lock (client.Sent) client.Sent.Clear();
        display.SetInput(Input.Hdmi1);
        Assert.Contains("%1INPT 32\r", client.Sent);
    }

    [Fact]
    public void InputNames_ListChangeMidPassRestartsAfterTheInFlightAnswer()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r");
        Assert.Equal(1, client.Sent.Count(s => s.StartsWith("%2INNM")));

        client.Feed("%1INST=11 32 33\r");
        Assert.Equal(1, client.Sent.Count(s => s.StartsWith("%2INNM")));

        client.Feed("%2INNM=Computer1\r");
        Assert.Equal(["%2INNM ?11\r", "%2INNM ?11\r"], client.Sent.Where(s => s.StartsWith("%2INNM")));

        client.Feed("%2INNM=Computer1\r%2INNM=HDMI1\r%2INNM=HDMI2\r");
        Assert.Equal("HDMI2", display.InputNames[33]);
        Assert.Equal([Input.Vga1, Input.Hdmi1, Input.Hdmi2], display.SupportedInputs);
    }

    [Fact]
    public void InputNames_QueryDeviceInformationMidPassDoesNotRestartIt()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r");

        display.QueryDeviceInformation();
        client.Feed("%1CLSS=2\r%1INST=11 32\r");

        Assert.Equal(1, client.Sent.Count(s => s.StartsWith("%2INNM")));
    }

    [Fact]
    public async Task DoPoll_RetriesDiscoveryUntilClassAndInputListAnswer()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client).NoDiscoveryThrottle();
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=ERR3\r%1INF1=ERR3\r%1INF2=ERR3\r%1INFO=ERR3\r%1NAME=ERR3\r%1INST=ERR3\r");
        Assert.False(display.DiscoveryComplete);
        lock (client.Sent) client.Sent.Clear();

        await display.Poll();
        Assert.Contains("%1CLSS ?\r", client.Sent);
        Assert.Contains("%1INST ?\r", client.Sent);
        Assert.Contains("%1NAME ?\r", client.Sent);

        client.Feed("%1CLSS=1\r%1INST=11\r%1INF1=X\r%1INF2=X\r%1INFO=X\r%1NAME=X\r");
        Assert.True(display.DiscoveryComplete);
        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        Assert.DoesNotContain("%1CLSS ?\r", client.Sent);
        Assert.DoesNotContain("%1INST ?\r", client.Sent);
    }

    [Fact]
    public async Task DoPoll_RaisesABannerIssueAfterRepeatedMissedBanners()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);

        for (int i = 0; i < PjLink.MissedBannerPollLimit * PjLink.MissedBannerReconnectLimit; i++)
            await display.Poll();

        Assert.Equal(PjLink.MissedBannerReconnectLimit, client.Reconnects);
        Assert.Single(display.GetOngoingIssues(), i => i.Key == PjLink.BannerIssueKey);
        Assert.Equal(CommunicationState.NotAttempted, display.CommunicationState);
        Assert.DoesNotContain(display.GetOngoingIssues(), i => i.Key == "communication");

        client.Feed("PJLINK 0\r%1POWR=1\r");
        Assert.DoesNotContain(display.GetOngoingIssues(), i => i.Key == PjLink.BannerIssueKey);
        Assert.Equal(CommunicationState.Okay, display.CommunicationState);
    }

    [Fact]
    public async Task Reconnect_ResetsAuthenticationAndTheInputNameQueue()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r%2INNM=Computer1\r");
        Assert.Equal("Computer1", display.InputNames[11]);

        client.SetConnectionState(ConnectionState.Disconnected);
        client.SetConnectionState(ConnectionState.Connected);
        lock (client.Sent) client.Sent.Clear();

        await display.Poll();
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%1POWR"));

        client.Feed("%2INNM=HDMI1\r");
        Assert.Equal(new Dictionary<int, string> { { 11, "Computer1" } }, display.InputNames);
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%2INNM"));
    }

    [Theory]
    [InlineData(32, "HDMI1", Input.Hdmi1)]
    [InlineData(36, "HDMI 3", Input.Hdmi3)]
    [InlineData(31, "HDMI", Input.Hdmi1)]
    [InlineData(11, "Computer1", Input.Vga1)]
    [InlineData(12, "RGB-2", Input.Vga2)]
    [InlineData(13, "Input B", Input.Vga2)]
    [InlineData(21, "Video", Input.Composite)]
    [InlineData(22, "S-Video", Input.SVideo)]
    [InlineData(23, "YPbPr", Input.Component)]
    [InlineData(33, "DVI-D", Input.Dvi1)]
    [InlineData(34, "DisplayPort", Input.DisplayPort)]
    [InlineData(35, "HDBaseT", Input.HdBaseT)]
    [InlineData(37, "DIGITAL LINK", Input.HdBaseT)]
    [InlineData(38, "SDI 1", Input.Sdi)]
    [InlineData(41, "USB", Input.Usb1)]
    [InlineData(42, "USB-B", Input.Usb2)]
    [InlineData(53, "USB Display", Input.UsbDisplay)]
    [InlineData(52, "LAN", Input.Network1)]
    [InlineData(54, "Screen Mirroring", Input.ScreenMirroring)]
    [InlineData(55, "Miracast", Input.ScreenMirroring)]
    [InlineData(54, "Wireless", Input.Network1)]
    [InlineData(11, null, Input.Vga1)]
    [InlineData(21, null, Input.Composite)]
    [InlineData(36, null, Input.Hdmi6)]
    [InlineData(56, null, Input.Network6)]
    [InlineData(61, null, Input.Internal1)]
    [InlineData(61, "Whiteboard", Input.Internal1)]
    [InlineData(31, "HDMI 1/MHL", Input.Hdmi1)]
    [InlineData(32, "HDMI2 (4K)", Input.Hdmi2)]
    [InlineData(11, "Computer (RGB)", Input.Vga1)]
    [InlineData(13, "INPUT C", Input.Vga3)]
    [InlineData(31, "DIGITAL 1", Input.Hdmi1)]
    [InlineData(33, "HDMI3/MHL", Input.Hdmi3)]
    public void TryMapInput_MapsKnownNamesAndCodes(int code, string? name, Input expected)
    {
        Assert.True(PjLink.TryMapInput(code, name, out var input));
        Assert.Equal(expected, input);
    }

    [Theory]
    [InlineData(71, "Whiteboard")]
    [InlineData(37, "HDMI7")]
    [InlineData(99, null)]
    [InlineData(14, null)]
    public void TryMapInput_RejectsUnknownNamesAndCodes(int code, string? name)
    {
        Assert.False(PjLink.TryMapInput(code, name, out _));
    }

    [Theory]
    [InlineData(true, "%2FREZ 1\r")]
    [InlineData(false, "%2FREZ 0\r")]
    public void SetFreeze_SendsTheExpectedCommand(bool freeze, string command)
    {
        _display.SetFreeze(freeze);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Fact]
    public void VolumeSteps_SendClass2Commands()
    {
        _display.SpeakerVolumeUp();
        _display.SpeakerVolumeDown();
        _display.MicrophoneVolumeUp();
        _display.MicrophoneVolumeDown();

        _mockClient.Verify(x => x.Send("%2SVOL 1\r"), Times.Once);
        _mockClient.Verify(x => x.Send("%2SVOL 0\r"), Times.Once);
        _mockClient.Verify(x => x.Send("%2MVOL 1\r"), Times.Once);
        _mockClient.Verify(x => x.Send("%2MVOL 0\r"), Times.Once);
    }

    [Fact]
    public void CustomInputMap_IsUsedForSetAndFeedback()
    {
        var client = TestFactory.CreateTcpClient();
        var display = new PjLink(client.Object, "Epson", null, PjLink.DefaultPassword,
            new Dictionary<Input, int> { { Input.Hdmi1, 32 }, { Input.Hdmi2, 33 } });

        client.Object.ResponseHandlers?.Invoke("%1INPT=32\r");
        Assert.Equal(Input.Hdmi1, display.Input);

        display.SetInput(Input.Hdmi2);
        client.Verify(x => x.Send("%1INPT 33\r"), Times.Once);
        Assert.Equal([Input.Hdmi1, Input.Hdmi2], display.SupportedInputs);
    }

    [Fact]
    public async Task InputNames_IncompletePassKeepsDesiredInputAndAssertsItOnceComplete()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client, Input.Hdmi1);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1POWR=1\r%1CLSS=2\r");
        display.PowerOn();
        Assert.Equal(Input.Hdmi1, display.DesiredInput);

        client.Feed("%1INST=11 32\r%2INNM=Computer1\r%2INNM=ERR3\r");
        Assert.Equal(Input.Hdmi1, display.DesiredInput);
        Assert.Equal(PjLink.DefaultInputMap.Keys, display.SupportedInputs);
        Assert.DoesNotContain(display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey);

        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        client.Feed("%2INNM=Computer1\r%2INNM=HDMI1\r");
        Assert.Equal([Input.Vga1, Input.Hdmi1], display.SupportedInputs);

        client.Feed("%1INPT=11\r");
        Assert.Contains("%1INPT 32\r", client.Sent);
    }

    [Theory]
    [InlineData("31 32 11", "HDMI 1/MHL|HDMI 2|Computer (RGB)", new[] { Input.Hdmi1, Input.Hdmi2, Input.Vga1 })]
    [InlineData("31 32", "HDMI1 (4K)|HDMI2 (4K)", new[] { Input.Hdmi1, Input.Hdmi2 })]
    [InlineData("11 12 13 31", "INPUT A|INPUT B|INPUT C|HDMI 1", new[] { Input.Vga1, Input.Vga2, Input.Vga3, Input.Hdmi1 })]
    [InlineData("31 32", "DIGITAL 1|DIGITAL 2", new[] { Input.Hdmi1, Input.Hdmi2 })]
    [InlineData("31 32 33", "HDMI1|HDMI2|HDMI3/MHL", new[] { Input.Hdmi1, Input.Hdmi2, Input.Hdmi3 })]
    public void InputMap_RealWorldLabelsAllMap(string inst, string names, Input[] expected)
    {
        _mockClient.Object.ResponseHandlers?.Invoke($"%1CLSS=2\r%1INST={inst}\r");
        foreach (var name in names.Split('|'))
            _mockClient.Object.ResponseHandlers?.Invoke($"%2INNM={name}\r");

        Assert.Equal(expected, _display.SupportedInputs);
        Assert.Empty(_display.UnmappedInputCodes);
    }

    [Fact]
    public async Task DoPoll_IdleTicksRaiseNoChangeEvents()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client).NoDiscoveryThrottle();
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r%2INNM=Computer1\r%2INNM=HDMI1\r%1INF1=EPSON\r%1INF2=X\r%1INFO=Y\r%1NAME=Z\r%2SNUM=S\r%2SVER=V\r%2RLMP=L\r%2RFIL=F\r%2RRES=R\r");
        int inputEvents = 0, infoEvents = 0;
        display.OnSupportedInputsChanged += _ => inputEvents++;
        display.OnDeviceInformationChanged += () => infoEvents++;
        lock (client.Sent) client.Sent.Clear();

        for (int i = 0; i < 3; i++)
        {
            await display.Poll();
            client.Feed("%1POWR=1\r%1INPT=32\r%1AVMT=30\r");
        }

        Assert.Equal(0, inputEvents);
        Assert.Equal(0, infoEvents);
        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%2INNM"));
    }

    [Fact]
    public void Reconnects_WithTheSameInputListRaiseOneSupportedInputsChange()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        int inputEvents = 0;
        display.OnSupportedInputsChanged += _ => inputEvents++;

        for (int i = 0; i < 20; i++)
        {
            client.SetConnectionState(ConnectionState.Disconnected);
            client.SetConnectionState(ConnectionState.Connected);
            client.Feed("PJLINK 0\r%1CLSS=2\r%1INST=11 32\r%2INNM=Computer1\r%2INNM=HDMI1\r");
        }

        Assert.Equal(1, inputEvents);
        Assert.Equal([Input.Vga1, Input.Hdmi1], display.SupportedInputs);
    }

    [Fact]
    public async Task InputNames_StandbyRetryBacksOff()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client).NoDiscoveryThrottle();
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1POWR=0\r%1CLSS=2\r%1INST=11 32\r%2INNM=ERR3\r%2INNM=ERR3\r");

        int Passes() => client.Sent.Count(s => s == "%2INNM ?11\r");
        int before = Passes();
        await display.Poll();                        // retry after 1 tick
        Assert.Equal(before + 1, Passes());
        client.Feed("%2INNM=ERR3\r%2INNM=ERR3\r");
        await display.Poll();                        // back-off now 2 ticks: nothing yet
        Assert.Equal(before + 1, Passes());
        await display.Poll();
        Assert.Equal(before + 2, Passes());
    }

    [Fact]
    public async Task HandleResponse_Err1ToASetDoesNotSilenceTheCommand()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client).NoDiscoveryThrottle();
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=1\r%1INST=31 32\r");

        display.SetInput(Input.Hdmi2);
        client.Feed("%1INPT=ERR1\r");

        Assert.DoesNotContain("INPT", display.UnsupportedCommands);
        Assert.Single(display.GetIssues(), i => i.Key == "err1-INPT");
        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        Assert.Contains("%1INPT ?\r", client.Sent);
        display.SetInput(Input.Hdmi1);
        Assert.Contains("%1INPT 31\r", client.Sent);

        await display.Poll();
        client.Feed("%1INPT=ERR1\r");
        Assert.DoesNotContain("INPT", display.UnsupportedCommands);
    }

    [Fact]
    public async Task HandleResponse_ErraBannerRaisesPasswordIssueOnly()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);

        client.Feed("PJLINK ERRA\r");
        for (int i = 0; i < 10; i++)
            await display.Poll();

        Assert.Single(display.GetOngoingIssues(), i => i.Key == PjLink.PasswordIssueKey);
        Assert.DoesNotContain(display.GetOngoingIssues(), i => i.Key == PjLink.BannerIssueKey);
        Assert.Equal(0, client.Reconnects);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task HandleResponse_ErraReplyStopsPollingUntilTheNextBanner()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client).NoDiscoveryThrottle();
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 1 3bcc52b3\r%1POWR=ERRA\r");
        Assert.Single(display.GetOngoingIssues(), i => i.Key == PjLink.PasswordIssueKey);

        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        Assert.Empty(client.Sent);

        client.Feed("PJLINK 1 3bcc52b4\r%1POWR=1\r");
        Assert.DoesNotContain(display.GetOngoingIssues(), i => i.Key == PjLink.PasswordIssueKey);
        Assert.Equal(CommunicationState.Okay, display.CommunicationState);
        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        Assert.Contains("%1POWR ?\r", client.Sent);
    }

    [Fact]
    public void HandleResponse_InputResolutionClearsWhenThePowerLeavesOn()
    {
        string? raised = null;
        _display.OnInputResolutionChanged += r => raised = r;
        _mockClient.Object.ResponseHandlers?.Invoke("%2IRES=1920x1080\r");
        Assert.Equal("1920x1080", _display.InputResolution);

        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=0\r");

        Assert.Equal(string.Empty, _display.InputResolution);
        Assert.Equal(string.Empty, raised);
        Assert.DoesNotContain(_display.Details, d => d.Label == "Input Resolution");
    }

    [Fact]
    public void HandleResponse_DuplicateInputCodesAreIgnored()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=31 31 32\r%1POWR=1\r");

        Assert.Equal([Input.Hdmi1, Input.Hdmi2], _display.SupportedInputs);
        Assert.Equal(PowerState.On, _display.PowerState);
    }

    [Fact]
    public void HandleResponse_EmptyInformationValuesRemoveTheirDetailRows()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1NAME=Room 1\r%2SNUM=\r%1INFO=   \r");
        Assert.Contains(_display.Details, d => d.Label == "Name");
        Assert.DoesNotContain(_display.Details, d => d.Label == "Serial Number");
        Assert.DoesNotContain(_display.Details, d => d.Label == "Other Information");

        _mockClient.Object.ResponseHandlers?.Invoke("%1NAME=\r");
        Assert.DoesNotContain(_display.Details, d => d.Label == "Name");
    }

    [Fact]
    public void HandleResponse_DoesntForceMuteWhenCorrect()
    {
        _display.SetAudioMute(MuteState.On);
        _mockClient.Object.ResponseHandlers?.Invoke("%1AVMT=21\r");

        _mockClient.Verify(x => x.Send("%1AVMT 21\r"), Times.Exactly(1));
    }

    [Fact]
    public void HandleResponse_RemoteMuteIsNotUndoneWhenNothingIsDesired()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1AVMT=31\r%1AVMT=30\r%1AVMT=11\r");

        Assert.Equal(MuteState.On, _display.VideoMute);
        _mockClient.Verify(x => x.Send(It.Is<string>(s => s.StartsWith("%1AVMT"))), Times.Never);
    }

    [Fact]
    public void HandleResponse_AlphanumericInputCodesAreReportedNotParsed()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=11 3A\r%1INPT=3A\r");

        Assert.Equal([Input.Vga1], _display.SupportedInputs);
        Assert.Equal(["3A"], _display.UnrecognisedInputCodes);
        Assert.Contains("3A", Assert.Single(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey).Message);
        Assert.Equal(Input.Unknown, _display.Input);
        Assert.DoesNotContain(_display.GetIssues(), i => i.Key == "parse-INPT");
    }

    [Fact]
    public async Task DoPoll_DoesNotRepeatDiscoveryWithinTheRetryInterval()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client);
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r");
        lock (client.Sent) client.Sent.Clear();

        await display.Poll();

        Assert.DoesNotContain(client.Sent, s => s.StartsWith("%1CLSS") || s.StartsWith("%1INST"));
        Assert.Contains("%1POWR ?\r", client.Sent);
    }

    [Fact]
    public async Task HandleResponse_Err1ToInptAfterARacingQueryIsStillNotBlacklisted()
    {
        var client = new TestTcpClient();
        var display = new TestablePjLink(client).NoDiscoveryThrottle();
        client.SetConnectionState(ConnectionState.Connected);
        client.Feed("PJLINK 0\r%1CLSS=1\r%1INST=31 32\r");

        display.SetInput(Input.Hdmi2);
        await display.Poll();                 // "INPT ?" is now the last INPT request
        client.Feed("%1INPT=ERR1\r");         // ...but this ERR1 belongs to the set

        Assert.DoesNotContain("INPT", display.UnsupportedCommands);
        Assert.Single(display.GetIssues(), i => i.Key == "err1-INPT");
        lock (client.Sent) client.Sent.Clear();
        await display.Poll();
        Assert.Contains("%1INPT ?\r", client.Sent);
    }

    [Fact]
    public void HandleResponse_StuckTransitionIsForcedAfterTheTolerance()
    {
        _display.PowerOff();
        for (int i = 0; i < 5; i++)
            _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=3\r");

        _mockClient.Verify(x => x.Send("%1POWR 0\r"), Times.Exactly(1));
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "power-state");

        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=3\r");

        _mockClient.Verify(x => x.Send("%1POWR 0\r"), Times.Exactly(2));
        Assert.Single(_display.GetOngoingIssues(), i => i.Key == "power-state");
    }

    [Fact]
    public void HandleResponse_TransitionToleranceResetsOnceTheTransitionEnds()
    {
        _display.PowerOn();
        for (int i = 0; i < 5; i++)
            _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=3\r");
        _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=1\r");
        for (int i = 0; i < 5; i++)
            _mockClient.Object.ResponseHandlers?.Invoke("%1POWR=3\r");

        _mockClient.Verify(x => x.Send("%1POWR 1\r"), Times.Exactly(1));
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == "power-state");
    }

    [Fact]
    public void InputMap_NewUnrecognisedCodeRaisesTheIssueEvenWhenTheMapIsUnchanged()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1CLSS=1\r%1INST=11 32\r");
        Assert.DoesNotContain(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey);

        _mockClient.Object.ResponseHandlers?.Invoke("%1INST=11 32 3A\r");

        Assert.Contains("3A", Assert.Single(_display.GetOngoingIssues(), i => i.Key == PjLink.InputMapIssueKey).Message);
        Assert.Equal([Input.Vga1, Input.Hdmi2], _display.SupportedInputs);
    }

    [Fact]
    public void SetPictureMute_KeepsARemoteAudioMute()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("%1AVMT=21\r");

        _display.SetPictureMute(MuteState.On);

        _mockClient.Verify(x => x.Send("%1AVMT 31\r"), Times.Once);
        _mockClient.Verify(x => x.Send("%1AVMT 11\r"), Times.Never);
    }
}
