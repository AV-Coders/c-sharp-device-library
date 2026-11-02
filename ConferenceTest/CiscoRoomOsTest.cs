using System.Diagnostics;
using System.Reflection;
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
    private const int RegisteredCallStatusPaths = 5;
    private readonly List<string> _callDisconnectEvent = new()
    {
        "*e CallDisconnect CauseValue: 1",
        "*e CallDisconnect CauseType: \"LocalDisconnect\"",
        "*e CallDisconnect CauseString: \"\"",
        "*e CallDisconnect OrigCallDirection: \"outgoing\"",
        "*e CallDisconnect RemoteURI: \"sip:+61300000000@example.com\"",
        "*e CallDisconnect DisplayName: \"Test Call\"",
        "*e CallDisconnect CallId: 139",
        "*e CallDisconnect CauseCode: 0",
        "*e CallDisconnect CauseOrigin: Internal",
        "*e CallDisconnect Protocol: Sip",
        "*e CallDisconnect Duration: 7",
        "*e CallDisconnect CallType: Video",
        "*e CallDisconnect CallRate: 6000",
        "*e CallDisconnect Encryption: \"Auto\"",
        "*e CallDisconnect RequestedURI: \"SIP:+61300000000@example.com\"",
        "*e CallDisconnect PeopleCountAverage: -1",
        "** end"
    };

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
            "xFeedback register /Event/CallDisconnect",
            "xFeedback register /Status/Audio/Volume",
            "xFeedback register /Status/Audio/VolumeMute",
            "xFeedback register /Status/Audio/Microphones/Mute",
            "xFeedback Register Configuration/Conference/AutoAnswer/Mode",
            "xStatus Standby",
            "xStatus Call",
            "xStatus Audio Volume",
            "xStatus Audio VolumeMute",
            "xStatus Audio Microphones Mute",
            "xStatus SIP Registration | resultId=\"sip-registration\"",
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

    [Fact]
    public void VolumeStatusResponse_UpdatesVolumeLevel()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*s Audio Volume: 60\n");

        _outputVolumeLevelHandler.Verify(x => x.Invoke(60));
    }

    [Fact]
    public void SetOutputVolume_SendsTheCommand()
    {
        _codec.SetOutputVolume(60);

        _mockClient.Verify(x => x.Send("xCommand Audio Volume Set Level: 60\r\n"));
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

    [Theory]
    [InlineData("Dialling", CallStatus.Dialling)]
    [InlineData("Ringing", CallStatus.Ringing)]
    [InlineData("Connecting", CallStatus.Connecting)]
    [InlineData("Disconnecting", CallStatus.Disconnecting)]
    public void CallResponses_TrackTheCallStatus(string status, CallStatus expected)
    {
        new List<string>
        {
            "*s Call 203 AnswerState: Unanswered\n",
            "*s Call 203 CallbackNumber: \"sip:*123456@client.uri\"\n",
            "*s Call 203 DisplayName: \"The Meeting Room!\"",
            $"*s Call 203 Status: {status}\n"
        }.ForEach(command => _mockClient.Object.ResponseHandlers!.Invoke(command));

        var call = Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(expected, call.Status);
        Assert.Equal("The Meeting Room!", call.Name);
        Assert.Equal("sip:*123456@client.uri", call.Number);
        _callStatusHandler.Verify(x => x.Invoke(expected));
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
    public void CallResponses_HangupRequestResponse_KeepsTheCalls_AndRequestsTheCallStatus()
    {
        Receive(new List<string> { "*s Call 139 Status: Connected", "** end", "*s Call 7 Status: Connected", "** end" }, "\n");
        ClearCallHandlerInvocations();

        _codec.HangUp(139);
        Receive(new List<string> { "*r CallDisconnectResult (status=OK): ", "** end" }, "\n");

        Assert.Equal(2, _codec.GetActiveCalls().Count);
        Assert.Equal(CallStatus.Connected, _codec.CallStatus);
        _mockClient.Verify(x => x.Send("xStatus Call\r\n"));
        _callStatusHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public void HangUp_OneOfTwoCalls_EndsOnlyThatCall()
    {
        Receive(new List<string> { "*s Call 139 Status: Connected", "** end", "*s Call 7 Status: Connected", "** end" }, "\n");
        ClearCallHandlerInvocations();

        _codec.HangUp(139);
        Receive(new List<string> { "*r CallDisconnectResult (status=OK): ", "** end" }, "\n");
        Receive(_callDisconnectEvent, "\n");

        _mockClient.Verify(x => x.Send("xCommand Call Disconnect CallId: 139\r\n"));
        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(7, _codec.FindCallId(_codec.GetActiveCalls()[0]));
        Assert.Equal(CallStatus.Connected, _codec.CallStatus);
        _callStatusHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public void HangUp_AllCalls_ClearsTheCalls_AndRaisesActiveCallsChanged()
    {
        Receive(new List<string> { "*s Call 139 Status: Connected", "** end" }, "\n");
        ClearCallHandlerInvocations();

        _codec.HangUp();

        _mockClient.Verify(x => x.Send("xCommand Call Disconnect\r\n"));
        Assert.Empty(_codec.GetActiveCalls());
        VerifyCallEndedOnce();
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

    [Fact]
    public void SetDoNotDisturbState_DoesNotForceTheOldState_WhenFeedbackArrivesDuringHandlers()
    {
        _codec.SetDoNotDisturbState(PowerState.On);
        _codec.DoNotDisturbStateHandlers += _ =>
            _mockClient.Object.ResponseHandlers!.Invoke("*s Conference DoNotDisturb: Inactive\n");

        _codec.SetDoNotDisturbState(PowerState.Off);

        _mockClient.Verify(x => x.Send("xCommand Conference DoNotDisturb Activate\r\n"), Times.Once);
        Assert.Equal(PowerState.Off, _codec.DoNotDisturbState);
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

    private static List<string> GhostLines(int callId) => Enumerable.Range(0, RegisteredCallStatusPaths)
        .SelectMany(_ => new List<string> { $"*s Call {callId} (ghost=True):", "** end" })
        .ToList();

    private void Receive(List<string> lines, string lineEnding) =>
        lines.ForEach(line => _mockClient.Object.ResponseHandlers!.Invoke(line + lineEnding));

    private void ClearCallHandlerInvocations()
    {
        _callStatusHandler.Invocations.Clear();
        _activeCallHandler.Invocations.Clear();
    }

    private void VerifyCallEndedOnce()
    {
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Idle), Times.Once);
        _callStatusHandler.VerifyNoOtherCalls();
        _activeCallHandler.Verify(x => x.Invoke(It.Is<List<Call>>(calls => calls.Count == 0)), Times.Once);
        _activeCallHandler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallResponses_HandleGhostsWithoutIdle(string lineEnding)
    {
        Receive(new List<string> { "*s Call 102 Status: Connected", "** end" }, lineEnding);
        Receive(GhostLines(102), lineEnding);

        Assert.Empty(_codec.GetActiveCalls());
        Assert.Equal(CallStatus.Idle, _codec.CallStatus);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Connected), Times.Once);
        _callStatusHandler.Verify(x => x.Invoke(CallStatus.Idle), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallResponses_GhostsForAnUnknownCall_RaiseNothing(string lineEnding)
    {
        Receive(GhostLines(102), lineEnding);

        _callStatusHandler.VerifyNoOtherCalls();
        _activeCallHandler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallResponses_SecondCallStaysConnected_WhenTheFirstGhosts(string lineEnding)
    {
        Receive(new List<string> { "*s Call 102 Status: Connected", "** end", "*s Call 103 Status: Connected", "** end" }, lineEnding);
        ClearCallHandlerInvocations();

        Receive(GhostLines(102), lineEnding);

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(103, _codec.FindCallId(_codec.GetActiveCalls()[0]));
        Assert.Equal(CallStatus.Connected, _codec.CallStatus);
        _callStatusHandler.VerifyNoOtherCalls();
        _activeCallHandler.Verify(x => x.Invoke(It.Is<List<Call>>(calls => calls.Count == 1)), Times.Once);
        _activeCallHandler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallDisconnectEvent_EndsOnlyTheCallInCallId(string lineEnding)
    {
        Receive(new List<string> { "*s Call 139 Status: Connected", "** end", "*s Call 7 Status: Connected", "** end" }, lineEnding);

        Receive(_callDisconnectEvent, lineEnding);

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(7, _codec.FindCallId(_codec.GetActiveCalls()[0]));
        Assert.Equal(CallStatus.Connected, _codec.GetActiveCalls()[0].Status);
        Assert.Equal(CallStatus.Connected, _codec.CallStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallDisconnectEvent_EndsTheCall_WithoutGhosts(string lineEnding)
    {
        Receive(new List<string> { "*s Call 139 Status: Connected", "** end" }, lineEnding);
        ClearCallHandlerInvocations();

        Receive(_callDisconnectEvent, lineEnding);

        Assert.Empty(_codec.GetActiveCalls());
        VerifyCallEndedOnce();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallResponses_GhostsThenCallDisconnectEvent_EndTheCallOnce(string lineEnding)
    {
        Receive(new List<string> { "*s Call 139 Status: Connected", "** end" }, lineEnding);
        ClearCallHandlerInvocations();

        Receive(GhostLines(139), lineEnding);
        Receive(_callDisconnectEvent, lineEnding);

        Assert.Empty(_codec.GetActiveCalls());
        VerifyCallEndedOnce();
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallResponses_CallDisconnectEventThenGhosts_EndTheCallOnce(string lineEnding)
    {
        Receive(new List<string> { "*s Call 139 Status: Connected", "** end" }, lineEnding);
        ClearCallHandlerInvocations();

        Receive(_callDisconnectEvent, lineEnding);
        Receive(GhostLines(139), lineEnding);

        Assert.Empty(_codec.GetActiveCalls());
        VerifyCallEndedOnce();
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CallResponses_MultiLineChunks_ParseEveryLine(string lineEnding)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(string.Concat(
            new List<string> { "*s Call 139 Status: Connected", "** end", "*s Call 7 Status: Connected", "** end" }
                .Select(line => line + lineEnding)));

        Assert.Equal(2, _codec.GetActiveCalls().Count);
        Assert.Equal(CallStatus.Connected, _codec.CallStatus);

        _mockClient.Object.ResponseHandlers!.Invoke(string.Concat(
            _callDisconnectEvent.Concat(GhostLines(139)).Select(line => line + lineEnding)));

        Assert.Single(_codec.GetActiveCalls());
        Assert.Equal(7, _codec.FindCallId(_codec.GetActiveCalls()[0]));
        Assert.Equal(CallStatus.Connected, _codec.CallStatus);
    }

    [Fact]
    public void SendCommand_WhenTheClientThrows_LogsTheErrorAndReportsIt()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        _codec.PowerOn();

        Assert.Equal(CommunicationState.Error, _codec.CommunicationState);
        Assert.Single(_codec.Errors);
    }

    [Fact]
    public void ProcessResponse_ABadLine_IsLoggedOnce_AndTheRestOfTheChunkIsProcessed()
    {
        var exception = Record.Exception(() =>
            _mockClient.Object.ResponseHandlers!.Invoke("*s Call abc Status: Connected\n*s Audio Volume: 50\n"));

        Assert.Null(exception);
        Assert.Single(_codec.Errors);
        _outputVolumeLevelHandler.Verify(x => x.Invoke(50));
    }

    [Theory]
    [InlineData("Preserved")]
    [InlineData("connected")]
    [InlineData("1")]
    [InlineData("Dialling,Connected")]
    public void CallResponses_UnknownStatus_KeepsTheCallStatus(string status)
    {
        Receive(new List<string> { "*s Call 204 Status: Connected", "** end" }, "\n");
        ClearCallHandlerInvocations();

        var exception = Record.Exception(() => Receive(new List<string> { $"*s Call 204 Status: {status}" }, "\n"));

        Assert.Null(exception);
        Assert.Empty(_codec.Errors);
        Assert.Equal(CallStatus.Connected, _codec.GetActiveCalls()[0].Status);
        Assert.Equal(CallStatus.Connected, _codec.CallStatus);
        _callStatusHandler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Connected", "Ringing", CallStatus.Ringing)]
    [InlineData("OnHold", "Connected", CallStatus.Connected)]
    [InlineData("Disconnecting", "Dialling", CallStatus.Dialling)]
    public void CallStatus_IsTheHighestPriorityStatusOfTheCalls(string firstStatus, string secondStatus, CallStatus expected)
    {
        Receive(new List<string> { $"*s Call 102 Status: {firstStatus}", "** end", $"*s Call 103 Status: {secondStatus}", "** end" }, "\n");

        Assert.Equal(expected, _codec.CallStatus);
    }

    [Fact]
    public void Responses_WithAFeedbackMarkerAfterTheStart_AreIgnored()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("Echo Standby State: Off *s\n");

        _powerStateHandlers.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("*s Call 204")]
    [InlineData("*s Call 204 Status:")]
    [InlineData("*e CallDisconnect CallId:")]
    [InlineData("*s Audio Volume:")]
    [InlineData("*s Audio VolumeMute:")]
    [InlineData("*s Audio Microphones Mute:")]
    [InlineData("*s Conference DoNotDisturb:")]
    [InlineData("*c xConfiguration Conference AutoAnswer Mode:")]
    [InlineData("*s SIP Registration 1 URI:")]
    [InlineData("*r Login")]
    [InlineData("*s Standby State:")]
    public void ShortResponses_AreIgnored(string response)
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Null(exception);
        Assert.Empty(_codec.Errors);
        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
        _powerStateHandlers.VerifyNoOtherCalls();
        _outputVolumeLevelHandler.VerifyNoOtherCalls();
        _outputMuteStateHandler.VerifyNoOtherCalls();
        _microphoneMuteStateHandler.VerifyNoOtherCalls();
        _callStatusHandler.VerifyNoOtherCalls();
        _activeCallHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public void OutputFader_LevelUp_StopsAtTheMaximum()
    {
        var fader = new CiscoRoomOsOutputFader("Speakers", _codec, VolumeType.Speaker);
        _mockClient.Object.ResponseHandlers!.Invoke("*s Audio Volume: 98\n");

        fader.LevelUp(5);

        _mockClient.Verify(x => x.Send("xCommand Audio Volume Set Level: 100\r\n"));
    }

    [Fact]
    public void OutputFader_LevelDown_StopsAtTheMinimum()
    {
        var fader = new CiscoRoomOsOutputFader("Speakers", _codec, VolumeType.Speaker);
        _mockClient.Object.ResponseHandlers!.Invoke("*s Audio Volume: 3\n");

        fader.LevelDown(5);

        _mockClient.Verify(x => x.Send("xCommand Audio Volume Set Level: 0\r\n"));
    }

    [Fact]
    public void Login_RegistersForHealthFeedback_AndReadsTheStatus()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");

        new List<string>
        {
            "xFeedback register /Status/SystemUnit/Hardware/Monitoring/Temperature/Status",
            "xFeedback register /Status/Diagnostics",
            "xFeedback register /Status/SIP/Registration",
            "xFeedback register /Status/Webex/Status",
            "xFeedback register /Status/Peripherals/ConnectedDevice",
            "xStatus SystemUnit ProductId | resultId=\"product-id\"",
            "xStatus SystemUnit Software Version | resultId=\"software-version\"",
            "xStatus SystemUnit Software DisplayName | resultId=\"software-display-name\"",
            "xStatus SystemUnit Hardware Module SerialNumber | resultId=\"serial-number\"",
            "xStatus SystemUnit Uptime | resultId=\"uptime\"",
            "xStatus Network 1 Ethernet MacAddress | resultId=\"mac-address\"",
            "xStatus Network 1 Ethernet Speed | resultId=\"ethernet-speed\"",
            "xStatus Network 1 IPv4 Address | resultId=\"ip-address\"",
            "xStatus Network 1 IPv4 SubnetMask | resultId=\"subnet-mask\"",
            "xStatus Network 1 IPv4 Gateway | resultId=\"gateway\"",
            "xStatus SystemUnit Hardware Monitoring Temperature Status | resultId=\"temperature\"",
            "xStatus Diagnostics | resultId=\"diagnostics\"",
            "xStatus SIP Registration | resultId=\"sip-registration\"",
            "xStatus Webex Status | resultId=\"webex\"",
            "xStatus Peripherals ConnectedDevice | resultId=\"peripherals\"",
        }.ForEach(s => _mockClient.Verify(x => x.Send($"{s}\r\n")));
    }

    [Theory]
    [InlineData("product-id", "*s SystemUnit ProductId: \"Cisco Room Kit EQ\"", "Model", "Cisco Room Kit EQ")]
    [InlineData("mac-address", "*s Network 1 Ethernet MacAddress: \"00:50:60:A4:3E:91\"", "MAC Address", "00:50:60:A4:3E:91")]
    public void IdentityReply_IsShownAsADetail(string read, string line, string label, string expected)
    {
        ReceiveReply(read, line);

        Assert.Equal(new DeviceDetail(label, expected), Detail(label));
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("*s SystemUnit Hardware Module SerialNumber: \"\"", "\"\"")]
    [InlineData("*s SystemUnit Hardware Module SerialNumber: FOC\"24", "FOC\"24")]
    public void IdentityReply_Garbage_IsShownExactlyAsReceived(string line, string expected)
    {
        ReceiveReply("serial-number", line);

        Assert.Equal(expected, Detail("Serial")!.Value);
    }

    [Fact]
    public void Uptime_IsShownReadable()
    {
        ReceiveReply("uptime", "*s SystemUnit Uptime: 597095");

        Assert.Equal(new DeviceDetail("Uptime", "6 d 21 h 51 min"), Detail("Uptime"));
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Fact]
    public void Uptime_NotANumberThreeTimesInARow_ShowsTheRawValue_AndRaisesAnIssue_UntilANumberArrives()
    {
        ReceiveReply("uptime", "*s SystemUnit Uptime: 59x7095");
        ReceiveReply("uptime", "*s SystemUnit Uptime: 59x7095");

        Assert.Equal(new DeviceDetail("Uptime", "59x7095", DetailTone.Warning), Detail("Uptime"));
        Assert.Empty(_codec.GetIssues());

        ReceiveReply("uptime", "*s SystemUnit Uptime: 59x7095");

        var issue = Assert.Single(_codec.GetOngoingIssues(), i => i.Key == "uptime-read");
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Contains("*s SystemUnit Uptime: 59x7095", issue.Message);

        ReceiveReply("uptime", "*s SystemUnit Uptime: 597095");

        Assert.Empty(_codec.GetOngoingIssues());
        Assert.Equal(new DeviceDetail("Uptime", "6 d 21 h 51 min"), Detail("Uptime"));
    }

    [Fact]
    public void Uptime_IsReadEveryPoll()
    {
        _mockClient.Invocations.Clear();

        InvokePoll();

        _mockClient.Verify(x => x.Send("xStatus SystemUnit Uptime | resultId=\"uptime\"\r\n"), Times.Once);
    }

    [Fact]
    public void Uptime_Rejected_IsNotReadAgainUntilTheNextLogin()
    {
        ReceiveReply("uptime", RejectedReply("SystemUnit/Uptime"));
        _mockClient.Invocations.Clear();

        InvokePoll();
        _mockClient.Verify(x => x.Send("xStatus SystemUnit Uptime | resultId=\"uptime\"\r\n"), Times.Never);

        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        _mockClient.Verify(x => x.Send("xStatus SystemUnit Uptime | resultId=\"uptime\"\r\n"), Times.Once);
    }

    [Theory]
    [InlineData("serial-number", "Status/SystemUnit/Hardware/Module/SerialNumber", "Serial")]
    [InlineData("webex", "Status/Webex/Status", "Webex")]
    [InlineData("diagnostics", "Status/Diagnostics", "Diagnostics")]
    public void RejectedRead_ShowsItIsNotSupportedWithTheRawReply_AndNoIssue(string read, string xPath, string label)
    {
        var communicationState = _codec.CommunicationState;

        ReceiveReply(read, RejectedReply(xPath));

        var reply = string.Join(" | ", RejectedReply(xPath).Append($"** resultId: \"{read}\""));
        Assert.Equal(new DeviceDetail(label, $"Not supported: {reply}", DetailTone.Normal), Detail(label));
        Assert.Empty(_codec.GetIssues());
        Assert.Equal(communicationState, _codec.CommunicationState);
    }

    [Theory]
    [InlineData(new string[0], "** resultId: \"serial-number\"")]
    [InlineData(new[] { "*s SystemUnit Hardware Module SerialNumber:" }, "*s SystemUnit Hardware Module SerialNumber:")]
    public void EmptyReply_ShowsTheRawReplyWithNoIssue(string[] lines, string expected)
    {
        ReceiveReply("serial-number", lines);

        Assert.Equal(new DeviceDetail("Serial", expected), Detail("Serial"));
        Assert.Empty(_codec.GetIssues());
    }

    [Fact]
    public void TaggedReply_TheDriverNeverAskedFor_IsIgnored()
    {
        var exception = Record.Exception(() => ReceiveReply("someone-else", "*s SystemUnit ProductId: \"Cisco Room Kit EQ\""));

        Assert.Null(exception);
        Assert.Empty(_codec.Errors);
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("Normal", DetailTone.Normal)]
    [InlineData("Unknown", DetailTone.Warning)]
    public void Temperature_WithoutAnAlarm_RaisesNoIssue(string status, DetailTone tone)
    {
        ReceiveReply("temperature", $"*s SystemUnit Hardware Monitoring Temperature Status: {status}");

        Assert.Equal(new DeviceDetail("Temperature", status, tone), Detail("Temperature"));
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("High", DetailTone.Warning, IssueSeverity.Minor)]
    [InlineData("Critical", DetailTone.Error, IssueSeverity.Critical)]
    public void Temperature_Alarm_RaisesAnIssue_UntilItIsNormal(string status, DetailTone tone, IssueSeverity severity)
    {
        Receive(new List<string> { $"*s SystemUnit Hardware Monitoring Temperature Status: {status}", "** end" }, "\n");

        Assert.Equal(new DeviceDetail("Temperature", status, tone), Detail("Temperature"));
        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("temperature", issue.Key);
        Assert.Equal(severity, issue.Severity);

        Receive(new List<string> { "*s SystemUnit Hardware Monitoring Temperature Status: Normal", "** end" }, "\n");

        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("temperature", "*s SystemUnit Hardware Monitoring Temperature Status: Toasty", "Temperature", "Toasty")]
    [InlineData("webex", "*s Webex Status: Confused", "Webex", "Confused")]
    public void UnrecognisedStatus_IsShownRaw_WithNoIssue(string read, string line, string label, string expected)
    {
        ReceiveReply(read, line);

        Assert.Equal(new DeviceDetail(label, expected), Detail(label));
        Assert.Empty(_codec.GetIssues());
    }

    [Theory]
    [InlineData("Critical", IssueSeverity.Critical)]
    [InlineData("Error", IssueSeverity.Major)]
    [InlineData("Warning", IssueSeverity.Minor)]
    [InlineData("Notice", IssueSeverity.Minor)]
    public void DiagnosticsMessage_RaisesAnIssueKeyedByItsType(string level, IssueSeverity severity)
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, level, "IPv4Assignment", "IP configuration incomplete", "delay=190"));

        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("diagnostic:IPv4Assignment", issue.Key);
        Assert.Equal(severity, issue.Severity);
        Assert.Equal($"{level} IPv4Assignment: IP configuration incomplete (delay=190)", issue.Message);
        Assert.Null(Detail("Diagnostics (info)"));
    }

    [Fact]
    public void InfoDiagnosticsMessages_AreShownAsADetail_WithoutAnIssue()
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Info", "SoftwareUpgradeAvailability", "A new software version is available", "")
            .Concat(DiagnosticsMessageLines(2, "Info", "DeveloperPreview", "Developer preview is enabled", "")).ToArray());

        Assert.Empty(_codec.GetOngoingIssues());
        Assert.Equal(new DeviceDetail("Diagnostics (info)",
                "SoftwareUpgradeAvailability: A new software version is available; DeveloperPreview: Developer preview is enabled"),
            Detail("Diagnostics (info)"));
    }

    [Fact]
    public void InfoDiagnosticsDetail_IsRemoved_WhenTheLastInfoMessageGhosts()
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Info", "SoftwareUpgradeAvailability", "A new software version is available", ""));

        Receive(new List<string> { "*s Diagnostics Message 1 (ghost=True):", "** end" }, "\n");

        Assert.Null(Detail("Diagnostics (info)"));
    }

    [Fact]
    public void DiagnosticsListing_AfterLogin_RemovesTheInfoDetail_WhenNoInfoMessagesAreLeft()
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Info", "SoftwareUpgradeAvailability", "A new software version is available", ""));

        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        ReceiveReply("diagnostics");

        Assert.Null(Detail("Diagnostics (info)"));
    }

    [Fact]
    public void DiagnosticsMessage_Ghost_ResolvesItsIssue()
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Warning", "NTPStatus", "NTP server not reachable", "")
            .Concat(DiagnosticsMessageLines(2, "Error", "CameraStatus", "Camera 1 is not connected", "")).ToArray());

        Receive(new List<string> { "*s Diagnostics Message 2 (ghost=True):", "** end" }, "\n");

        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("diagnostic:NTPStatus", issue.Key);
        Assert.Equal("Warning NTPStatus: NTP server not reachable", issue.Message);
    }

    [Fact]
    public void DiagnosticsMessages_WithTheSameType_ShareOneIssue_AtTheHighestSeverity()
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Warning", "CameraStatus", "Camera 1 is overheating", "")
            .Concat(DiagnosticsMessageLines(2, "Critical", "CameraStatus", "Camera 2 is not connected", "")).ToArray());

        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("diagnostic:CameraStatus", issue.Key);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        Assert.Equal("Warning CameraStatus: Camera 1 is overheating; Critical CameraStatus: Camera 2 is not connected", issue.Message);
    }

    [Fact]
    public void DiagnosticsListing_AfterLogin_ResolvesClearedMessages_AndKeepsTheRest()
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Warning", "NTPStatus", "NTP server not reachable", "")
            .Concat(DiagnosticsMessageLines(2, "Error", "CameraStatus", "Camera 1 is not connected", "")).ToArray());
        var cameraIssue = Assert.Single(_codec.GetOngoingIssues(), i => i.Key == "diagnostic:CameraStatus");

        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Error", "CameraStatus", "Camera 1 is not connected", ""));

        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal(cameraIssue.Id, issue.Id);
    }

    [Fact]
    public void EmptyDiagnosticsListing_ResolvesEveryDiagnosticsIssue()
    {
        ReceiveReply("diagnostics", DiagnosticsMessageLines(1, "Warning", "NTPStatus", "NTP server not reachable", ""));

        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        ReceiveReply("diagnostics");

        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("Registered", DetailTone.Normal)]
    [InlineData("Inactive", DetailTone.Normal)]
    [InlineData("Registering", DetailTone.Warning)]
    [InlineData("Deregister", DetailTone.Warning)]
    public void SipRegistration_WithoutAFailure_RaisesNoIssue(string status, DetailTone tone)
    {
        ReceiveReply("sip-registration", SipRegistrationLines("\"\"", status));

        Assert.Equal(new DeviceDetail("SIP Registration 1", status, tone), Detail("SIP Registration 1"));
        Assert.Empty(_codec.GetOngoingIssues());
        Assert.Equal("firstname.lastname@company.com", _codec.GetUri());
    }

    [Fact]
    public void SipRegistration_Failed_RaisesAnIssueWithTheReason_UntilRegistered()
    {
        ReceiveReply("sip-registration", SipRegistrationLines("\"404 Not Found\"", "Failed"));

        Assert.Equal(new DeviceDetail("SIP Registration 1", "Failed: 404 Not Found", DetailTone.Error), Detail("SIP Registration 1"));
        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("sip-registration:1", issue.Key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Equal("SIP registration 1 failed: 404 Not Found", issue.Message);

        Receive(new List<string> { "*s SIP Registration 1 Reason: \"\"", "*s SIP Registration 1 Status: Registered", "** end" }, "\n");

        Assert.Empty(_codec.GetOngoingIssues());
        Assert.Equal(new DeviceDetail("SIP Registration 1", "Registered"), Detail("SIP Registration 1"));
    }

    [Fact]
    public void SipRegistrationListing_AfterLogin_RemovesUnlistedRegistrations()
    {
        ReceiveReply("sip-registration", SipRegistrationLines("\"404 Not Found\"", "Failed"));

        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        ReceiveReply("sip-registration");

        Assert.Null(Detail("SIP Registration 1"));
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Fact]
    public void SipRegistration_UnrecognisedStatus_IsShownRaw_AndRaisesAnIssue()
    {
        ReceiveReply("sip-registration", SipRegistrationLines("\"\"", "Confused"));

        Assert.Equal(new DeviceDetail("SIP Registration 1", "Confused", DetailTone.Warning), Detail("SIP Registration 1"));
        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("sip-registration:1", issue.Key);
        Assert.Contains("Confused", issue.Message);
    }

    [Theory]
    [InlineData("Registered", DetailTone.Normal)]
    [InlineData("Disabled", DetailTone.Normal)]
    [InlineData("Registering", DetailTone.Warning)]
    [InlineData("Stopped", DetailTone.Warning)]
    public void WebexStatus_WithoutAnError_RaisesNoIssue(string status, DetailTone tone)
    {
        ReceiveReply("webex", $"*s Webex Status: {status}");

        Assert.Equal(new DeviceDetail("Webex", status, tone), Detail("Webex"));
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("Registered")]
    [InlineData("Disabled")]
    [InlineData("Stopped")]
    public void WebexStatus_Error_RaisesAnIssue_UntilRegisteredDisabledOrStopped(string status)
    {
        Receive(new List<string> { "*s Webex Status: Error", "** end" }, "\n");

        Assert.Equal(new DeviceDetail("Webex", "Error", DetailTone.Error), Detail("Webex"));
        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("webex", issue.Key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);

        Receive(new List<string> { $"*s Webex Status: {status}", "** end" }, "\n");

        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Fact]
    public void ConnectedDevice_IsShownAsADetail()
    {
        ReceiveReply("peripherals", ConnectedDeviceLines(1001, "Connected"));

        Assert.Equal(new DeviceDetail("Peripheral 1001", "Cisco Room Navigator, TouchPanel, Connected, software TI7.2.0, serial ABC123"),
            Detail("Peripheral 1001"));
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("LostConnection")]
    [InlineData("ResponseTimedOut")]
    public void ConnectedDevice_Lost_RaisesAnIssue_UntilConnected(string status)
    {
        ReceiveReply("peripherals", ConnectedDeviceLines(1001, "Connected"));

        Receive(new List<string> { $"*s Peripherals ConnectedDevice 1001 Status: {status}", "** end" }, "\n");

        Assert.Equal(DetailTone.Error, Detail("Peripheral 1001")!.Tone);
        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("peripheral:1001", issue.Key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Equal($"Peripheral 1001, Cisco Room Navigator (TouchPanel), is {status}", issue.Message);

        Receive(new List<string> { "*s Peripherals ConnectedDevice 1001 Status: Connected", "** end" }, "\n");

        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Fact]
    public void AnotherControlSystem_Lost_IsANormalDetailWithNoIssue()
    {
        ReceiveReply("peripherals", ConnectedDeviceLines(1003, "LostConnection", "ControlSystem", "CP4-Lobby", "Lobby processor"));

        Receive(new List<string> { "*s Peripherals ConnectedDevice 1003 Status: ResponseTimedOut", "** end" }, "\n");

        Assert.Equal(new DeviceDetail("Peripheral 1003", "Lobby processor, ControlSystem, ResponseTimedOut, software TI7.2.0, serial ABC123", DetailTone.Normal),
            Detail("Peripheral 1003"));
        Assert.Empty(_codec.GetIssues());
    }

    [Theory]
    [InlineData("ControlSystem", "AV-Coders-RoomOS-Module")]
    [InlineData("TouchPanel", "00:10:20:7c:4a:e5")]
    public void ThisDriversOwnEntryOrATouchPanel_LostInTheListing_RaisesAMajorIssue(string type, string peripheralId)
    {
        ReceiveReply("peripherals", ConnectedDeviceLines(1004, "LostConnection", type, peripheralId));

        var issue = Assert.Single(_codec.GetOngoingIssues());
        Assert.Equal("peripheral:1004", issue.Key);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Equal(DetailTone.Error, Detail("Peripheral 1004")!.Tone);
    }

    [Fact]
    public void ConnectedDevice_Ghost_RemovesItsDetail_AndResolvesItsIssue()
    {
        ReceiveReply("peripherals", ConnectedDeviceLines(1001, "LostConnection"));

        Receive(new List<string> { "*s Peripherals ConnectedDevice 1001 (ghost=True):", "** end" }, "\n");

        Assert.Null(Detail("Peripheral 1001"));
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Fact]
    public void PeripheralsListing_AfterLogin_RemovesUnlistedDevices()
    {
        ReceiveReply("peripherals", ConnectedDeviceLines(1001, "LostConnection").Concat(ConnectedDeviceLines(1002, "Connected")).ToArray());
        var detailsBeforeLogin = _codec.Details.Count;

        _mockClient.Object.ResponseHandlers!.Invoke("*r Login successful\n");
        ReceiveReply("peripherals", ConnectedDeviceLines(1002, "Connected"));

        Assert.Null(Detail("Peripheral 1001"));
        Assert.NotNull(Detail("Peripheral 1002"));
        Assert.Equal(detailsBeforeLogin - 1, _codec.Details.Count);
        Assert.Empty(_codec.GetOngoingIssues());
    }

    [Theory]
    [InlineData("*s SystemUnit")]
    [InlineData("*s SystemUnit ProductId")]
    [InlineData("*s Diagnostics Message 1")]
    [InlineData("*s Peripherals ConnectedDevice 1001")]
    [InlineData("*s SIP Registration 1")]
    [InlineData("** resultId:")]
    public void ShortHealthResponses_DoNotThrow(string response)
    {
        var exception = Record.Exception(() => _mockClient.Object.ResponseHandlers!.Invoke($"{response}\n"));

        Assert.Null(exception);
        Assert.Empty(_codec.Errors);
        Assert.Empty(_codec.Details);
    }

    private DeviceDetail? Detail(string label) => _codec.Details.SingleOrDefault(d => d.Label == label);

    private void ReceiveReply(string read, params string[] lines) =>
        Receive(lines.Append($"** resultId: \"{read}\"").Append("** end").ToList(), "\n");

    private void InvokePoll() =>
        ((Task)typeof(CiscoRoomOs).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_codec, [CancellationToken.None])!).Wait();

    private static string[] RejectedReply(string xPath) =>
    [
        "*r Status (status=Error):",
        "*r Status Reason: \"No match on address expression.\"",
        $"*r Status XPath: \"{xPath}\""
    ];

    private static string[] DiagnosticsMessageLines(int instance, string level, string type, string description, string references) =>
    [
        $"*s Diagnostics Message {instance} Description: \"{description}\"",
        $"*s Diagnostics Message {instance} Level: {level}",
        $"*s Diagnostics Message {instance} References: \"{references}\"",
        $"*s Diagnostics Message {instance} Type: {type}"
    ];

    private static string[] SipRegistrationLines(string reason, string status) =>
    [
        "*s SIP Registration 1 Authentication: Off",
        $"*s SIP Registration 1 Reason: {reason}",
        $"*s SIP Registration 1 Status: {status}",
        "*s SIP Registration 1 URI: \"firstname.lastname@company.com\""
    ];

    private static string[] ConnectedDeviceLines(int id, string status, string type = "TouchPanel",
        string peripheralId = "00:10:20:7c:4a:e5", string name = "Cisco Room Navigator") =>
    [
        $"*s Peripherals ConnectedDevice {id} HardwareInfo: \"1122330-0\"",
        $"*s Peripherals ConnectedDevice {id} ID: \"{peripheralId}\"",
        $"*s Peripherals ConnectedDevice {id} Name: \"{name}\"",
        $"*s Peripherals ConnectedDevice {id} NetworkAddress: \"192.0.1.20\"",
        $"*s Peripherals ConnectedDevice {id} SerialNumber: \"ABC123\"",
        $"*s Peripherals ConnectedDevice {id} SoftwareInfo: \"TI7.2.0\"",
        $"*s Peripherals ConnectedDevice {id} Status: {status}",
        $"*s Peripherals ConnectedDevice {id} Type: {type}",
        $"*s Peripherals ConnectedDevice {id} UpgradeStatus: None"
    ];
}

