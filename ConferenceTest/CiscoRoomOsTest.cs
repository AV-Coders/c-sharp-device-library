using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoRoomOsTest
{

    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly Mock<CommunicationStateHandler> _communicationStateHandlers = new ();
    private readonly Mock<PowerStateHandler> _powerStateHandlers = new ();
    private readonly Mock<VolumeLevelHandler> _outputVolumeLevelHandler = new ();
    private readonly Mock<MuteStateHandler> _outputMuteStateHandler = new ();
    private readonly Mock<MuteStateHandler> _microphoneMuteStateHandler = new ();
    private readonly Mock<CallStatusHandler> _callStatusHandler = new ();
    private readonly Mock<ActiveCallHandler> _activeCallHandler = new ();
    
    private readonly CiscoRoomOs _codec;

    public CiscoRoomOsTest()
    {
        _codec = new CiscoRoomOs(_mockClient.Object, new CiscoRoomOsDeviceInfo("Test", "Xunit", "An Awesome Laptop", "012934"));
        _codec.CommunicationStateHandlers += _communicationStateHandlers.Object;
        _codec.PowerStateHandlers += _powerStateHandlers.Object;
        _codec.OutputVolume.VolumeLevelHandlers += _outputVolumeLevelHandler.Object;
        _codec.OutputMute.MuteStateHandlers += _outputMuteStateHandler.Object;
        _codec.MicrophoneMute.MuteStateHandlers += _microphoneMuteStateHandler.Object;
        _codec.CallStatusHandlers += _callStatusHandler.Object;
        _codec.ActiveCallHandlers += _activeCallHandler.Object;
    }

    [Fact]
    public void Module_RegistersAndSubscribes()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        new List<string> {
            "xFeedback register /Status/Standby",
            "xFeedback register /Status/Call",
            "xFeedback Register Configuration/Conference/AutoAnswer/Mode",
            "xStatus Standby",
            "xStatus Call",
            "xStatus SIP Registration URI",
            "xConfiguration Conference AutoAnswer Mode",
        }.ForEach(s => 
            _mockClient.Verify(x => x.Send($"{s}\r\n")));

        Assert.StartsWith("xCommand Peripherals Connect ID: AV-Coders-RoomOS-Module Type: ControlSystem", (string) _mockClient.Invocations[0].Arguments[0]);
    }

    [Fact]
    public void HeartbeatOkay_UpdatesCommunicationState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r PeripheralsHeartBeatResult (status=OK): \n");
        
        _communicationStateHandlers.Verify(x => x.Invoke(CommunicationState.Okay), Times.Once);
    }
    
    [Fact]
    public void HeartbeatNotFound_TriggersRegistration()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r PeripheralsHeartBeatResult (status=Error)\n");
        
        _communicationStateHandlers.Verify(x => x.Invoke(CommunicationState.Error), Times.Once);
        
        Assert.StartsWith("xCommand Peripherals Connect ID: AV-Coders-RoomOS-Module Type: ControlSystem", (string) _mockClient.Invocations[0].Arguments[0]);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(60)]
    public void VolumeStatusResponse_UpdatesVolumeLevel(int volume)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"*s Audio Volume: {volume}\n");
        
        _outputVolumeLevelHandler.Verify(x => x.Invoke(volume));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(100)]
    public void SetOutputVolume_SendsTheCommand(int volume)
    {
        _codec.SetOutputVolume(volume);
        
        _mockClient.Verify(x=> x.Send($"xCommand Audio Volume Set Level: {volume}\r\n"));
    }

    [Theory]
    [InlineData("Off", MuteState.Off)]
    [InlineData("On", MuteState.On)]
    public void MicMuteStatusResponse_UpdatesMuteState(string response, MuteState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"*s Audio Microphones Mute: {response}\n");
        
        _microphoneMuteStateHandler.Verify(x => x.Invoke(expectedState));
    }

    [Theory]
    [InlineData("Off", MuteState.Off)]
    [InlineData("On", MuteState.On)]
    public void OutputMuteStatusResponse_UpdatesMuteState(string response, MuteState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"*s Audio VolumeMute: {response}\n");
        
        _outputMuteStateHandler.Verify(x => x.Invoke(expectedState));
    }

    [Fact]
    public void PowerOn_SendTheCommand()
    {
        _codec.PowerOn();
        
        _mockClient.Verify(x => x.Send("xCommand Standby Deactivate\r\n"));
    }

    [Fact]
    public void PowerOff_SendTheCommand()
    {
        _codec.PowerOff();
        
        _mockClient.Verify(x => x.Send("xCommand Standby Activate\r\n"));
    }

    [Theory]
    [InlineData("Standby", PowerState.Off)]
    [InlineData("EnteringStandby", PowerState.Off)]
    [InlineData("HalfWake", PowerState.Off)]
    [InlineData("Off", PowerState.On)]
    public void StandbyStatusResponse_UpdatesPowerState(string response, PowerState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"*s Standby State: {response}\n");
        
        _powerStateHandlers.Verify(x => x.Invoke(expectedState));
    }

    [Fact]
    public void CallResponses_HandleDialling()
    {
        new List<string>
        {
            "*s Call 203 AnswerState: Unanswered\n",
            "*s Call 203 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 203 DisplayName: \"The Meeting Room!\"",
            "*s Call 203 Status: Dialling\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Dialling, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("The Meeting Room!", _codec.GetActiveCalls()[0].Name);
        Assert.Equal("sip:*123456@client.uri", _codec.GetActiveCalls()[0].Number);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Dialling));
    }

    [Fact]
    public void CallResponses_HandleDiallingFailed()
    {
        new List<string>
        {
            "*s Call 203 AnswerState: Unanswered\n",
            "*s Call 203 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 203 DisplayName: \"*123456\"",
            "*s Call 203 Status: Dialling\n",
            "*s Call 203 (ghost=True):\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Empty(_codec.GetActiveCalls());
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Dialling));
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Idle));
    }

    [Fact]
    public void CallResponses_HandleConnected()
    {
        new List<string>
        {
            "*s Call 204 AnswerState: Autoanswered\n",
            "*s Call 204 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 204 DisplayName: \"VCAT IR 19-12\"",
            "*s Call 204 Status: Dialling\n",
            "*s Call 204 Status: Connected\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Connected, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("VCAT IR 19-12", _codec.GetActiveCalls()[0].Name);
        Assert.Equal("sip:*123456@client.uri", _codec.GetActiveCalls()[0].Number);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Connected));
        _activeCallHandler.Verify(x => x.Invoke(It.IsAny<List<Call>>()));
    }

    [Fact]
    public void CallResponses_HandleGhost()
    {
        new List<string>
        {
            "*s Call 204 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 204 DisplayName: \"*123456\"",
            "*s Call 204 Status: Connected\n",
            "*s Call 204 (ghost=True):\n"
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Idle), Times.Once);
        Assert.Equal(CallStatus.Idle, _callStatusHandler.Invocations.Last().Arguments[0]);
        Assert.Empty(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Idle, _codec.CallStatus);
    }

    [Fact]
    public void CallResponses_HandleDisconnecting()
    {
        new List<string>
        {
            "*s Call 204 AnswerState: Unanswered\n",
            "*s Call 204 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 204 DisplayName: \"*123456\"",
            "*s Call 204 Status: Connected\n",
            "*s Call 204 Status: Disconnecting\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Disconnecting, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("*123456", _codec.GetActiveCalls()[0].Name);
        Assert.Equal("sip:*123456@client.uri", _codec.GetActiveCalls()[0].Number);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Disconnecting));
    }

    [Fact]
    public void CallResponses_HandleIdle()
    {
        new List<string>
        {
            "*s Call 204 AnswerState: Unanswered\n",
            "*s Call 204 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 204 DisplayName: \"*123456\"",
            "*s Call 204 Status: Disconnecting\n",
            "*s Call 204 Status: Idle\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Empty(_codec.GetActiveCalls());
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Idle));
    }

    [Fact]
    public void CallResponses_HandleRinging()
    {
        new List<string>
        {
            "*s Call 204 AnswerState: Unanswered\n",
            "*s Call 204 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 204 DisplayName: \"*123456\"",
            "*s Call 204 Status: Ringing\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        
        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Ringing, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("*123456", _codec.GetActiveCalls()[0].Name);
        Assert.Equal("sip:*123456@client.uri", _codec.GetActiveCalls()[0].Number);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Ringing));
    }

    [Fact]
    public void CallResponses_HandleConnecting()
    {
        new List<string>
        {
            "*s Call 204 AnswerState: Unanswered\n",
            "*s Call 204 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 204 DisplayName: \"*123456\"",
            "*s Call 204 Status: Connecting\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        
        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Connecting, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("*123456", _codec.GetActiveCalls()[0].Name);
        Assert.Equal("sip:*123456@client.uri", _codec.GetActiveCalls()[0].Number);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Connecting));
    }

    [Fact]
    public void CallResponses_HandleHangupRequestResponse()
    {
        new List<string>
        {
            "*r CallDisconnectResult (status=OK): \n"
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Idle));
    }

    [Fact]
    public void RegistrationUri_IsStored()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*s SIP Registration 1 URI: \"300300@client.domain\"\n");
        
        Assert.Equal("300300@client.domain", _codec.GetUri());
    }

    [Theory]
    [InlineData(MuteState.Off, "xCommand Audio Volume Unmute\r\n")]
    [InlineData(MuteState.On, "xCommand Audio Volume Mute\r\n")]
    public void SetOutputMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _codec.SetOutputMute(state);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(MuteState.Off, "xCommand Audio Microphones Unmute\r\n")]
    [InlineData(MuteState.On, "xCommand Audio Microphones Mute\r\n")]
    public void SetMicrophoneMute_SendsTheCommand(MuteState state, string expectedCommand)
    {
        _codec.SetMicrophoneMute(state);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(PowerState.Off, "xCommand Conference DoNotDisturb Deactivate\r\n")]
    [InlineData(PowerState.On, "xCommand Conference DoNotDisturb Activate\r\n")]
    public void SetDoNotDisturbState_SendsTheCommand(PowerState state, string expectedCommand)
    {
        _codec.SetDoNotDisturbState(state);
        
        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData("*s Conference DoNotDisturb: Active\n", PowerState.On)]
    [InlineData("*s Conference DoNotDisturb: Inactive\n", PowerState.Off)]
    public void DoNotDisturbResponses_UpdateTheState(string response, PowerState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedState, _codec.DoNotDisturbState);
    }

    [Theory]
    [InlineData("*c xConfiguration Conference AutoAnswer Mode: Off\n", PowerState.Off)]
    [InlineData("*c xConfiguration Conference AutoAnswer Mode: On\n", PowerState.On)]
    public void AutoAnswerResponses_UpdateTheState(string response, PowerState expectedState)
    {
        var mockHandler = new Mock<PowerStateHandler>();
        _codec.AutoAnswerStateHandlers += mockHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedState, _codec.AutoAnswerState);
        mockHandler.Verify(x => x.Invoke(expectedState));
    }
    
}