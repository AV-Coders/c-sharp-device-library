using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.MediaPlayer;
using Moq;

namespace AVCoders.Display.Tests;

public class SonySimpleIPControlTest
{
    private readonly SonySimpleIpControl _sonyTv;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    readonly Mock<PowerStateHandler> _powerStateHandler = new ();
    private static readonly RemoteButton[] _excludedButtons = 
    [
        RemoteButton.Display, RemoteButton.Eject, 
        RemoteButton.PopupMenu, RemoteButton.TopMenu,
        RemoteButton.PowerOn, RemoteButton.PowerOff,
        RemoteButton.Guide
    ];
    public static IEnumerable<object[]> RemoteButtonValues()
    {
        return Enum.GetValues(typeof(RemoteButton))
            .Cast<RemoteButton>()
            .Where(rb => !_excludedButtons.Contains(rb))
            .Select(rb => new object[] { rb });
    }

    public SonySimpleIPControlTest()
    {
        // Most tests want the external-audio latch to be reachable without waiting out the boot grace.
        SonySimpleIpControl.ExternalAudioGrace = TimeSpan.Zero;
        SonySimpleIpControl.OutstandingExpiry = TimeSpan.FromSeconds(8);
        _sonyTv = new SonySimpleIpControl(_mockClient.Object, "Test display", Input.Hdmi1);
        _sonyTv.PowerStateHandlers += _powerStateHandler.Object;
    }

    [Fact]
    public void SendCommand_DoesNotManipulateInput()
    {
        string input = "Foo";

        var method = _sonyTv.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);