[Collection(LoggerFactoryCollection.Name)]
public class CiscoRoomOsPhonebookLineTest : IDisposable
{
    private const string ProcessResponseSpan = "CiscoRoomOs.ProcessResponse";
    private const string UnhandledFeedback = "Unhandled feedback";
    private const string ReplyEnd = "** end";
    private const string PeripheralsConnectResult = "*r PeripheralsConnectResult (status=OK): ";
    private static readonly string[] PhonebookReply =
    [
        "*r PhonebookSearchResult (status=OK): ",
        "*r PhonebookSearchResult ResultInfo Offset: 0",
        "*r PhonebookSearchResult ResultInfo Limit: 300",
        "*r PhonebookSearchResult ResultInfo TotalRows: 4",
        "*r PhonebookSearchResult Contact 1 Name: \"Test Room One (SIP)\"",
        "*r PhonebookSearchResult Contact 1 ContactId: \"e_10001\"",
        "*r PhonebookSearchResult Contact 1 Type: \"Room\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 Number: \"SIP:+61300000001@example.com\"",
        "*r PhonebookSearchResult Contact 1 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 2 Name: \"Test Room Two (SIP)\"",
        "*r PhonebookSearchResult Contact 2 ContactId: \"e_10002\"",
        "*r PhonebookSearchResult Contact 2 Type: \"Room\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 Number: \"SIP:300001@example.com\"",
        "*r PhonebookSearchResult Contact 2 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 3 Name: \"Test Room Three (SIP)\"",
        "*r PhonebookSearchResult Contact 3 ContactId: \"e_10003\"",
        "*r PhonebookSearchResult Contact 3 Type: \"Room\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 Number: \"SIP:+61300000003@example.com\"",
        "*r PhonebookSearchResult Contact 3 ContactMethod 1 Protocol: SIP",
        "*r PhonebookSearchResult Contact 4 Name: \"Test Room Four - Desk Pro (SIP)\"",
        "*r PhonebookSearchResult Contact 4 ContactId: \"e_10004\"",
        "*r PhonebookSearchResult Contact 4 Type: \"Room\"",
        "*r PhonebookSearchResult Contact 4 ContactMethod 1 ContactMethodId: \"1\"",
        "*r PhonebookSearchResult Contact 4 ContactMethod 1 Number: \"SIP:300004@example.com\"",
        "*r PhonebookSearchResult Contact 4 ContactMethod 1 Protocol: SIP"
    ];

    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly List<string> _received = [];
    private readonly List<Activity> _spans = [];
    private readonly object _spansLock = new();
    private readonly CiscoRoomOs _codec;
    private readonly CiscoRoomOsPhonebookParser _parser;
    private readonly ActivityListener _listener;

    public CiscoRoomOsPhonebookLineTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _codec = new CiscoRoomOs(_mockClient.Object, new CiscoRoomOsDeviceInfo("Test", "Xunit", "An Awesome Laptop", "012934"));
            _parser = new CiscoRoomOsPhonebookParser(_mockClient.Object);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
        _mockClient.Object.ResponseHandlers += _received.Add;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LogBase.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != ProcessResponseSpan || (string?)activity.GetTagItem("InstanceUid") != _codec.InstanceUid)
                    return;
                lock (_spansLock)
                    _spans.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private void Receive(string line) => _mockClient.Object.ResponseHandlers!.Invoke(line);

    private int ProcessResponseSpans()
    {
        lock (_spansLock)
            return _spans.Count;
    }

    [Fact]
    public void PhonebookLines_SkipTheCodecsLineHandling()
    {
        foreach (var line in PhonebookReply)
            Receive(line);

        Assert.Equal(0, ProcessResponseSpans());
        Assert.DoesNotContain(_logs.Logs, log => log.Message.StartsWith(UnhandledFeedback));
        Assert.Equal(PhonebookReply, _received);

        Receive(ReplyEnd);

        Assert.Equal(1, ProcessResponseSpans());
    }

    [Fact]
    public void PhonebookLines_StillBuildThePhonebook()
    {
        foreach (var line in PhonebookReply)
            Receive(line);
        Receive(ReplyEnd);

        var contacts = _parser.PhoneBook.Items.Cast<PhonebookContact>().ToList();
        Assert.Equal(["Test Room One (SIP)", "Test Room Two (SIP)", "Test Room Three (SIP)", "Test Room Four - Desk Pro (SIP)"],
            contacts.Select(contact => contact.Name));
        Assert.Equal(["SIP:+61300000001@example.com", "SIP:300001@example.com", "SIP:+61300000003@example.com", "SIP:300004@example.com"],
            contacts.Select(contact => contact.Numbers.Single().Number));
    }

    [Fact]
    public void OtherResultLines_AreStillLoggedAsUnhandled()
    {
        Receive(PeripheralsConnectResult);

        Assert.Contains(_logs.Logs, log => log.Message == $"{UnhandledFeedback} {PeripheralsConnectResult}");
        Assert.Equal(1, ProcessResponseSpans());
    }
}
