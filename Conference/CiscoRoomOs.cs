using AVCoders.Core;

namespace AVCoders.Conference;

public record CiscoRoomOsDeviceInfo(string Name, string SoftwareInfo, string HardwareInfo, string SerialNumber);

public class CiscoRoomOsOutputFader : VolumeControl
{
  private readonly CiscoRoomOs _codec;
    
  public CiscoRoomOsOutputFader(string name, CiscoRoomOs codec, VolumeType type) : base(name, type)
  {
    _codec = codec;
    _codec.OutputVolume.VolumeLevelHandlers += x => Volume = x;
    _codec.OutputMute.MuteStateHandlers += x => MuteState = x;
    Volume = _codec.OutputVolume.Volume;
    MuteState = _codec.OutputMute.MuteState;
  }

  public override void LevelUp(int amount) => _codec.SetOutputVolume(_codec.OutputVolume.Volume + amount);

  public override void LevelDown(int amount) => _codec.SetOutputVolume(_codec.OutputVolume.Volume - amount);

  public override void SetLevel(int percentage) => _codec.SetOutputVolume(percentage);

  public override void ToggleAudioMute() => _codec.ToggleOutputMute();

  public override void SetAudioMute(MuteState state) => _codec.SetOutputMute(state);
}

public class CiscoRoomOsMicFader : VolumeControl
{
  private readonly CiscoRoomOs _codec;
    
  public CiscoRoomOsMicFader(string name, CiscoRoomOs codec, VolumeType type) : base(name, type)
  {
    _codec = codec;
    _codec.MicrophoneMute.MuteStateHandlers += x => MuteState = x;
    MuteState = _codec.MicrophoneMute.MuteState;
  }

  public override void LevelUp(int amount) => throw new NotImplementedException("Cisco room os mic volume control is not supported");

  public override void LevelDown(int amount) => throw new NotImplementedException("Cisco room os mic volume control is not supported");

  public override void SetLevel(int percentage) => throw new NotImplementedException("Cisco room os mic volume control is not supported");

  public override void ToggleAudioMute() => _codec.ToggleMicrophoneMute();

  public override void SetAudioMute(MuteState state) => _codec.SetMicrophoneMute(state);
}

public enum PeripheralType
{
  TouchPanel,
  ControlSystem,
  Other
}

