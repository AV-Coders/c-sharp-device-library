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
            "xFeedback register /Status/Video/Input/Connector",
            "xFeedback register /Status/Video/Input/Source",
            "xFeedback register /Status/Video/Output/Connector",
            "xStatus Standby",
            "xStatus Call",
            "xStatus SIP Registration URI",
            "xStatus Video Input Connector",
            "xStatus Video Input Source",
            "xStatus Video Output Connector",
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
    public void CallResponses_HandleOnHold()
    {
        new List<string>
        {
            "*s Call 204 AnswerState: Autoanswered\n",
            "*s Call 204 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 204 DisplayName: \"Foo 12\"",
            "*s Call 204 Status: Dialling\n",
            "*s Call 204 Status: OnHold\n"
            
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.OnHold, _codec.GetActiveCalls()[0].Status);
        Assert.Equal("Foo 12", _codec.GetActiveCalls()[0].Name);
        Assert.Equal("sip:*123456@client.uri", _codec.GetActiveCalls()[0].Number);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.OnHold));
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

    [Theory]
    [InlineData("OK", ConnectionState.Connected)]
    [InlineData("Unstable", ConnectionState.Degraded)]
    [InlineData("Unsupported", ConnectionState.Error)]
    [InlineData("Unknown", ConnectionState.Disconnected)]
    [InlineData("NotFound", ConnectionState.Disconnected)]
    [InlineData("DetectingFormat", ConnectionState.Connecting)]
    public void VideoInputSignalStateResponse_UpdatesInputConnectionStatus(string response, ConnectionState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"*s Video Input Connector 1 SignalState: {response}\n");

        Assert.Equal(expectedState, _codec.GetVideoInput(1).InputConnectionStatus);
    }

    [Fact]
    public void VideoInputDisconnectedResponse_UpdatesInputConnectionStatus()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*s Video Input Connector 2 SignalState: OK\n");
        _mockClient.Object.ResponseHandlers!.Invoke("*s Video Input Connector 2 Connected: False\n");

        Assert.Equal(ConnectionState.Disconnected, _codec.GetVideoInput(2).InputConnectionStatus);
    }

    [Fact]
    public void VideoInputSourceResolutionResponses_UpdateInputResolution()
    {
        new List<string>
        {
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Equal("1920x1080@60", _codec.GetVideoInput(1).InputResolution);
    }

    [Fact]
    public void VideoInputSourceResolutionResponses_UseTheConnectorMapping()
    {
        new List<string>
        {
            "*s Video Input Connector 3 SourceId: 2\n",
            "*s Video Input Source 2 Resolution Height: 2160\n",
            "*s Video Input Source 2 Resolution RefreshRate: 30\n",
            "*s Video Input Source 2 Resolution Width: 3840\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Equal("3840x2160@30", _codec.GetVideoInput(3).InputResolution);
        Assert.Equal(string.Empty, _codec.GetVideoInput(2).InputResolution);
    }

    [Fact]
    public void VideoInputDisconnect_ClearsTheResolution()
    {
        new List<string>
        {
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Input Connector 1 Connected: False\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Equal(string.Empty, _codec.GetVideoInput(1).InputResolution);
    }

    [Fact]
    public void VideoInputResponses_NotifySubscribers()
    {
        var handler = new Mock<SyncInfoHandler>();
        _codec.GetVideoInput(1).InputStatusChangedHandlers += handler.Object;

        _mockClient.Object.ResponseHandlers!.Invoke("*s Video Input Connector 1 SignalState: OK\n");

        handler.Verify(x => x.Invoke(ConnectionState.Connected, string.Empty, HdcpStatus.Unknown), Times.Once);
    }

    [Theory]
    [InlineData("True", ConnectionState.Connected)]
    [InlineData("False", ConnectionState.Disconnected)]
    public void VideoOutputConnectedResponse_UpdatesOutputConnectionStatus(string response, ConnectionState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"*s Video Output Connector 1 Connected: {response}\n");

        Assert.Equal(expectedState, _codec.GetVideoOutput(1).OutputConnectionStatus);
    }

    [Fact]
    public void VideoOutputResolutionResponses_UpdateOutputResolution()
    {
        new List<string>
        {
            "*s Video Output Connector 2 Resolution Height: 1080\n",
            "*s Video Output Connector 2 Resolution RefreshRate: 50\n",
            "*s Video Output Connector 2 Resolution Width: 1920\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Equal("1920x1080@50", _codec.GetVideoOutput(2).OutputResolution);
    }

    [Fact]
    public void VideoResponses_PopulateTheConnectorLists()
    {
        new List<string>
        {
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Connector 2 SignalState: Unknown\n",
            "*s Video Output Connector 1 Connected: True\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Equal(2, _codec.GetVideoInputs().Count);
        Assert.Single(_codec.GetVideoOutputs());
        Assert.Equal(AVEndpointType.Encoder, _codec.GetVideoInputs()[0].DeviceType);
        Assert.Equal(AVEndpointType.Decoder, _codec.GetVideoOutputs()[0].DeviceType);
    }

    [Theory]
    [InlineData("Active", HdcpStatus.Active)]
    [InlineData("Inactive", HdcpStatus.Available)]
    [InlineData("Unsupported", HdcpStatus.NotSupported)]
    public void VideoOutputHdcpStateResponse_UpdatesOutputHdcpStatus(string response, HdcpStatus expectedStatus)
    {
        _mockClient.Object.ResponseHandlers!.Invoke($"*s Video Output Connector 1 HDCP State: {response}\n");

        Assert.Equal(expectedStatus, _codec.GetVideoOutput(1).OutputHdcpStatus);
    }

    [Fact]
    public void VideoInputHdcpStateResponse_UpdatesInputHdcpStatus()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*s Video Input Connector 5 HDCP State: Active\n");

        Assert.Equal(HdcpStatus.Active, _codec.GetVideoInput(5).InputHdcpStatus);
    }

    [Fact]
    public void UnrelatedVideoResponses_AreIgnored()
    {
        new List<string>
        {
            "*s Video Input MainVideoSource: 1\n",
            "*s Video Monitors: Single\n",
            "*s Video Input Source 3 (ghost=True)\n",
            "*s Video Input Source 1 Availability: Idle\n",
            "*s Video Input Source 1 FormatStatus: Ok\n",
            "*s Video Input Source 1 MediaChannelId: 118\n",
            "*s Video Output Connector 1 Type: HDMI\n",
            "*s Video Output Connector 1 MonitorRole: First\n",
            "*s Video Output Connector 1 ConnectedDevice CEC 1 DeviceType: \"Unknown\"\n",
            "*s Video Output Connector 1 ConnectedDevice Name: \"Extron HDMI\"\n",
            "*s Video Output Connector 1 ConnectedDevice PreferredFormat: \"1920x1080@60Hz\"\n",
            "*s Video Output Connector 1 ConnectedDevice SupportedFormat Res_1920_1080_60: True\n",
            "*s Video Output Connector 1 HDCP Version: None\n",
            "*s Video Output Connector 1 TouchInput Enabled: False\n",
            "*s Video Input Connector 1 Type: HDMI\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Empty(_codec.GetVideoInputs().FindAll(x => x.InputConnectionStatus != ConnectionState.Unknown));
        Assert.Empty(_codec.GetVideoOutputs().FindAll(x => x.OutputConnectionStatus != ConnectionState.Unknown));
    }

    [Fact]
    public void VideoInputHotplugFeedback_TracksTheConnectionStates()
    {
        var states = new List<ConnectionState>();
        _codec.GetVideoInput(1).InputStatusChangedHandlers += (state, _, _) => states.Add(state);

        new List<string>
        {
            "*s Video Input Connector 1 Connected: False\n",
            "*s Video Input Connector 1 SignalState: NotFound\n",
            "*s Video Input Connector 1 Connected: True\n",
            "*s Video Input Connector 1 SignalState: DetectingFormat\n",
            "*s Video Input Connector 1 SignalState: NotFound\n",
            "*s Video Input Connector 1 SignalState: DetectingFormat\n",
            "*s Video Input Connector 1 SignalState: OK\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Equal(ConnectionState.Connected, _codec.GetVideoInput(1).InputConnectionStatus);
        Assert.Equal(new List<ConnectionState>
        {
            ConnectionState.Disconnected,
            ConnectionState.Connecting,
            ConnectionState.Disconnected,
            ConnectionState.Connecting,
            ConnectionState.Connected
        }, states);
    }

    [Fact]
    public void VideoStatusDump_PopulatesAConnector()
    {
        new List<string>
        {
            "*s Video Input Connector 1 Connected: True\n",
            "*s Video Input Connector 1 SignalState: OK\n",
            "*s Video Input Connector 1 SourceId: 1\n",
            "*s Video Input Connector 1 Type: HDMI\n",
            "*s Video Input Source 1 Availability: Idle\n",
            "*s Video Input Source 1 ConnectorId: 1\n",
            "*s Video Input Source 1 FormatStatus: Ok\n",
            "*s Video Input Source 1 MediaChannelId: 118\n",
            "*s Video Input Source 1 Resolution Height: 1080\n",
            "*s Video Input Source 1 Resolution RefreshRate: 60\n",
            "*s Video Input Source 1 Resolution Width: 1920\n",
            "*s Video Output Connector 3 Connected: False\n",
            "*s Video Output Connector 3 ConnectedDevice PreferredFormat: \"-1x-1@-1Hz\"\n",
            "*s Video Output Connector 3 ConnectedDevice ScreenSize: -1\n",
            "*s Video Output Connector 3 HDCP State: Unsupported\n",
            "*s Video Output Connector 3 Resolution Height: 0\n",
            "*s Video Output Connector 3 Resolution RefreshRate: 0\n",
            "*s Video Output Connector 3 Resolution Width: 0\n"
        }.ForEach(response => _mockClient.Object.ResponseHandlers!.Invoke(response));

        Assert.Equal(ConnectionState.Connected, _codec.GetVideoInput(1).InputConnectionStatus);
        Assert.Equal("1920x1080@60", _codec.GetVideoInput(1).InputResolution);
        Assert.Equal(ConnectionState.Disconnected, _codec.GetVideoOutput(3).OutputConnectionStatus);
        Assert.Equal(string.Empty, _codec.GetVideoOutput(3).OutputResolution);
        Assert.Equal(HdcpStatus.NotSupported, _codec.GetVideoOutput(3).OutputHdcpStatus);
    }

    [Fact]
    public void ActiveCalls_IsEmpty_AfterManyCallsAreDisconnectedExternally()
    {
        for (int i = 1; i <= 50; i++)
        {
            new List<string>
            {
                $"*s Call {i} CallbackNumber: \"sip:user{i}@client.uri\"\n",
                $"*s Call {i} DisplayName: \"User {i}\"",
                $"*s Call {i} Status: Dialling\n",
                $"*s Call {i} Status: Connected\n",
            }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));
        }

        Assert.Equal(50, _codec.GetActiveCalls().Count);

        for (int i = 1; i <= 50; i++)
        {
            new List<string>
            {
                $"*s Call {i} Status: Disconnecting\n",
                $"*s Call {i} (ghost=True):\n",
            }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));
        }

        Assert.Empty(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Idle, _codec.CallStatus);
    }

}