        method?.Invoke(_sonyTv, [input]);
        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendCommand_DoesNotReportOkayUntilTheTvAnswers()
    {
        _sonyTv.PowerOn();
        Assert.Equal(CommunicationState.NotAttempted, _sonyTv.CommunicationState);

        _mockClient.Object.ResponseHandlers?.Invoke("*SAPOWR0000000000000000\n");
        Assert.Equal(CommunicationState.Okay, _sonyTv.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        string input = "Foo";

        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));
        var method = _sonyTv.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);

        method?.Invoke(_sonyTv, [input]);
        Assert.Equal(CommunicationState.Error, _sonyTv.CommunicationState);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        string expectedPowerOnCommand = "*SCPOWR0000000000000001\n";
        _sonyTv.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        string expectedPowerOffCommand = "*SCPOWR0000000000000000\n";
        _sonyTv.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOffCommand), Times.Once);
    }

    [Theory]
    [InlineData("*SNPOWR0000000000000001\n", PowerState.On)]
    [InlineData("*SNPOWR0000000000000000\n", PowerState.Off)]
    public void HandleResponse_SetsThePowerState(string response, PowerState expectedPowerState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        Assert.Equal(expectedPowerState, _sonyTv.PowerState);
    }

    [Theory]
    [InlineData("*SNPOWR0000000000000001\n", PowerState.On)]
    [InlineData("*SNPOWR0000000000000000\n", PowerState.Off)]
    public void HandleResponse_CallsThePowerDelegate(string response, PowerState expectedPowerState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);
        
        _powerStateHandler.Verify(x => x.Invoke(expectedPowerState));
    }

    [Fact]
    public void HandleResponse_SetsTheVolume()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("*SNVOLU0000000000000010\n");

        Assert.Equal(10, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_InvokesTheVolumeDelegate()
    {
        Mock<VolumeLevelHandler> volumeLevelHandler = new Mock<VolumeLevelHandler>();
        _sonyTv.VolumeLevelHandlers += volumeLevelHandler.Object;
        _mockClient.Object.ResponseHandlers?.Invoke("*SNVOLU0000000000000010\n");

        volumeLevelHandler.Verify(x => x.Invoke(10));
    }

    [Theory]
    [InlineData("*SNAMUT0000000000000000\n", MuteState.Off)]
    [InlineData("*SNAMUT0000000000000001\n", MuteState.On)]
    public void HandleResponse_SetsTheAudioMuteState(string input, MuteState expectedMuteState)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(input);
        
        Assert.Equal(expectedMuteState, _sonyTv.AudioMute);
    }

    [Theory]
    [InlineData("*SNAMUT0000000000000000\n", MuteState.Off)]
    [InlineData("*SNAMUT0000000000000001\n", MuteState.On)]
    public void HandleResponse_InvokesTheDelegate(string input, MuteState expectedMuteState)
    {
        Mock<MuteStateHandler> muteStateHandler = new Mock<MuteStateHandler>();
        _sonyTv.MuteStateHandlers += muteStateHandler.Object;
        _mockClient.Object.ResponseHandlers?.Invoke(input);
        
        muteStateHandler.Verify(x => x.Invoke(expectedMuteState));
    }

    [Theory]
    [InlineData("*SNINPT0000000100000001\n", Input.Hdmi1)]
    [InlineData("*SNINPT0000000100000002\n", Input.Hdmi2)]
    [InlineData("*SNINPT0000000100000003\n", Input.Hdmi3)]
    [InlineData("*SNINPT0000000100000004\n", Input.Hdmi4)]
    [InlineData("*SNINPT0000000000000000\n", Input.DvbtTuner)]
    public void HandleResponse_SetsTheInput(string response, Input expectedInput)
    {
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        Assert.Equal(expectedInput, _sonyTv.Input);
    }

    [Theory]
    [InlineData("*SNINPT0000000100000001\n", Input.Hdmi1)]
    [InlineData("*SNINPT0000000100000002\n", Input.Hdmi2)]
    [InlineData("*SNINPT0000000100000003\n", Input.Hdmi3)]
    [InlineData("*SNINPT0000000100000004\n", Input.Hdmi4)]
    [InlineData("*SNINPT0000000000000000\n", Input.DvbtTuner)]
    public void HandleResponse_InvokesTheInputDelegate(string response, Input expectedInput)
    {
        Mock<InputHandler> inputHandler = new Mock<InputHandler>();
        _sonyTv.InputHandlers += inputHandler.Object;
        _mockClient.Object.ResponseHandlers?.Invoke(response);

        inputHandler.Verify(x => x.Invoke(expectedInput));
    }

    [Fact]
    public void HandleResponse_HandlesAMultiResponseString()
    {
        _mockClient.Object.ResponseHandlers?.Invoke(
            "*SNINPT0000000000000000\n*SNPOWR0000000000000001\n*SNVOLU0000000000000010\n*SAPMUT0000000000000001\n");

        Assert.Equal(10, _sonyTv.Volume);
        Assert.Equal(PowerState.On, _sonyTv.PowerState);
        Assert.Equal(Input.DvbtTuner, _sonyTv.Input);
        Assert.Equal(MuteState.On, _sonyTv.VideoMute);
    }

    [Fact]
    public void HandleResponse_HandlesWhiteSpaceInAMultiResponseString()
    {
        _mockClient.Object.ResponseHandlers?.Invoke(
            "*SNINPT0000000000000000\n\t \t*SNPOWR0000000000000001\n                        *SNVOLU0000000000000010\n");

        Assert.Equal(10, _sonyTv.Volume);
        Assert.Equal(PowerState.On, _sonyTv.PowerState);
        Assert.Equal(Input.DvbtTuner, _sonyTv.Input);
    }

    [Theory]
    [InlineData(Input.Hdmi1, "*SCINPT0000000100000001\n")]
    [InlineData(Input.Hdmi2, "*SCINPT0000000100000002\n")]
    [InlineData(Input.Hdmi3, "*SCINPT0000000100000003\n")]
    [InlineData(Input.Hdmi4, "*SCINPT0000000100000004\n")]
    public void SetInput_SetsTheInput(Input input, string expectedInputCommand)
    {
        _sonyTv.SetInput(input);

        _mockClient.Verify(x => x.Send(expectedInputCommand), Times.Once);
    }

    [Theory]
    [InlineData(0, "*SCVOLU0000000000000000\n")]
    [InlineData(100, "*SCVOLU0000000000000100\n")]
    public void SetVolume_SetsTheVolume(int volume, string expectedVolumeCommand)
    {
        _sonyTv.SetVolume(volume);

        _mockClient.Verify(x => x.Send(expectedVolumeCommand), Times.Once);
    }

    [Fact]
    public void SetVolume_UpdatesInternalState()
    {
        _sonyTv.SetVolume(15);

        Assert.Equal(15, _sonyTv.Volume);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void SetVolume_IgnoresInvalidValues(int volume)
    {
        _sonyTv.SetVolume(volume);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(MuteState.On, "*SCAMUT0000000000000001\n")]
    [InlineData(MuteState.Off, "*SCAMUT0000000000000000\n")]
    public void setAudioMute_SendsTheCommand(MuteState state, string expectedMuteCommand)
    {
        _sonyTv.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(expectedMuteCommand), Times.Once);
    }


    [Theory]
    [InlineData(MuteState.On, "*SCPMUT0000000000000001\n")]
    [InlineData(MuteState.Off, "*SCPMUT0000000000000000\n")]
    public void setPictureMute_SendsTheCommand(MuteState state, string expectedMuteCommand)
    {
        _sonyTv.SetVideoMute(state);

        _mockClient.Verify(x => x.Send(expectedMuteCommand), Times.Once);
    }

    [Fact]
    public void setPictureMute_UpdatesInternalState()
    {
        _sonyTv.SetVideoMute(MuteState.On);

        Assert.Equal(MuteState.On, _sonyTv.VideoMute);
    }

    [Fact]
    public void SendIrCode_SendsTheCommand()
    {
        string expectedCommand = "*SCIRCC0000000000000032\n";

        _sonyTv.SendIRCode(RemoteButton.Mute);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [MemberData(nameof(RemoteButtonValues))]
    public void SendIRCode_HandlesAllRemoteButtonValues(RemoteButton button)
    {
        _mockClient.Invocations.Clear();

        _sonyTv.SendIRCode(button);

        Assert.Contains(button, _sonyTv.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public void SetChannel_SendsTheCommand()
    {
        string expectedCommand = "*SCCHNN00000002.0000000\n";

        _sonyTv.SetChannel(2);
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Fact]
    public void SetChannel_WithMinor_SendsTheCommand()
    {
        _sonyTv.SetChannel(10, 3);
        _mockClient.Verify(x => x.Send("*SCCHNN00000010.0000003\n"));
    }
    
    [Fact]
    public void ChannelUp_SendsTheCommand()
    {
        string expectedCommand = "*SCIRCC0000000000000033\n";
        
        _sonyTv.ChannelUp();
        _mockClient.Verify(x => x.Send(expectedCommand));
    }
    
    [Fact]
    public void ChannelDown_SendsTheCommand()
    {
        string expectedCommand = "*SCIRCC0000000000000034\n";

        _sonyTv.ChannelDown();
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    private void Feed(string response) => _mockClient.Object.ResponseHandlers?.Invoke(response);

    private void TurnOn() => Feed("*SNPOWR0000000000000001\n");

    private int Sent(string prefix) => _mockClient.Invocations
        .Count(i => i.Method.Name == "Send" && i.Arguments[0] is string s && s.StartsWith(prefix));

    [Fact]
    public void HandleResponse_ReassemblesASplitFrame()
    {
        Feed("*SNVOLU00000000");
        Assert.Equal(0, _sonyTv.Volume);

        Feed("00000010\n");
        Assert.Equal(10, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_IgnoresMalformedFrames()
    {
        Feed("*SNVOLU12\n*SNVOLUabcdefghijklmnop\ngarbage\n*SNVOLU0000000000000007\n");

        Assert.Equal(7, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_AppliesTheConnectSnapshot()
    {
        Feed("*SNPOWR0000000000000000\n*SNAMUT0000000000000001\n*SNVOLU0000000000000003\n*SNPMUT0000000000000000\n");

        Assert.Equal(PowerState.Off, _sonyTv.PowerState);
        Assert.Equal(MuteState.On, _sonyTv.AudioMute);
        Assert.Equal(3, _sonyTv.Volume);
        Assert.Equal(MuteState.Off, _sonyTv.VideoMute);
        Assert.Contains(_sonyTv.Details, d => d.Label == "Audio" && d.Value == "TV speakers");
        Assert.Equal(CommunicationState.Okay, _sonyTv.CommunicationState);
    }

    [Fact]
    public void HandleResponse_DistinguishesAControlAckFromAnEnquiryAnswer()
    {
        _sonyTv.QueryStatus();
        Feed("*SAPOWR0000000000000000\n");
        Assert.Equal(PowerState.Off, _sonyTv.PowerState);

        _sonyTv.PowerOn();
        Feed("*SAPOWR0000000000000000\n");
        Assert.Equal(PowerState.On, _sonyTv.PowerState);
        Assert.Equal(1, Sent("*SCPOWR"));
    }

    [Fact]
    public void HandleResponse_IgnoresAnUntrackedAmbiguousAck()
    {
        Feed("*SNVOLU0000000000000010\n");
        Feed("*SAVOLU0000000000000000\n");

        Assert.Equal(10, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_AppliesAnUntrackedUnambiguousAnswer()
    {
        Feed("*SAVOLU0000000000000042\n");

        Assert.Equal(42, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_RejectedControlRaisesAMomentaryIssueThatEscalates()
    {
        for (int i = 0; i < 3; i++)
        {
            _sonyTv.SetVolume(50);
            Feed("*SAVOLUFFFFFFFFFFFFFFFF\n");
        }

        Assert.Equal(CommunicationState.Okay, _sonyTv.CommunicationState);
        Assert.Contains(_sonyTv.GetIssues(), i => i.Key == "rejected-VOLU" && i.Status == IssueStatus.Momentary && i.OccurrenceCount == 3);
        Assert.Contains(_sonyTv.GetOngoingIssues(), i => i.Key == "rejected-VOLU");

        _sonyTv.SetVolume(50);
        Feed("*SAVOLU0000000000000000\n");
        Assert.DoesNotContain(_sonyTv.GetOngoingIssues(), i => i.Key == "rejected-VOLU");
    }

    [Fact]
    public void HandleResponse_UnsupportedEnquiryIsNeverSentAgain()
    {
        Feed("*SNINPT0000000000000000\n");
        Assert.Equal(1, Sent("*SECHNN"));
        Feed("*SACHNNNNNNNNNNNNNNNNNN\n");

        Assert.Contains("CHNN", _sonyTv.UnsupportedCommands);
        Assert.Contains(_sonyTv.Details, d => d.Label == "Unsupported Commands" && d.Value == "CHNN" && d.Tone == DetailTone.Warning);
        Assert.DoesNotContain(_sonyTv.GetIssues(), i => i.Key.StartsWith("rejected"));

        _sonyTv.SetChannel(6);
        TurnOn();
        Assert.Equal(0, Sent("*SCCHNN"));
        Assert.Equal(1, Sent("*SECHNN"));
    }

    [Fact]
    public void HandleResponse_PowerOnTriggersTheOnEnquiries()
    {
        TurnOn();

        foreach (var command in new[] { "INPT", "PMUT", "VOLU", "AMUT" })
            Assert.Equal(1, Sent($"*SE{command}################"));
        Assert.Equal(0, Sent("*SECHNN"));
    }

    [Fact]
    public void HandleResponse_NotApplicableInputOnTwoConsecutivePollsMeansHome()
    {
        TurnOn();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");
        Assert.Equal(Input.Unknown, _sonyTv.Input);

        _sonyTv.QueryStatus();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");

        Assert.Equal(Input.Home, _sonyTv.Input);
        Assert.Equal(CommunicationState.Okay, _sonyTv.CommunicationState);
    }

    [Fact]
    public void HandleResponse_NotApplicableAnswersDuringBootAreNotLatched()
    {
        _sonyTv.PowerOn();
        Assert.False(_sonyTv.TvConfirmedOn);
        _sonyTv.QueryStatus();
        Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n*SAINPTFFFFFFFFFFFFFFFF\n");
        _sonyTv.QueryStatus();
        Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n*SAINPTFFFFFFFFFFFFFFFF\n");

        Assert.False(_sonyTv.ExternalAudio);
        Assert.NotEqual(Input.Home, _sonyTv.Input);
        Assert.DoesNotContain(_sonyTv.Details, d => d.Label == "Audio");

        TurnOn();
        Feed("*SNVOLU0000000000000005\n");
        Assert.Equal(5, _sonyTv.Volume);
        Assert.Contains(_sonyTv.Details, d => d.Label == "Audio" && d.Value == "TV speakers");
    }

    [Fact]
    public void SendIRCode_HomeClearsTheDesiredInputSoHomeIsNotFought()
    {
        _sonyTv.SetInput(Input.Hdmi2);
        Feed("*SAINPT0000000000000000\n");
        Assert.Equal(Input.Hdmi2, _sonyTv.DesiredInput);

        _sonyTv.SendIRCode(RemoteButton.Home);
        Assert.Equal(Input.Unknown, _sonyTv.DesiredInput);

        TurnOn();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");
        _sonyTv.QueryStatus();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");

        Assert.Equal(Input.Home, _sonyTv.Input);
        Assert.Equal(1, Sent("*SCINPT"));
        Assert.DoesNotContain(_sonyTv.GetOngoingIssues(), i => i.Key == "input");
    }

    [Fact]
    public void HandleResponse_NotApplicableInputWhileOffIsIgnored()
    {
        Feed("*SNPOWR0000000000000000\n");
        _sonyTv.QueryStatus();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");

        Assert.Equal(Input.Unknown, _sonyTv.Input);
    }

    private void LatchExternalAudio()
    {
        TurnOn();
        Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n");
        _sonyTv.QueryStatus();
        Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n");
    }

    [Fact]
    public void HandleResponse_ExternalAudioLatchesAfterTwoPollsAndReprobesEveryFourth()
    {
        TurnOn();
        Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n");
        Assert.False(_sonyTv.ExternalAudio);

        _sonyTv.QueryStatus();
        Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n");
        Assert.True(_sonyTv.ExternalAudio);
        Assert.Equal(MuteState.Unknown, _sonyTv.AudioMute);
        Assert.Contains(_sonyTv.Details, d => d.Label == "Audio" && d.Value == "External audio system" && d.Tone == DetailTone.Normal);
        Assert.Empty(_sonyTv.GetOngoingIssues());
        Assert.Equal(2, Sent("*SEVOLU"));

        for (int i = 0; i < 3; i++)
            _sonyTv.QueryStatus();
        Assert.Equal(2, Sent("*SEVOLU"));
        Assert.Equal(2, Sent("*SEAMUT"));
        Assert.Equal(5, Sent("*SEINPT"));

        _sonyTv.QueryStatus();
        Assert.Equal(3, Sent("*SEVOLU"));
    }

    [Fact]
    public void HandleResponse_VolumeAnswerRecoversFromExternalAudio()
    {
        LatchExternalAudio();
        Assert.True(_sonyTv.ExternalAudio);

        Feed("*SNVOLU0000000000000005\n");
        Assert.False(_sonyTv.ExternalAudio);
        Assert.Contains(_sonyTv.Details, d => d.Label == "Audio" && d.Value == "TV speakers");
    }

    [Fact]
    public void SetVolume_IsRefusedWhileExternalAudioIsLatched()
    {
        LatchExternalAudio();
        int before = Sent("*SCVOLU");

        _sonyTv.SetVolume(9);
        _sonyTv.SetAudioMute(MuteState.On);

        Assert.Equal(before, Sent("*SCVOLU"));
        Assert.Equal(0, Sent("*SCAMUT"));
        Assert.Contains(_sonyTv.Events, e => e.Info.Contains("external audio system"));
    }

    [Fact]
    public void HandleResponse_OffToOnTransitionResetsExternalAudio()
    {
        LatchExternalAudio();
        Feed("*SNPOWR0000000000000000\n");
        Feed("*SNPOWR0000000000000001\n");

        Assert.False(_sonyTv.ExternalAudio);
        Assert.DoesNotContain(_sonyTv.Details, d => d.Label == "Audio");
    }

    [Fact]
    public void HandleResponse_ParsesTheChannel()
    {
        string? raised = null;
        _sonyTv.ChannelChanged += c => raised = c;

        Feed("*SNINPT0000000000000000\n*SNCHNN00000097.0000000\n");

        Assert.Equal("97.0", _sonyTv.Channel);
        Assert.Equal("97.0", raised);
        Assert.Contains(_sonyTv.Details, d => d.Label == "Channel" && d.Value == "97.0");

        Feed("*SNINPT0000000100000001\n");
        Assert.Equal(string.Empty, _sonyTv.Channel);
        Assert.DoesNotContain(_sonyTv.Details, d => d.Label == "Channel");
    }

    [Fact]
    public void HandleResponse_TunerInputEnquiresTheChannel()
    {
        Feed("*SNINPT0000000000000000\n");

        Assert.Equal(1, Sent("*SECHNN"));
    }

    [Theory]
    [InlineData("00000097.0000000", true, "97.0")]
    [InlineData("00000010.0000003", true, "10.3")]
    [InlineData("0000000000000012", true, "12")]
    [InlineData("FFFFFFFFFFFFFFFF", false, "")]
    [InlineData("1.2.3", false, "")]
    public void TryParseChannel_HandlesTheFormats(string value, bool expectedOk, string expected)
    {
        bool ok = SonySimpleIpControl.TryParseChannel(value, out string channel);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expected, channel);
    }

    [Theory]
    [InlineData("*SNINPT0000000300000001\n", Input.Composite)]
    [InlineData("*SNINPT0000000200000001\n", Input.Scart)]
    [InlineData("*SNINPT0000000400000001\n", Input.Component)]
    [InlineData("*SNINPT0000000500000001\n", Input.ScreenMirroring)]
    [InlineData("*SNINPT0000000600000001\n", Input.Pc)]
    [InlineData("*SNINPT0000000900000001\n", Input.Unknown)]
    public void HandleResponse_MapsTheAdditionalInputs(string response, Input expected)
    {
        Feed(response);

        Assert.Equal(expected, _sonyTv.Input);
    }

    [Fact]
    public void Track_DropsTheNewestWhenTooManyRequestsAreOutstanding()
    {
        // 9 rapid volume sets, then a poll enquiry, then the answers arrive in order: 9 acks then the
        // enquiry answer. Only the first 8 sets are tracked; the 9th ack and the enquiry answer are
        // untracked, so the poll's answer is applied as state and the acks never masquerade as state.
        TurnOn();
        Feed("*SAVOLU0000000000000003\n");
        for (int i = 1; i <= SonySimpleIpControl.MaxOutstandingPerCommand + 1; i++)
            _sonyTv.SetVolume(i);
        _sonyTv.QueryStatus();

        for (int i = 0; i < SonySimpleIpControl.MaxOutstandingPerCommand + 1; i++)
            Feed("*SAVOLU0000000000000000\n");
        Assert.Equal(SonySimpleIpControl.MaxOutstandingPerCommand + 1, _sonyTv.Volume);

        Feed("*SAVOLU0000000000000042\n");
        Assert.Equal(42, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_ExternalAudioDoesNotLatchInsideTheBootGrace()
    {
        SonySimpleIpControl.ExternalAudioGrace = TimeSpan.FromSeconds(45);
        try
        {
            TurnOn();
            Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n");
            _sonyTv.QueryStatus();
            Feed("*SAVOLUFFFFFFFFFFFFFFFF\n*SAAMUTFFFFFFFFFFFFFFFF\n");
            Assert.False(_sonyTv.ExternalAudio);

            _sonyTv.SetVolume(4);
            Assert.Equal(1, Sent("*SCVOLU"));
            Feed("*SAVOLU0000000000000000\n");

            SonySimpleIpControl.ExternalAudioGrace = TimeSpan.Zero;
            _sonyTv.QueryStatus();
            Feed("*SAVOLUFFFFFFFFFFFFFFFF\n");
            Assert.True(_sonyTv.ExternalAudio);
        }
        finally
        {
            SonySimpleIpControl.ExternalAudioGrace = TimeSpan.Zero;
        }
    }

    [Fact]
    public void QueryStatus_SendsOneInputEnquiryPerTickAroundPowerOn()
    {
        _sonyTv.PowerOn();
        _sonyTv.QueryStatus();
        Assert.Equal(0, Sent("*SEINPT"));

        Feed("*SAPOWR0000000000000001\n");
        Assert.Equal(1, Sent("*SEINPT"));

        _sonyTv.QueryStatus();
        Assert.Equal(2, Sent("*SEINPT"));
    }

    [Fact]
    public void HandleResponse_TwoNotApplicableInputAnswersInOneRoundCountOnce()
    {
        TurnOn();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n*SAINPTFFFFFFFFFFFFFFFF\n");

        Assert.NotEqual(Input.Home, _sonyTv.Input);
        Assert.Equal(0, Sent("*SCINPT"));
    }

    [Fact]
    public void SetVolumeAndMute_AreRefusedWithoutChangingStateWhileExternalAudio()
    {
        LatchExternalAudio();
        int events = _sonyTv.Events.Count;
        int volumeBefore = _sonyTv.Volume;

        _sonyTv.SetVolume(9);
        _sonyTv.SetAudioMute(MuteState.On);

        Assert.Equal(volumeBefore, _sonyTv.Volume);
        Assert.Equal(MuteState.Unknown, _sonyTv.AudioMute);
        Assert.Equal(0, Sent("*SCVOLU"));
        Assert.Equal(0, Sent("*SCAMUT"));
        Assert.Equal(events + 2, _sonyTv.Events.Count);
    }

    [Fact]
    public void PowerOn_OnAnAlreadyOnTvKeepsTheExternalAudioLatch()
    {
        LatchExternalAudio();
        Feed("*SAPOWR0000000000000001\n"); // answers the enquiry LatchExternalAudio's QueryStatus sent
        Assert.True(_sonyTv.TvConfirmedOn, "confirmed before PowerOn");

        _sonyTv.PowerOn();
        Assert.True(_sonyTv.TvConfirmedOn, "confirmed after PowerOn");
        Feed("*SAPOWR0000000000000000\n");
        _sonyTv.QueryStatus();
        Feed("*SAPOWR0000000000000001\n");
        Assert.True(_sonyTv.ExternalAudio, "latch after re-confirmation");

        Assert.True(_sonyTv.ExternalAudio);
        Assert.Contains(_sonyTv.Details, d => d.Label == "Audio" && d.Value == "External audio system");
        Assert.Equal(0, Sent("*SCVOLU"));
    }

    [Fact]
    public void HandleResponse_OffAnswerDuringBootDoesNotClearInputState()
    {
        TurnOn();
        Feed("*SNINPT0000000100000002\n");
        Assert.Equal(Input.Hdmi2, _sonyTv.Input);

        _sonyTv.PowerOn();
        Feed("*SAPOWR0000000000000000\n"); // ack of the power-on control
        _sonyTv.QueryStatus();
        Feed("*SAPOWR0000000000000000\n"); // the TV is still in standby

        Assert.Equal(Input.Hdmi2, _sonyTv.Input);
        Assert.Equal(PowerState.On, _sonyTv.PowerState);
    }

    [Fact]
    public void PowerOff_InsideTheBootWindowStillClearsInputStateImmediately()
    {
        TurnOn();
        Feed("*SNINPT0000000100000002\n");
        _sonyTv.PowerOn();
        Feed("*SAPOWR0000000000000000\n");

        _sonyTv.PowerOff();

        Assert.Equal(Input.Unknown, _sonyTv.Input);
        Assert.Equal(PowerState.Off, _sonyTv.PowerState);
    }

    [Fact]
    public void HandleResponse_UntrackedNotApplicableAnswersDoNotMoveTheDetectors()
    {
        TurnOn();
        Feed("*SAINPT0000000100000001\n");
        // 9 rapid input sets: the 9th is beyond the cap and untracked. Its rejection must not count
        // as a Home probe round.
        for (int i = 0; i < SonySimpleIpControl.MaxOutstandingPerCommand + 1; i++)
            _sonyTv.SetInput(Input.Pc);
        for (int i = 0; i < SonySimpleIpControl.MaxOutstandingPerCommand + 1; i++)
            Feed("*SAINPTFFFFFFFFFFFFFFFF\n");

        _sonyTv.QueryStatus();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");

        Assert.NotEqual(Input.Home, _sonyTv.Input);
    }

    [Fact]
    public void HandleResponse_HomeIsDeferredWhileBooting()
    {
        _sonyTv.PowerOn();
        Feed("*SAPOWR0000000000000000\n");
        _sonyTv.SendIRCode(RemoteButton.Home); // no desired input, so Home is not fought once reported
        Feed("*SNPOWR0000000000000001\n");
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");
        _sonyTv.QueryStatus();
        Feed("*SAINPTFFFFFFFFFFFFFFFF\n");
        Assert.NotEqual(Input.Home, _sonyTv.Input);

        SonySimpleIpControl.PowerOnBootWindow = TimeSpan.Zero;
        try
        {
            // Rounds inside the boot window were deferred, not counted, so two rounds are needed now.
            _sonyTv.QueryStatus();
            Feed("*SAINPTFFFFFFFFFFFFFFFF\n");
            Assert.NotEqual(Input.Home, _sonyTv.Input);
            _sonyTv.QueryStatus();
            Feed("*SAINPTFFFFFFFFFFFFFFFF\n");
            Assert.Equal(Input.Home, _sonyTv.Input);
        }
        finally
        {
            SonySimpleIpControl.PowerOnBootWindow = TimeSpan.FromSeconds(30);
        }
    }

    [Fact]
    public void HandleResponse_OffAnswerOutsideBootClearsInputState()
    {
        SonySimpleIpControl.PowerOnBootWindow = TimeSpan.Zero;
        try
        {
            TurnOn();
            Feed("*SNINPT0000000100000002\n");
            _sonyTv.PowerOn();
            Feed("*SAPOWR0000000000000000\n"); // ack of the power-on control
            _sonyTv.QueryStatus();
            Feed("*SAPOWR0000000000000000\n");

            Assert.Equal(Input.Unknown, _sonyTv.Input);
        }
        finally
        {
            SonySimpleIpControl.PowerOnBootWindow = TimeSpan.FromSeconds(30);
        }
    }

    [Fact]
    public void Untrack_ExpiredRequestsDoNotMistypeLaterAnswers()
    {
        var previous = SonySimpleIpControl.OutstandingExpiry;
        SonySimpleIpControl.OutstandingExpiry = TimeSpan.Zero;
        try
        {
            _sonyTv.QueryStatus();
            Feed("*SAPOWR0000000000000001\n");
            _sonyTv.QueryStatus();

            _sonyTv.PowerOn();
            Feed("*SAPOWR0000000000000000\n");

            Assert.Equal(PowerState.On, _sonyTv.PowerState);
            Assert.Equal(1, Sent("*SCPOWR"));
        }
        finally
        {
            SonySimpleIpControl.OutstandingExpiry = previous;
        }
    }

    [Fact]
    public void HandleResponse_LateAnswerOverlappingTheNextRequestIsMatchedInOrder()
    {
        TurnOn();
        Assert.Equal(1, Sent("*SEVOLU"));
        _sonyTv.SetVolume(50);

        Feed("*SAVOLU0000000000000003\n");
        Assert.Equal(3, _sonyTv.Volume);
        Feed("*SAVOLU0000000000000000\n");
        Assert.Equal(3, _sonyTv.Volume);

        _sonyTv.SetVolume(40);
        _sonyTv.QueryStatus();
        Feed("*SAVOLU0000000000000000\n");
        Assert.Equal(40, _sonyTv.Volume);
        Feed("*SAVOLU0000000000000041\n");
        Assert.Equal(41, _sonyTv.Volume);
    }

    [Fact]
    public void HandleResponse_NonZeroValueIsStateEvenWhenAControlIsOutstanding()
    {
        _sonyTv.SetVolume(50);
        Feed("*SAVOLU0000000000000007\n");

        Assert.Equal(7, _sonyTv.Volume);
    }

    [Fact]
    public void PowerOff_ClearsInputStateAndTheInputIssue()
    {
        TurnOn();
        _sonyTv.SetInput(Input.Hdmi1);
        Feed("*SNINPT0000000000000000\n*SNCHNN00000010.0000000\n");
        Assert.Contains(_sonyTv.GetOngoingIssues(), i => i.Key == "input");

        _sonyTv.PowerOff();

        Assert.DoesNotContain(_sonyTv.GetOngoingIssues(), i => i.Key == "input");
        Assert.Equal(Input.Unknown, _sonyTv.Input);
        Assert.Equal(string.Empty, _sonyTv.Channel);
        Assert.DoesNotContain(_sonyTv.Details, d => d.Label == "Channel");
    }

    [Fact]
    public void HandleResponse_TvReportedOffClearsInputStateAndTheInputIssue()
    {
        TurnOn();
        _sonyTv.SetInput(Input.Hdmi1);
        Feed("*SNINPT0000000000000000\n");
        Assert.Contains(_sonyTv.GetOngoingIssues(), i => i.Key == "input");

        Feed("*SNPOWR0000000000000000\n");

        Assert.DoesNotContain(_sonyTv.GetOngoingIssues(), i => i.Key == "input");
        Assert.Equal(Input.Unknown, _sonyTv.Input);
        Assert.False(_sonyTv.TvConfirmedOn);
    }

    [Fact]
    public void HandleResponse_ControlUnsupportedDoesNotBlacklistTheCommand()
    {
        _sonyTv.SetInput(Input.Pc);
        Feed("*SAINPTNNNNNNNNNNNNNNNN\n");

        Assert.Empty(_sonyTv.UnsupportedCommands);
        Assert.Contains(_sonyTv.GetIssues(), i => i.Key == "rejected-INPT");

        TurnOn();
        Assert.Equal(1, Sent("*SEINPT"));
    }

    [Fact]
    public void HandleResponse_HandlesCrLfFrames()
    {
        Feed("*SNVOLU0000000000000010\r\n*SNPMUT0000000000000001\r\n");

        Assert.Equal(10, _sonyTv.Volume);
        Assert.Equal(MuteState.On, _sonyTv.VideoMute);
    }

    [Theory]
    [InlineData("*SNINPT0000000100000005\n", Input.Hdmi5)]
    [InlineData("*SNINPT0000000300000002\n", Input.Composite)]
    [InlineData("*SNINPT0000000100000099\n", Input.Unknown)]
    public void HandleResponse_ParsesInputsGenerically(string response, Input expected)
    {
        Feed(response);

        Assert.Equal(expected, _sonyTv.Input);
    }

    [Fact]
    public async Task ConnectionState_ConnectedResetsEverythingAndPollsOnce()
    {
        var client = new TestTcpClient();
        var tv = new TestableSony(client);
        string? channelRaised = null;
        tv.ChannelChanged += c => channelRaised = c;

        client.Feed("*SNPOWR0000000000000001\n");
        client.Feed("*SAVOLUFFFFFFFFFFFFFFFF\n");
        tv.QueryStatus();
        client.Feed("*SAVOLUFFFFFFFFFFFFFFFF\n");
        client.Feed("*SNINPT0000000000000000\n*SNCHNN00000010.0000000\n");
        client.Feed("*SACHNNNNNNNNNNNNNNNNNN\n");
        // Answer the two outstanding PMUT enquiries so the rejections below are matched to controls.
        client.Feed("*SAPMUT0000000000000000\n*SAPMUT0000000000000000\n");
        for (int i = 0; i < 3; i++)
        {
            tv.SetVideoMute(MuteState.On);
            client.Feed("*SAPMUTFFFFFFFFFFFFFFFF\n");
        }
        Assert.True(tv.ExternalAudio);
        Assert.NotEmpty(tv.UnsupportedCommands);
        Assert.Equal("10.0", tv.Channel);
        Assert.Contains(tv.GetOngoingIssues(), i => i.Key == "rejected-PMUT");
        client.ClearSent();
        int restartsBefore = tv.RestartCount;

        client.SetConnectionState(ConnectionState.Connected);

        Assert.False(tv.ExternalAudio);
        Assert.False(tv.TvConfirmedOn);
        Assert.Empty(tv.UnsupportedCommands);
        Assert.Equal(string.Empty, tv.Channel);
        Assert.Equal(string.Empty, channelRaised);
        Assert.DoesNotContain(tv.Details, d => d.Label is "Audio" or "Channel" or "Unsupported Commands");
        Assert.DoesNotContain(tv.GetOngoingIssues(), i => i.Key.StartsWith("rejected-"));
        Assert.Equal(restartsBefore + 1, tv.RestartCount);
        Assert.Empty(client.Sent);
        await Task.CompletedTask;
    }

    private class TestTcpClient() : TcpClient("host", 20060, "Test", CommandStringFormat.Ascii)
    {
        private readonly object _sentLock = new();
        private readonly List<string> _sent = [];
        public List<string> Sent { get { lock (_sentLock) return [.._sent]; } }
        public void ClearSent() { lock (_sentLock) _sent.Clear(); }
        public override void Send(string message) { lock (_sentLock) _sent.Add(message); }
        public override void Send(byte[] bytes) { lock (_sentLock) _sent.Add(System.Text.Encoding.ASCII.GetString(bytes)); }
        public override void Connect() { }
        public override void Reconnect() { }
        public override void Disconnect() { }
        protected override Task ProcessSendQueue(CancellationToken token) => Task.CompletedTask;
        protected override Task CheckConnectionState(CancellationToken token) => Task.CompletedTask;
        public void SetConnectionState(ConnectionState state) => ConnectionState = state;
        protected override Task Receive(CancellationToken token) => Task.CompletedTask;
        public void Feed(string response) => InvokeResponseHandlers(response);
    }

    private class TestableSony(TcpClient client) : SonySimpleIpControl(client, "Test", null)
    {
        public int RestartCount;
        public Task Poll() => DoPoll(CancellationToken.None);
        protected override void RestartPolling() => RestartCount++;
    }

    [Fact]
    public void QueryStatus_EnquiresPowerOnlyUntilTheTvIsOn()
    {
        _sonyTv.QueryStatus();
        Assert.Equal(1, Sent("*SE"));
        Assert.Equal(1, Sent("*SEPOWR"));

        Feed("*SAPOWR0000000000000001\n");
        Feed("*SNINPT0000000000000000\n");
        int before = _mockClient.Invocations.Count;
        _sonyTv.QueryStatus();

        var sent = _mockClient.Invocations.Skip(before)
            .Where(i => i.Method.Name == "Send").Select(i => (string)i.Arguments[0]).ToList();
        Assert.Equal(
            ["*SEPOWR################\n", "*SEINPT################\n", "*SEPMUT################\n",
             "*SEVOLU################\n", "*SEAMUT################\n", "*SECHNN################\n"],
            sent);
    }

    [Fact]
    public async Task DoPoll_EnquiresWhenConnected()
    {
        var client = new TestTcpClient();
        var tv = new TestableSony(client);
        int restartsBefore = tv.RestartCount;
        client.SetConnectionState(ConnectionState.Connected);
        client.ClearSent();

        await tv.Poll();

        Assert.Equal(["*SEPOWR################\n"], client.Sent);
        Assert.Equal(restartsBefore + 1, tv.RestartCount);
    }

    [Fact]
    public async Task DoPoll_DoesNothingWhileDisconnected()
    {
        var client = new TestTcpClient();
        var tv = new TestableSony(client);
        client.SetConnectionState(ConnectionState.Disconnected);
        client.Sent.Clear();

        await tv.Poll();

        Assert.Empty(client.Sent);
    }

    [Fact]
    public void SupportedButtons_MatchTheRemoteMap()
    {
        Assert.Contains(RemoteButton.Home, _sonyTv.SupportedButtons);
        Assert.Contains(RemoteButton.Button0, _sonyTv.SupportedButtons);
        Assert.Contains(RemoteButton.Subtitle, _sonyTv.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Guide, _sonyTv.SupportedButtons);
        Assert.DoesNotContain(RemoteButton.Eject, _sonyTv.SupportedButtons);
    }

    [Fact]
    public void SendIRCode_UnsupportedButtonSendsNothing()
    {
        _sonyTv.SendIRCode(RemoteButton.Guide);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SupportedButtons_AreExactlyTheButtonsTheTestExpects()
    {
        Assert.Equal(Enum.GetValues<RemoteButton>().Except(_excludedButtons).OrderBy(b => b), _sonyTv.SupportedButtons.OrderBy(b => b));
    }

    public static IEnumerable<object[]> ExcludedButtonValues() => _excludedButtons.Select(rb => new object[] { rb });

    [Theory]
    [MemberData(nameof(ExcludedButtonValues))]
    public void SendIRCode_ExcludedButtonsAreNotSupportedAndSendNothing(RemoteButton button)
    {
        _mockClient.Invocations.Clear();
        _sonyTv.SendIRCode(button);

        Assert.DoesNotContain(button, _sonyTv.SupportedButtons);
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }
}