public class CiscoRoomOs : Conference
  {
    private readonly CiscoRoomOsDeviceInfo _deviceInfo;
    private readonly string _moduleIdentifier;
    private readonly PeripheralType _peripheralType;
    private readonly Dictionary<int, CiscoRoomOsVideoInput> _videoInputs = new();
    private readonly Dictionary<int, CiscoRoomOsVideoOutput> _videoOutputs = new();
    private readonly Dictionary<int, int> _sourceIdToConnectorId = new();
    private bool _forceDoNotDisturb = true;
    private PowerState _doNotDisturbState = PowerState.Unknown;
    private PowerState _desiredDoNotDisturbState = PowerState.Unknown;
    private PowerState _autoAnswerState = PowerState.Unknown;
    public PowerStateHandler? DoNotDisturbStateHandlers;
    public PowerStateHandler? AutoAnswerStateHandlers;
    public StringHandler? OutputVolumeResponseHandlers;
    public event Action<PowerState>? OnDoNotDisturbStateChanged;
    public event Action<PowerState>? OnAutoAnswerStateChanged;

    public PowerState DoNotDisturbState
    {
      get => _doNotDisturbState;
      private set
      {
        if (_doNotDisturbState == value)
          return;
        _doNotDisturbState = value;
        DoNotDisturbStateHandlers?.Invoke(value);
        OnDoNotDisturbStateChanged?.Invoke(value);
      }
    }

    public PowerState AutoAnswerState
    {
      get => _autoAnswerState;
      private set
      {
        if (_autoAnswerState == value)
          return;
        _autoAnswerState = value;
        AutoAnswerStateHandlers?.Invoke(value);
        OnAutoAnswerStateChanged?.Invoke(value);
      }
    }

    public CiscoRoomOs(CommunicationClient communicationClient, CiscoRoomOsDeviceInfo deviceInfo, string instanceId = "", 
      PeripheralType peripheralType = PeripheralType.ControlSystem) : base(communicationClient)
    {
      _moduleIdentifier = $"AV-Coders-RoomOS-Module{instanceId}";

      _deviceInfo = deviceInfo;
      _peripheralType = peripheralType;
      CommunicationClient.ResponseHandlers += HandleResponse;
    }

    private void Reinitialise()
    {
      using (PushProperties("Reinitialise"))
      {
        PollWorker.Stop();
        try
        {
          SendCommand(
            $"xCommand Peripherals Connect ID: {_moduleIdentifier} Type: {_peripheralType.ToString()} Name: \"{_deviceInfo.Name}\" SoftwareInfo: \"{_deviceInfo.SoftwareInfo}\" HardwareInfo: \"{_deviceInfo.HardwareInfo}\" SerialNumber: \"{_deviceInfo.SerialNumber}\"");
          SendCommand("xFeedback register /Status/Standby");
          SendCommand("xFeedback register /Status/Conference/DoNotDisturb");
          SendCommand("xFeedback register /Status/Call");
          SendCommand("xFeedback register /Status/Audio/Volume");
          SendCommand("xFeedback Register Configuration/Conference/AutoAnswer/Mode");
          SendCommand("xFeedback register /Status/Video/Input/Connector");
          SendCommand("xFeedback register /Status/Video/Input/Source");
          SendCommand("xFeedback register /Status/Video/Output/Connector");
          SendCommand("xStatus Standby");
          SendCommand("xStatus Conference DoNotDisturb");
          SendCommand("xStatus Call");
          SendCommand("xStatus Audio Volume");
          SendCommand("xStatus SIP Registration URI");
          SendCommand("xStatus Video Input Connector");
          SendCommand("xStatus Video Input Source");
          SendCommand("xStatus Video Output Connector");
          SendCommand("xConfiguration Conference AutoAnswer Mode");
        }
        catch (Exception e)
        {
          LogException(e, "Can't initialise Cisco Room OS");
        }

        PollWorker.Restart();
      }
    }

    private void SendCommand(string command)
    {
      try
      {
        CommunicationClient.Send(command + "\r\n");
        CommunicationState = CommunicationState.Okay;
      }
      catch (Exception)
      {
        CommunicationState = CommunicationState.Error;
      }
    }

    private Task SendHeartbeat()
    {
      using (PushProperties("SendHeartbeat"))
      {
        SendCommand($"xCommand Peripherals HeartBeat ID: {_moduleIdentifier} Timeout: 120");
        LogVerbose("Sending Heartbeat");
        return Task.CompletedTask;
      }
    }

    private void SendCallCommand(string commandString) => SendCommand($"xCommand Call {commandString}");

    public override void SendDtmf(char number) => SendCallCommand($"DTMFSend DTMFString: {number}");

    public override void Dial(string number) => SendCommand($"xCommand Dial Number: {number}");

    public void Answer(int callId) => SendCallCommand($"Accept CallId: {callId}");

    public void Answer() => SendCallCommand("Accept");

    public void HangUp(int callId = 0)
    {
      SendCallCommand(callId == 0 ? "Disconnect" : $"Disconnect CallId: {callId}");
      if (callId == 0)
      {
        ActiveCalls.Clear();
        CallStatus = CallStatus.Idle;
      }
    }

    protected override void DoPowerOn() => SendCommand("xCommand Standby Deactivate");

    protected override Task Poll(CancellationToken token) => SendHeartbeat();

    public override void HangUp(Call? call)
    {
      HangUp(FindCallId(call));
    }
    
    public int FindCallId(Call? value)
    {
      using (PushProperties("FindCallId"))
      {
        if (value == null)
          return 0;

        foreach (var keyValuePair in ActiveCalls)
        {
          if (keyValuePair.Value.Equals(value))
          {
            return keyValuePair.Key;
          }
        }

        LogError("No call found for {CallId}, terminating all", value);
        return 0;
      }
    }

    protected override void DoPowerOff() => SendCommand("xCommand Standby Activate");

    public void SelfView(bool on) => SendCommand(on ? "xCommand Video SelfView Set Mode: On" : "xCommand Video SelfView Set Mode: Off");

    private void HandleResponse(string response)
    {
      using (PushProperties("HandleResponse"))
      {
        if (!response.Contains("*s") && !response.Contains("*r") && !response.Contains("*c"))
          return;

        var responses = response.Split(' ');
        try
        {
          switch (responses[1])
          {
            case "PeripheralsHeartBeatResult":
              if (response.Contains("status=OK"))
                CommunicationState = CommunicationState.Okay;
              else if (response.Contains("status=Error"))
              {
                CommunicationState = CommunicationState.Error;
                Reinitialise();
              }
              return;
            case "CallDisconnectResult":
              if (!response.Contains("status=OK"))
                return;
              ActiveCalls.Clear();
              CallStatus = CallStatus.Idle;
              SendCommand("xStatus Call");
              return;
            case "Call":
              ProcessCallResponse(responses);
              RaiseActiveCallsChanged(GetActiveCalls());
              return;
            case "Video":
              ProcessVideoResponse(responses);
              return;
            case "Audio" when responses[2] == "Volume:":
              OutputVolume.SetVolumeFromPercentage(double.Parse(responses[3]));
              return;
            case "Audio" when responses[2] == "VolumeMute:":
              OutputMute.MuteState = responses[3].Contains("On") ? MuteState.On : MuteState.Off;
              return;
            case "Audio" when responses[2] == "Microphones" && responses[3] == "Mute:":
              MicrophoneMute.MuteState = responses[4].Contains("On") ? MuteState.On : MuteState.Off;
              return;
            case "Conference" when responses[2] == "DoNotDisturb:":
              DoNotDisturbState = responses[3].Contains("Active") ? PowerState.On : PowerState.Off;
              ValidateDoNotDisturbState();
              return;
            case "xConfiguration" when responses[2] == "Conference" && responses[3] == "AutoAnswer" && responses[4] == "Mode:":
              AutoAnswerState = responses[5].Contains("On") ? PowerState.On : PowerState.Off;
              return;

            case "SIP" when responses[2] == "Registration" && responses[4] == "URI:":
              Uri = responses[5].Trim().Trim('"');
              return;
            case "Login" when responses[2].Trim()== "successful":
              Reinitialise();
              return;
            case "Standby" when responses[2] == "State:":
              PowerState = responses[3].Contains("Off") ? PowerState.On : PowerState.Off;
              ProcessPowerState();
              return;
          }
        }
        catch (Exception e)
        {
          using (PushProperties("HandleResponse"))
          {
            LogException(e, $"An exception was thrown while processing the string {response}");
            throw;
          }
        }
      }
    }
    
    public void ForceDoNotDisturb(bool forceDoNotDisturb) => _forceDoNotDisturb = forceDoNotDisturb;

    private void ValidateDoNotDisturbState()
    {
      using (PushProperties("ValidateDoNotDisturbState"))
      {
        if (!_forceDoNotDisturb)
          return;
        if (_desiredDoNotDisturbState == PowerState.Unknown)
          return;
        if (DoNotDisturbState == _desiredDoNotDisturbState)
          return;
        LogInformation(
          "The current Do Not Disturb state ({IncorrectDoNotDisturbState}) is not what's expected ({DesiredDoNotDisturbState}), forcing state",
          DoNotDisturbState.ToString(), _desiredDoNotDisturbState.ToString());
        SetDoNotDisturbState(_desiredDoNotDisturbState);
      }
    }

    public CiscoRoomOsVideoInput GetVideoInput(int connectorId)
    {
      if (_videoInputs.TryGetValue(connectorId, out var input))
        return input;
      input = new CiscoRoomOsVideoInput($"{Name} Video Input {connectorId}", connectorId);
      _videoInputs[connectorId] = input;
      return input;
    }

    public CiscoRoomOsVideoOutput GetVideoOutput(int connectorId)
    {
      if (_videoOutputs.TryGetValue(connectorId, out var output))
        return output;
      output = new CiscoRoomOsVideoOutput($"{Name} Video Output {connectorId}", connectorId);
      _videoOutputs[connectorId] = output;
      return output;
    }

    public List<SyncStatus> GetVideoInputs() => _videoInputs.OrderBy(x => x.Key).Select(SyncStatus (x) => x.Value).ToList();

    public List<SyncStatus> GetVideoOutputs() => _videoOutputs.OrderBy(x => x.Key).Select(SyncStatus (x) => x.Value).ToList();

    private void ProcessVideoResponse(string[] responses)
    {
      if (responses.Length < 7)
        return;
      if (!int.TryParse(responses[4], out var number))
        return;
      switch (responses[2])
      {
        case "Input" when responses[3] == "Connector":
          ProcessVideoInputConnectorResponse(GetVideoInput(number), responses);
          return;
        case "Input" when responses[3] == "Source":
          ProcessVideoInputSourceResponse(number, responses);
          return;
        case "Output" when responses[3] == "Connector":
          ProcessVideoOutputConnectorResponse(GetVideoOutput(number), responses);
          return;
      }
    }

    private void ProcessVideoInputConnectorResponse(CiscoRoomOsVideoInput input, string[] responses)
    {
      switch (responses[5])
      {
        case "Connected:":
          if (responses[6].Contains("False"))
            input.SetConnectionState(ConnectionState.Disconnected);
          return;
        case "SignalState:":
          input.SetConnectionState(responses[6].Trim() switch
          {
            "OK" => ConnectionState.Connected,
            "DetectingFormat" => ConnectionState.Connecting,
            "Unstable" => ConnectionState.Degraded,
            "Unsupported" => ConnectionState.Error,
            _ => ConnectionState.Disconnected
          });
          return;
        case "SourceId:":
          if (int.TryParse(responses[6], out var sourceId))
            _sourceIdToConnectorId[sourceId] = input.ConnectorId;
          return;
        case "HDCP" when responses.Length >= 8 && responses[6] == "State:":
          input.SetHdcpStatus(ParseHdcpState(responses[7]));
          return;
      }
    }

    private static HdcpStatus ParseHdcpState(string value) => value.Trim() switch
    {
      "Active" => HdcpStatus.Active,
      "Inactive" => HdcpStatus.Available,
      "Unsupported" => HdcpStatus.NotSupported,
      _ => HdcpStatus.Unknown
    };

    private void ProcessVideoInputSourceResponse(int sourceId, string[] responses)
    {
      switch (responses[5])
      {
        case "ConnectorId:":
          if (int.TryParse(responses[6], out var connectorId))
            _sourceIdToConnectorId[sourceId] = connectorId;
          return;
        case "Resolution" when responses.Length >= 8:
          var connector = GetVideoInput(_sourceIdToConnectorId.GetValueOrDefault(sourceId, sourceId));
          SetResolutionPart(connector, responses[6], responses[7]);
          return;
      }
    }

    private static void ProcessVideoOutputConnectorResponse(CiscoRoomOsVideoOutput output, string[] responses)
    {
      switch (responses[5])
      {
        case "Connected:":
          output.SetConnectionState(responses[6].Contains("True")
            ? ConnectionState.Connected
            : ConnectionState.Disconnected);
          return;
        case "Resolution" when responses.Length >= 8:
          SetResolutionPart(output, responses[6], responses[7]);
          return;
        case "HDCP" when responses.Length >= 8 && responses[6] == "State:":
          output.SetHdcpStatus(ParseHdcpState(responses[7]));
          return;
      }
    }

    private static void SetResolutionPart(CiscoRoomOsVideoConnector connector, string dimension, string value)
    {
      if (!int.TryParse(value.Trim(), out var parsed))
        return;
      switch (dimension)
      {
        case "Height:":
          connector.SetResolutionHeight(parsed);
          return;
        case "Width:":
          connector.SetResolutionWidth(parsed);
          return;
        case "RefreshRate:":
          connector.SetResolutionRefreshRate(parsed);
          return;
      }
    }

    private void ProcessCallResponse(string[] responses)
    {
      int callId = int.Parse(responses[2]);
      if (!ActiveCalls.ContainsKey(callId))
      {
        ActiveCalls[callId] = new Call(CallStatus.Unknown, string.Empty, string.Empty);
      }

      if (responses[3] == "Status:")
      {
        ActiveCalls[callId].Status = Enum.Parse<CallStatus>(responses[4].Trim());
        CallStatusFeedback();
      }
      else if (responses[3] == "DisplayName:")
      {
        var displayName = string.Join(" ", responses.Skip(4)).Trim().Trim('"');
        ActiveCalls[callId].Name = displayName;
      }
      else if (responses[3] == "CallbackNumber:")
      {
        ActiveCalls[callId].Number = responses[4].Trim().Trim('"');
      }
      else if (responses[3].Contains("(ghost=True)"))
      {
        ActiveCalls.Remove(callId);
        CallStatusFeedback();
      }
    }

    private void CallStatusFeedback()
    {
      var priorityOrder = new List<CallStatus>
      {
        CallStatus.Ringing,
        CallStatus.Dialling,
        CallStatus.Connecting,
        CallStatus.Connected,
        CallStatus.OnHold,
        CallStatus.Disconnecting
      };

      var idleCalls = ActiveCalls.Where(x => x.Value.Status == CallStatus.Idle).Select(x => x.Key).ToList();
      idleCalls.ForEach(x => ActiveCalls.Remove(x));

      CallStatus status = ActiveCalls.Values
        .Select(call => call.Status)
        .FirstOrDefault(status => priorityOrder.Contains(status), CallStatus.Idle);

      CallStatus = status;
    }

    public override void SetOutputVolume(int volume)
    {
      SendCommand($"xCommand Audio Volume Set Level: {volume}");
      OutputVolume.SetVolumeFromPercentage(volume);
    }
    
    public override void SetOutputMute(MuteState state)
    {
      SendCommand($"xCommand Audio Volume {(state == MuteState.On ? "Mute": "Unmute")}");
      OutputMute.MuteState = state;
    }

    public override void SetMicrophoneMute(MuteState state)
    {
      SendCommand($"xCommand Audio Microphones {(state == MuteState.On ? "Mute": "Unmute")}");
      MicrophoneMute.MuteState = state;
    }

    public void SetDoNotDisturbState(PowerState state)
    {
      SendCommand($"xCommand Conference DoNotDisturb {(state == PowerState.On ? "Activate": "Deactivate")}");
      DoNotDisturbState = state;
      _desiredDoNotDisturbState = state;
    }
  }