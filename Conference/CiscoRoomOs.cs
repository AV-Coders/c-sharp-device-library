using AVCoders.Core;

namespace AVCoders.Conference;

public record CiscoRoomOsDeviceInfo(string Name, string SoftwareInfo, string HardwareInfo, string SerialNumber);

public class CiscoRoomOs : Conference
  {
    private readonly CommunicationClient _communicationClient;
    private readonly CiscoRoomOsDeviceInfo _deviceInfo;
    public readonly CiscoCE9PhonebookParser PhoneBookParser;
    private readonly string _moduleIdentifier;

    public CiscoRoomOs(CommunicationClient communicationClient, CiscoRoomOsDeviceInfo deviceInfo)
    {
      _moduleIdentifier = $"AV-Coders-RoomOS-Module-{DateTime.Now.Ticks:x}";

      _communicationClient = communicationClient;
      _deviceInfo = deviceInfo;
      _communicationClient.ResponseHandlers += HandleResponse;
      _communicationClient.ConnectionStateHandlers += HandleConnectionState;

      PhoneBookParser = new CiscoCE9PhonebookParser();
      PhoneBookParser.Comms += _communicationClient.Send;
      PhoneBookParser.LogHandlers += (message, level) => Log($"Phonebook - {message}");
      
      HandleConnectionState(_communicationClient.GetConnectionState());
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
      if (connectionState == ConnectionState.Connected)
        InitialiseModule();
      else
        PollWorker.Stop();
    }

    private void InitialiseModule()
    {
      PollWorker.Stop();
      try
      {
        SendCommand($"xCommand Peripherals Connect ID: {_moduleIdentifier} Type: ControlSystem Name: \"{_deviceInfo.Name}\" SoftwareInfo: \"{_deviceInfo.SoftwareInfo}\" HardwareInfo: \"{_deviceInfo.HardwareInfo}\" SerialNumber: \"{_deviceInfo.SerialNumber}\"");
        SendCommand("xFeedback register /Status/Standby");
        SendCommand("xFeedback register /Event/UserInterface/Extensions/Event");
        SendCommand("xFeedback register /Status/Call");
        SendCommand("xFeedback register /Status/Audio/Volume");
        SendCommand("xFeedback register /Status/Audio/Microphones");
        SendCommand("xFeedback register /Status/Conference/Presentation");
        SendCommand("xFeedback register /Event/UserInterface/Presentation/ExternalSource");
        SendCommand("xFeedback register /Status/Conference/DoNotDisturb");
        SendCommand("xFeedback register /Status/Conference/Call/AuthenticationRequest");
        SendCommand("xFeedback register /Status/Conference/Call");
        SendCommand("xFeedback register /Status/Cameras/SpeakerTrack");
        SendCommand("xFeedback register /Status/Video/Selfview");
        SendCommand("xFeedback register /Status/Video/Layout/CurrentLayouts/ActiveLayout");
        SendCommand("xFeedback register /Event/Bookings");
        SendCommand("xStatus Standby");
        SendCommand("xStatus Call");
        SendCommand("xStatus Audio Volume");
        SendCommand("xStatus Audio Microphones");
        SendCommand("xStatus Conference DoNotDisturb");
        SendCommand("xStatus Conference Presentation");
        SendCommand("xStatus Cameras SpeakerTrack status");
        SendCommand("xStatus Video Selfview");
        SendCommand("xStatus Video Layout CurrentLayouts ActiveLayout");
        SendCommand("xStatus SIP Registration URI");
        PhoneBookParser.RequestPhonebook();
      }
      catch (Exception ex)
      {
        Log("Can't initialise Cisco Room OS");
        Log(ex.Message);
      }
      PollWorker.Restart();
    }

    private void SendCommand(string command)
    {
      try
      {
        _communicationClient.Send(command + "\r\n");
        UpdateCommunicationState(CommunicationState.Okay);
      }
      catch (Exception ex)
      {
        UpdateCommunicationState(CommunicationState.Error);
      }
    }

    private void SendHeartbeat()
    {
      SendCommand($"xCommand Peripherals HeartBeat ID: {_moduleIdentifier} Timeout: 120");
      Log("Sending Heartbeat");
    }

    private void SendCallCommand(string commandString) => SendCommand($"xCommand Call {commandString}");

    public override void SendDtmf(char number) => SendCallCommand($"DTMFSend DTMFString: {number}");

    public override void Dial(string number) => SendCommand($"xCommand Dial Number: {number}");

    public void Answer(int callId) => SendCallCommand($"Accept CallId: {callId}");

    public void Answer() => SendCallCommand("Accept");

    public void HangUp(int callId = 0) => SendCallCommand(callId == 0 ? "Disconnect" : $"Disconnect CallId: {callId}");

    public override void PowerOn() => SendCommand("xCommand Standby Deactivate");

    protected override void Poll()
    {
      SendHeartbeat();
    }

    public override void PowerOff() => SendCommand("xCommand Standby Activate");

    public void SelfView(bool on) => SendCommand(on ? "xCommand Video SelfView Set Mode: On" : "xCommand Video SelfView Set Mode: Off");

    private void HandleResponse(string response)
    {
      if(response.Contains("*s"))
        Log("Status");
      else if (response.Contains("*r"))
        Log("Response");
      else
        return;
      
      var responses = response.Split(' ');
      
      if (response.Contains("PhonebookSearchResult"))
        UpdateCommunicationState(PhoneBookParser.HandlePhonebookSearchResponse(response));
      else if (response.Contains("PeripheralsHeartBeatResult"))
      {
        UpdateCommunicationState(response.Contains("status=OK")? CommunicationState.Okay : CommunicationState.Error);

        if (CommunicationState == CommunicationState.Error)
        {
          InitialiseModule();
        }
      }
      else if (response.Contains("Audio Volume:"))
      {
        OutputVolume.SetVolumeFromPercentage(double.Parse(responses[3]));
      }
      else if (response.Contains("Audio Microphones Mute:"))
      {
        MicrophoneMute.MuteState = responses[4].Contains("On")? MuteState.On : MuteState.Off;
      }
    }

    public void SetOutputVolume(int volume)
    {
      SendCommand($"xCommand Audio Volume Set Level: {volume}");
    }
  }