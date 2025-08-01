﻿using AVCoders.Core;

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
    private readonly CommunicationClient _communicationClient;
    private readonly CiscoRoomOsDeviceInfo _deviceInfo;
    public readonly CiscoCE9PhonebookParser PhoneBookParser;
    private readonly string _moduleIdentifier;
    private readonly PeripheralType _peripheralType;
    private PowerState _doNotDisturbState = PowerState.Unknown;
    public PowerStateHandler? DoNotDisturbStateHandlers;
    public StringHandler? OutputVolumeResponseHandlers;

    public PowerState DoNotDisturbState
    {
      get => _doNotDisturbState;
      set
      {
        if (_doNotDisturbState == value)
          return;
        _doNotDisturbState = value;
        DoNotDisturbStateHandlers?.Invoke(value);
      }
    }

    public CiscoRoomOs(CommunicationClient communicationClient, CiscoRoomOsDeviceInfo deviceInfo, PeripheralType peripheralType = PeripheralType.ControlSystem)
    {
      _moduleIdentifier = $"AV-Coders-RoomOS-Module-{DateTime.Now.Ticks:x}";

      _communicationClient = communicationClient;
      _deviceInfo = deviceInfo;
      _peripheralType = peripheralType;
      _communicationClient.ResponseHandlers += HandleResponse;

      PhoneBookParser = new CiscoCE9PhonebookParser();
      PhoneBookParser.Comms += _communicationClient.Send;
    }

    private void Reinitialise()
    {
      PollWorker.Stop();
      try
      {
        SendCommand($"xCommand Peripherals Connect ID: {_moduleIdentifier} Type: {_peripheralType.ToString()} Name: \"{_deviceInfo.Name}\" SoftwareInfo: \"{_deviceInfo.SoftwareInfo}\" HardwareInfo: \"{_deviceInfo.HardwareInfo}\" SerialNumber: \"{_deviceInfo.SerialNumber}\"");
        SendCommand("xFeedback register /Status/Standby");
        SendCommand("xFeedback register /Status/Conference/DoNotDisturb");
        SendCommand("xFeedback register /Status/Call");
        SendCommand("xFeedback register /Status/Audio/Volume");
        SendCommand("xStatus Standby");
        SendCommand("xStatus Conference DoNotDisturb");
        SendCommand("xStatus Call");
        SendCommand("xStatus Audio Volume");
        SendCommand("xStatus SIP Registration URI");
        PhoneBookParser.RequestPhonebook();
      }
      catch (Exception ex)
      {
        Verbose("Can't initialise Cisco Room OS");
        Verbose(ex.Message);
      }
      PollWorker.Restart();
    }

    private void SendCommand(string command)
    {
      try
      {
        _communicationClient.Send(command + "\r\n");
        CommunicationState = CommunicationState.Okay;
      }
      catch (Exception)
      {
        CommunicationState = CommunicationState.Error;
      }
    }

    private Task SendHeartbeat()
    {
      SendCommand($"xCommand Peripherals HeartBeat ID: {_moduleIdentifier} Timeout: 120");
      Verbose("Sending Heartbeat");
      return Task.CompletedTask;
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
      if (value == null)
        return 0;
      
      foreach (var keyValuePair in ActiveCalls)
      {
        if (keyValuePair.Value.Equals(value))
        {
          return keyValuePair.Key;
        }
      }
      Verbose($"No call found for {value}, terminating all");
      return 0;
    }

    protected override void DoPowerOff() => SendCommand("xCommand Standby Activate");

    public void SelfView(bool on) => SendCommand(on ? "xCommand Video SelfView Set Mode: On" : "xCommand Video SelfView Set Mode: Off");

    private void HandleResponse(string response)
    {
      if(!response.Contains("*s") && !response.Contains("*r"))
        return;
      
      var responses = response.Split(' ');
      try
      {
        if (response.Contains("PhonebookSearchResult"))
          CommunicationState = PhoneBookParser.HandlePhonebookSearchResponse(response);
        else if (response.Contains("PeripheralsHeartBeatResult"))
        {
          if(response.Contains("status=OK"))
            CommunicationState = CommunicationState.Okay;
          else if(response.Contains("status=Error"))
            CommunicationState = CommunicationState.Error;


          if (CommunicationState == CommunicationState.Error)
            Reinitialise();
        }
        else if (response.Contains("CallDisconnectResult"))
        {
          if (!response.Contains("status=OK"))
            return;
          ActiveCalls.Clear();
          CallStatus = CallStatus.Idle;
          SendCommand("xStatus Call");
        }
        else if (response.Contains("Call"))
        {
          if (!response.Contains("Conference"))
            ProcessCallResponse(responses);
        }
        else if (response.Contains("Standby State:"))
        {
          PowerState = responses[3].Contains("Off") ? PowerState.On : PowerState.Off;
          ProcessPowerState();
        }
        else if (response.Contains("Audio Volume:"))
        {
          OutputVolume.SetVolumeFromPercentage(double.Parse(responses[3]));
        }
        else if (response.Contains("Audio VolumeMute:"))
        {
          OutputMute.MuteState = responses[3].Contains("On") ? MuteState.On : MuteState.Off;
        }
        else if (response.Contains("Audio Microphones Mute:"))
        {
          MicrophoneMute.MuteState = responses[4].Contains("On") ? MuteState.On : MuteState.Off;
        }
        else if (response.Contains("SIP Registration 1 URI:"))
        {
          Uri = responses[5].Trim().Trim('"');
        }
        else if (response.StartsWith("*r Login successful"))
        {
          Reinitialise();
        }
        else if (response.StartsWith("*s Conference DoNotDisturb: Active"))
        {
          DoNotDisturbState = PowerState.On;
        }
        else if (response.StartsWith("*s Conference DoNotDisturb: Inactive"))
        {
          DoNotDisturbState = PowerState.Off;
        }
      }
      catch (Exception e)
      {
        Error(e.Message);
        Error(e.StackTrace ?? "No stack trace available");
      }
    }

    private void ProcessCallResponse(string[] responses)
    {
      int callId = int.Parse(responses[2]);
      if (!ActiveCalls.ContainsKey(callId))
      {
        ActiveCalls[callId] = new Call(CallStatus.Unknown, String.Empty, String.Empty);
      }

      if (responses[3] == "Status:")
      {
        ActiveCalls[callId].Status = Enum.Parse<CallStatus>(responses[4].Trim());
        CallStatusFeedback();
      }
      else if (responses[3] == "DisplayName:")
      {
        ActiveCalls[callId].Name = responses[4].Trim().Trim('"');
      }
      else if (responses[3] == "CallbackNumber:")
      {
        ActiveCalls[callId].Number = responses[4].Trim().Trim('"');
      }
      else if (responses[3].Contains("(ghost=True)"))
      {
        ActiveCalls[callId].Status = CallStatus.Idle;
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
        CallStatus.Disconnecting
      };

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
    }
  }