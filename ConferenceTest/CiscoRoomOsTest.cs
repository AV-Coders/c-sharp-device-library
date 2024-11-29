using AVCoders.Core;
using Moq;

namespace AVCoders.Conference.Tests;

public class CiscoRoomOsTest
{
    
    public abstract class StubbedClient : IpComms
    {
        protected StubbedClient(string host, ushort port) : base(host, port, "StubbedClient"){}

        public override void Send(string message){}

        public override void Send(byte[] bytes){}

        public override void SetPort(ushort port){}

        public override void SetHost(string host){}
        public new ConnectionState GetConnectionState() => ConnectionState.Connected;
    }
    
    private readonly Mock<StubbedClient> _mockClient = new("foo", (ushort)1);
    private readonly Mock<LogHandler> _logHandlers = new ();
    private readonly Mock<CommunicationStateHandler> _communicationStateHandlers = new ();
    private readonly Mock<PowerStateHandler> _powerStateHandlers = new ();
    private readonly Mock<VolumeLevelHandler> _outputVolumeLevelHandler = new ();
    private readonly Mock<MuteStateHandler> _outputMuteStateHandler = new ();
    private readonly Mock<MuteStateHandler> _microphoneMuteStateHandler = new ();
    private readonly Mock<CallStatusHandler> _callStatusHandler = new ();
    
    private readonly CiscoRoomOs _codec;

    public CiscoRoomOsTest()
    {
        _codec = new CiscoRoomOs(_mockClient.Object, new CiscoRoomOsDeviceInfo("Test", "Xunit", "An Awesome Laptop", "012934"));
        _codec.LogHandlers += _logHandlers.Object;
        _codec.CommunicationStateHandlers += _communicationStateHandlers.Object;
        _codec.PowerStateHandlers += _powerStateHandlers.Object;
        _codec.OutputVolume.VolumeLevelHandlers += _outputVolumeLevelHandler.Object;
        _codec.OutputMute.MuteStateHandlers += _outputMuteStateHandler.Object;
        _codec.MicrophoneMute.MuteStateHandlers += _microphoneMuteStateHandler.Object;
        _codec.CallStatusHandlers += _callStatusHandler.Object;
    }

    [Fact]
    public void Module_RegistersAndSubscribes()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        new List<string> {
        "xFeedback register /Status/Standby",
        "xFeedback register /Event/UserInterface/Extensions/Event",
        "xFeedback register /Status/Call",
        "xFeedback register /Status/Audio/Volume",
        "xFeedback register /Status/Audio/Microphones",
        "xFeedback register /Status/Conference/Presentation",
        "xFeedback register /Event/UserInterface/Presentation/ExternalSource",
        "xFeedback register /Status/Conference/DoNotDisturb",
        "xFeedback register /Status/Conference/Call/AuthenticationRequest",
        "xFeedback register /Status/Conference/Call",
        "xFeedback register /Status/Cameras/SpeakerTrack",
        "xFeedback register /Status/Video/Selfview",
        "xFeedback register /Status/Video/Layout/CurrentLayouts/ActiveLayout",
        "xFeedback register /Event/Bookings",
        "xStatus Standby",
        "xStatus Call",
        "xStatus Audio Volume",
        "xStatus Audio Microphones",
        "xStatus Conference DoNotDisturb",
        "xStatus Conference Presentation",
        "xStatus Cameras SpeakerTrack status",
        "xStatus Video Selfview",
        "xStatus Video Layout CurrentLayouts ActiveLayout",
        "xStatus SIP Registration URI",
        }.ForEach(s => 
            _mockClient.Verify(x => x.Send($"{s}\r\n")));

        Assert.StartsWith("xCommand Peripherals Connect ID: AV-Coders-RoomOS-Module-", (string) _mockClient.Invocations[0].Arguments[0]);
    }

    [Fact]
    public void HeartbeatOkayUpdatesCommunicationState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r PeripheralsHeartBeatResult (status=OK): \n");
        
        _communicationStateHandlers.Verify(x => x.Invoke(CommunicationState.Okay), Times.Once);
    }

    [Theory]
    [InlineData(50)]
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
            "*s Call 203 DisplayName: \"*123456\"",
            "*s Call 203 Status: Dialling\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Dialling, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("*123456", _codec.GetActiveCalls()[0].Name);
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
            "*s Call 204 DisplayName: \"*123456\"",
            "*s Call 204 Status: Dialling\n",
            "*s Call 204 Status: Connected\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Connected, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("*123456", _codec.GetActiveCalls()[0].Name);
        Assert.Equal("sip:*123456@client.uri", _codec.GetActiveCalls()[0].Number);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Connected));
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
    
}