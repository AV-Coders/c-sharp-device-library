using AVCoders.Core;

namespace AVCoders.Conference;

public record CiscoRoomOsDeviceInfo(string Name, string SoftwareInfo, string HardwareInfo, string SerialNumber);

public class CiscoRoomOsOutputFader : VolumeControl
{
  private const int MinimumVolume = 0;
  private const int MaximumVolume = 100;
  private readonly CiscoRoomOs _codec;
    
  public CiscoRoomOsOutputFader(string name, CiscoRoomOs codec, VolumeType type) : base(name, type)
  {
    _codec = codec;
    _codec.OutputVolume.VolumeLevelHandlers += x => Volume = x;
    _codec.OutputMute.MuteStateHandlers += x => MuteState = x;
    Volume = _codec.OutputVolume.Volume;
    MuteState = _codec.OutputMute.MuteState;
  }

  public override void LevelUp(int amount) =>
    _codec.SetOutputVolume(Math.Clamp(_codec.OutputVolume.Volume + amount, MinimumVolume, MaximumVolume));

  public override void LevelDown(int amount) =>
    _codec.SetOutputVolume(Math.Clamp(_codec.OutputVolume.Volume - amount, MinimumVolume, MaximumVolume));

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

  public override bool SupportsLevel => false;

  public override void LevelUp(int amount) { using (PushProperties("LevelUp")) LogWarning("{Name} does not support volume", Name); }

  public override void LevelDown(int amount) { using (PushProperties("LevelDown")) LogWarning("{Name} does not support volume", Name); }

  public override void SetLevel(int percentage) { using (PushProperties("SetLevel")) LogWarning("{Name} does not support volume", Name); }

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
    private const int LoginTokens = 3;
    private const int ValueTokens = 4;
    private const int ItemValueTokens = 5;
    private const int PathValueTokens = 6;
    private const int MaxMissedReplies = 3;
    private const string StatusMarker = "*s ";
    private const string ResultMarker = "*r ";
    private const string ResultIdMarker = "** resultId:";
    private const string ReplyEndMarker = "** end";
    private const string PhonebookResultMarker = "*r PhonebookSearchResult";
    private const string RejectedMarker = "(status=Error)";
    private const string GhostMarker = "(ghost=True):";
    private const string ReplySeparator = " | ";
    private const string EmptyString = "\"\"";
    private const string ProductIdRead = "product-id";
    private const string SoftwareVersionRead = "software-version";
    private const string SoftwareDisplayNameRead = "software-display-name";
    private const string SerialNumberRead = "serial-number";
    private const string UptimeRead = "uptime";
    private const string MacAddressRead = "mac-address";
    private const string EthernetSpeedRead = "ethernet-speed";
    private const string IpAddressRead = "ip-address";
    private const string SubnetMaskRead = "subnet-mask";
    private const string GatewayRead = "gateway";
    private const string TemperatureRead = "temperature";
    private const string DiagnosticsRead = "diagnostics";
    private const string SipRegistrationRead = "sip-registration";
    private const string WebexRead = "webex";
    private const string PeripheralsRead = "peripherals";
    private const string ProductIdPath = "SystemUnit ProductId";
    private const string SoftwareVersionPath = "SystemUnit Software Version";
    private const string SoftwareDisplayNamePath = "SystemUnit Software DisplayName";
    private const string SerialNumberPath = "SystemUnit Hardware Module SerialNumber";
    private const string UptimePath = "SystemUnit Uptime";
    private const string MacAddressPath = "Network 1 Ethernet MacAddress";
    private const string EthernetSpeedPath = "Network 1 Ethernet Speed";
    private const string IpAddressPath = "Network 1 IPv4 Address";
    private const string SubnetMaskPath = "Network 1 IPv4 SubnetMask";
    private const string GatewayPath = "Network 1 IPv4 Gateway";
    private const string TemperaturePath = "SystemUnit Hardware Monitoring Temperature Status";
    private const string WebexStatusPath = "Webex Status";
    private const string ModelLabel = "Model";
    private const string FirmwareLabel = "Firmware";
    private const string SoftwareLabel = "Software";
    private const string SerialLabel = "Serial";
    private const string UptimeLabel = "Uptime";
    private const string MacAddressLabel = "MAC Address";
    private const string EthernetSpeedLabel = "Ethernet Speed";
    private const string IpAddressLabel = "IP Address";
    private const string SubnetMaskLabel = "Subnet Mask";
    private const string GatewayLabel = "Gateway";
    private const string TemperatureLabel = "Temperature";
    private const string WebexLabel = "Webex";
    private const string InfoDiagnosticsLabel = "Diagnostics (info)";
    private const string DiagnosticsLabel = "Diagnostics";
    private const string SipRegistrationLabel = "SIP Registration";
    private const string PeripheralsLabel = "Peripherals";
    private const string InfoLevel = "Info";
    private const string SipRegistrationLabelPrefix = "SIP Registration ";
    private const string PeripheralLabelPrefix = "Peripheral ";
    private const string TemperatureIssueKey = "temperature";
    private const string WebexIssueKey = "webex";
    private const string SipRegistrationIssuePrefix = "sip-registration:";
    private const string DiagnosticIssuePrefix = "diagnostic:";
    private const string PeripheralIssuePrefix = "peripheral:";
    private const string ControlSystemType = nameof(PeripheralType.ControlSystem);
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly string[] FeedbackMarkers = ["*s ", "*r ", "*c ", "*e "];
    private static readonly Dictionary<string, (string Path, string Label)> StatusReads = new()
    {
      [ProductIdRead] = (ProductIdPath, ModelLabel),
      [SoftwareVersionRead] = (SoftwareVersionPath, FirmwareLabel),
      [SoftwareDisplayNameRead] = (SoftwareDisplayNamePath, SoftwareLabel),
      [SerialNumberRead] = (SerialNumberPath, SerialLabel),
      [UptimeRead] = (UptimePath, UptimeLabel),
      [MacAddressRead] = (MacAddressPath, MacAddressLabel),
      [EthernetSpeedRead] = (EthernetSpeedPath, EthernetSpeedLabel),
      [IpAddressRead] = (IpAddressPath, IpAddressLabel),
      [SubnetMaskRead] = (SubnetMaskPath, SubnetMaskLabel),
      [GatewayRead] = (GatewayPath, GatewayLabel),
      [TemperatureRead] = (TemperaturePath, TemperatureLabel),
      [DiagnosticsRead] = ("Diagnostics", DiagnosticsLabel),
      [SipRegistrationRead] = ("SIP Registration", SipRegistrationLabel),
      [WebexRead] = (WebexStatusPath, WebexLabel),
      [PeripheralsRead] = ("Peripherals ConnectedDevice", PeripheralsLabel)
    };
    private static readonly Dictionary<string, string> ReadsByPath =
      StatusReads.ToDictionary(status => status.Value.Path, status => status.Key);
    private static readonly CallStatus[] CallStatusPriority =
    [
      CallStatus.Ringing,
      CallStatus.Dialling,
      CallStatus.Connecting,
      CallStatus.Connected,
      CallStatus.OnHold,
      CallStatus.Disconnecting
    ];
    private static readonly Dictionary<string, CallStatus> CallStatuses =
      Enum.GetValues<CallStatus>().ToDictionary(status => status.ToString());
    private readonly CiscoRoomOsDeviceInfo _deviceInfo;
    private readonly string _moduleIdentifier;
    private readonly PeripheralType _peripheralType;
    private readonly List<string> _replyLines = [];
    private readonly Dictionary<string, DiagnosticsMessage> _diagnostics = new();
    private readonly Dictionary<string, SipRegistration> _sipRegistrations = new();
    private readonly Dictionary<string, ConnectedDevice> _connectedDevices = new();
    private bool _uptimeRejected;
    private int _missedUptimeReplies;
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
          SendCommand("xFeedback register /Event/CallDisconnect");
          SendCommand("xFeedback register /Status/Audio/Volume");
          SendCommand("xFeedback register /Status/Audio/VolumeMute");
          SendCommand("xFeedback register /Status/Audio/Microphones/Mute");
          SendCommand("xFeedback Register Configuration/Conference/AutoAnswer/Mode");
          SendCommand("xFeedback register /Status/SystemUnit/Hardware/Monitoring/Temperature/Status");
          SendCommand("xFeedback register /Status/Diagnostics");
          SendCommand("xFeedback register /Status/SIP/Registration");
          SendCommand("xFeedback register /Status/Webex/Status");
          SendCommand("xFeedback register /Status/Peripherals/ConnectedDevice");
          SendCommand("xStatus Standby");
          SendCommand("xStatus Conference DoNotDisturb");
          SendCommand("xStatus Call");
          SendCommand("xStatus Audio Volume");
          SendCommand("xStatus Audio VolumeMute");
          SendCommand("xStatus Audio Microphones Mute");
          SendCommand("xConfiguration Conference AutoAnswer Mode");
          _replyLines.Clear();
          _diagnostics.Clear();
          _connectedDevices.Clear();
          _sipRegistrations.Clear();
          _uptimeRejected = false;
          _missedUptimeReplies = 0;
          foreach (var read in StatusReads.Keys)
            SendRead(read);
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
      catch (Exception e)
      {
        LogException(e, $"Failed to send {command}");
        CommunicationState = CommunicationState.Error;
      }
    }

    private void SendRead(string read) => SendCommand($"xStatus {StatusReads[read].Path} | resultId=\"{read}\"");

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
        RaiseActiveCallsChanged(GetActiveCalls());
      }
    }

    protected override void DoPowerOn() => SendCommand("xCommand Standby Deactivate");

    protected override Task Poll(CancellationToken token)
    {
      if (!_uptimeRejected)
        SendRead(UptimeRead);
      return SendHeartbeat();
    }

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
      foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        ProcessResponse(line);
    }

    private void ProcessResponse(string response)
    {
      if (response.StartsWith(PhonebookResultMarker, StringComparison.Ordinal))
        return;
      using (PushProperties("ProcessResponse"))
      {
        if (response.StartsWith(ResultIdMarker))
        {
          HandleTaggedReply(response);
          return;
        }
        if (response.StartsWith(ReplyEndMarker))
        {
          _replyLines.Clear();
          return;
        }
        if (!FeedbackMarkers.Any(marker => response.StartsWith(marker)))
          return;
        _replyLines.Add(response);

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
              if (response.Contains("status=OK"))
                SendCommand("xStatus Call");
              return;
            case "Call" when responses.Length >= ValueTokens && responses[3].Contains("(ghost=True)"):
              EndCall(int.Parse(responses[2]));
              return;
            case "Call" when responses.Length >= ItemValueTokens:
              ProcessCallResponse(responses);
              RaiseActiveCallsChanged(GetActiveCalls());
              return;
            case "CallDisconnect" when responses.Length >= ValueTokens && responses[2] == "CallId:":
              EndCall(int.Parse(responses[3]));
              return;
            case "Audio" when responses.Length >= ValueTokens && responses[2] == "Volume:":
              OutputVolume.SetVolumeFromPercentage(double.Parse(responses[3]));
              return;
            case "Audio" when responses.Length >= ValueTokens && responses[2] == "VolumeMute:":
              OutputMute.MuteState = responses[3].Contains("On") ? MuteState.On : MuteState.Off;
              return;
            case "Audio" when responses.Length >= ItemValueTokens && responses[2] == "Microphones" && responses[3] == "Mute:":
              MicrophoneMute.MuteState = responses[4].Contains("On") ? MuteState.On : MuteState.Off;
              return;
            case "Conference" when responses.Length >= ValueTokens && responses[2] == "DoNotDisturb:":
              DoNotDisturbState = responses[3].Contains("Active") ? PowerState.On : PowerState.Off;
              ValidateDoNotDisturbState();
              return;
            case "xConfiguration" when responses.Length >= PathValueTokens && responses[2] == "Conference" && responses[3] == "AutoAnswer" && responses[4] == "Mode:":
              AutoAnswerState = responses[5].Contains("On") ? PowerState.On : PowerState.Off;
              return;

            case "SIP" when responses.Length >= PathValueTokens && responses[2] == "Registration" && responses[4] == "URI:":
              Uri = responses[5].Trim().Trim('"');
              return;
            case "SIP" when responses.Length >= ItemValueTokens && responses[2] == "Registration":
              UpdateSipRegistration(responses[3], responses[4], StatusValue(response), response);
              return;
            case "SystemUnit" or "Network" or "Webex":
              ShowStatusValue(StatusPath(response), StatusValue(response), response);
              return;
            case "Diagnostics" when responses.Length >= ItemValueTokens && responses[2] == "Message":
              UpdateDiagnosticsMessage(responses[3], responses[4], StatusValue(response), response);
              return;
            case "Peripherals" when responses.Length >= ItemValueTokens && responses[2] == "ConnectedDevice":
              UpdateConnectedDevice(responses[3], responses[4], StatusValue(response), response);
              return;
            case "Login" when responses.Length >= LoginTokens && responses[2].Trim()== "successful":
              Reinitialise();
              return;
            case "Standby" when responses.Length >= ValueTokens && responses[2] == "State:":
              PowerState = responses[3].Contains("Off") ? PowerState.On : PowerState.Off;
              ProcessPowerState();
              return;
            default:
              LogDebug("Unhandled feedback {Response}", response);
              return;
          }
        }
        catch (Exception e)
        {
          LogException(e, $"An exception was thrown while processing the string {response}");
        }
      }
    }
    
    private void HandleTaggedReply(string line)
    {
      string read = Unquote(StatusValue(line));
      string reply = string.Join(ReplySeparator, _replyLines.Append(line));
      if (!StatusReads.TryGetValue(read, out var status))
      {
        LogWarning("The codec tagged a reply {ResultId}, which this driver never asks for: {Reply}", read, reply);
        return;
      }

      if (_replyLines.Any(replyLine => replyLine.StartsWith(ResultMarker) && replyLine.Contains(RejectedMarker)))
      {
        HandleRejectedRead(read, status.Path, status.Label, reply);
        return;
      }

      switch (read)
      {
        case DiagnosticsRead:
          RemoveDetail(DiagnosticsLabel);
          ResolveIssue(ReadIssueKey(read));
          ResolveClearedDiagnostics();
          ShowInfoDiagnostics();
          return;
        case PeripheralsRead:
          RemoveDetail(PeripheralsLabel);
          ResolveIssue(ReadIssueKey(read));
          RemoveUnlistedConnectedDevices();
          return;
        case SipRegistrationRead:
          RemoveDetail(SipRegistrationLabel);
          ResolveIssue(ReadIssueKey(read));
          RemoveUnlistedSipRegistrations();
          return;
        default:
          if (!_replyLines.Any(replyLine => replyLine.StartsWith(StatusMarker)))
            ShowEmptyValue(read, reply);
          return;
      }
    }

    private void ShowEmptyValue(string read, string reply)
    {
      if (read == UptimeRead)
        _missedUptimeReplies = 0;
      SetDetail(StatusReads[read].Label, reply);
      ResolveIssue(ReadIssueKey(read));
    }

    private void HandleRejectedRead(string read, string path, string label, string reply)
    {
      if (read == UptimeRead)
        _uptimeRejected = true;
      LogInformation("The codec does not support xStatus {Path}: {Reply}", path, reply);
      SetDetail(label, $"Not supported: {reply}", DetailTone.Normal);
      ResolveIssue(ReadIssueKey(read));
    }

    private void ShowStatusValue(string path, string value, string line)
    {
      if (value.Length == 0 && ReadsByPath.TryGetValue(path, out var emptyRead))
      {
        ShowEmptyValue(emptyRead, line);
        return;
      }
      switch (path)
      {
        case ProductIdPath:
          ShowIdentity(ProductIdRead, ModelLabel, value);
          return;
        case SoftwareVersionPath:
          ShowIdentity(SoftwareVersionRead, FirmwareLabel, value);
          return;
        case SoftwareDisplayNamePath:
          ShowIdentity(SoftwareDisplayNameRead, SoftwareLabel, value);
          return;
        case SerialNumberPath:
          ShowIdentity(SerialNumberRead, SerialLabel, value);
          return;
        case MacAddressPath:
          ShowIdentity(MacAddressRead, MacAddressLabel, value);
          return;
        case EthernetSpeedPath:
          ShowIdentity(EthernetSpeedRead, EthernetSpeedLabel, value);
          return;
        case IpAddressPath:
          ShowIdentity(IpAddressRead, IpAddressLabel, value);
          return;
        case SubnetMaskPath:
          ShowIdentity(SubnetMaskRead, SubnetMaskLabel, value);
          return;
        case GatewayPath:
          ShowIdentity(GatewayRead, GatewayLabel, value);
          return;
        case UptimePath:
          ShowUptime(value, line);
          return;
        case TemperaturePath:
          ShowTemperature(value);
          return;
        case WebexStatusPath:
          ShowWebexStatus(value);
          return;
        default:
          LogDebug("Unhandled feedback {Response}", line);
          return;
      }
    }

    private void ShowIdentity(string read, string label, string value)
    {
      SetDetail(label, Unquote(value));
      ResolveIssue(ReadIssueKey(read));
    }

    private void ShowUptime(string value, string line)
    {
      if (!uint.TryParse(value, out uint seconds))
      {
        SetDetail(UptimeLabel, value, DetailTone.Warning);
        CountMissedUptimeReply(line);
        return;
      }
      _missedUptimeReplies = 0;
      var uptime = TimeSpan.FromSeconds(seconds);
      SetDetail(UptimeLabel, $"{(int)uptime.TotalDays} d {uptime.Hours} h {uptime.Minutes} min");
      ResolveIssue(ReadIssueKey(UptimeRead));
    }

    private void CountMissedUptimeReply(string line)
    {
      var missed = ++_missedUptimeReplies;
      if (missed < MaxMissedReplies)
      {
        LogInformation("The codec's uptime is not a number of seconds, reading it again at the next poll ({Missed} of {MaxMissedReplies}): {Reply}",
          missed, MaxMissedReplies, line);
        return;
      }
      RaiseOngoingIssue(ReadIssueKey(UptimeRead),
        $"The codec's uptime was not a number of seconds {MaxMissedReplies} times in a row, the last was {line}", IssueSeverity.Minor);
    }

    private void ShowTemperature(string value)
    {
      switch (value)
      {
        case "Normal":
          SetDetail(TemperatureLabel, value);
          ResolveIssue(TemperatureIssueKey);
          break;
        case "Unknown":
          SetDetail(TemperatureLabel, value, DetailTone.Warning);
          break;
        case "High":
          SetDetail(TemperatureLabel, value, DetailTone.Warning);
          RaiseOngoingIssue(TemperatureIssueKey, "The codec's temperature is High", IssueSeverity.Minor);
          break;
        case "Critical":
          SetDetail(TemperatureLabel, value, DetailTone.Error);
          RaiseOngoingIssue(TemperatureIssueKey, "The codec's temperature is Critical", IssueSeverity.Critical);
          break;
        default:
          SetDetail(TemperatureLabel, value);
          break;
      }
      ResolveIssue(ReadIssueKey(TemperatureRead));
    }

    private void ShowWebexStatus(string value)
    {
      switch (value)
      {
        case "Registered":
        case "Disabled":
          SetDetail(WebexLabel, value);
          ResolveIssue(WebexIssueKey);
          break;
        case "Stopped":
          SetDetail(WebexLabel, value, DetailTone.Warning);
          ResolveIssue(WebexIssueKey);
          break;
        case "Registering":
          SetDetail(WebexLabel, value, DetailTone.Warning);
          break;
        case "Error":
          SetDetail(WebexLabel, value, DetailTone.Error);
          RaiseOngoingIssue(WebexIssueKey, "The codec's Webex status is Error", IssueSeverity.Major);
          break;
        default:
          SetDetail(WebexLabel, value);
          break;
      }
      ResolveIssue(ReadIssueKey(WebexRead));
    }

    private void UpdateSipRegistration(string instance, string field, string value, string line)
    {
      var registration = _sipRegistrations.GetValueOrDefault(instance, new SipRegistration(null, null));
      switch (field)
      {
        case GhostMarker:
          _sipRegistrations.Remove(instance);
          RemoveDetail($"{SipRegistrationLabelPrefix}{instance}");
          ResolveIssue($"{SipRegistrationIssuePrefix}{instance}");
          return;
        case "Status:":
          registration = registration with { Status = value };
          break;
        case "Reason:":
          registration = registration with { Reason = value };
          break;
        default:
          LogDebug("Unhandled feedback {Response}", line);
          return;
      }
      _sipRegistrations[instance] = registration;
      ShowSipRegistration(instance, registration);
    }

    private void ShowSipRegistration(string instance, SipRegistration registration)
    {
      if (registration.Status == null)
        return;
      string label = $"{SipRegistrationLabelPrefix}{instance}";
      string issueKey = $"{SipRegistrationIssuePrefix}{instance}";
      string text = registration.Reason is null or EmptyString
        ? registration.Status
        : $"{registration.Status}: {Unquote(registration.Reason)}";
      switch (registration.Status)
      {
        case "Registered":
        case "Inactive":
          SetDetail(label, text);
          ResolveIssue(issueKey);
          break;
        case "Registering":
        case "Deregister":
          SetDetail(label, text, DetailTone.Warning);
          break;
        case "Failed":
          SetDetail(label, text, DetailTone.Error);
          RaiseOngoingIssue(issueKey, $"SIP registration {instance} failed: {Unquote(registration.Reason)}", IssueSeverity.Major);
          break;
        default:
          SetDetail(label, text, DetailTone.Warning);
          RaiseOngoingIssue(issueKey, $"SIP registration {instance} reports a status this driver does not recognise: {registration.Status}", IssueSeverity.Minor);
          break;
      }
    }

    private void UpdateDiagnosticsMessage(string instance, string field, string value, string line)
    {
      var previous = _diagnostics.GetValueOrDefault(instance, new DiagnosticsMessage(null, null, null, null));
      switch (field)
      {
        case GhostMarker:
          _diagnostics.Remove(instance);
          break;
        case "Level:":
          _diagnostics[instance] = previous with { Level = value };
          break;
        case "Type:":
          _diagnostics[instance] = previous with { Type = value };
          break;
        case "Description:":
          _diagnostics[instance] = previous with { Description = value };
          break;
        case "References:":
          _diagnostics[instance] = previous with { References = value };
          break;
        default:
          LogDebug("Unhandled feedback {Response}", line);
          return;
      }
      ShowDiagnostic(previous.Type);
      ShowDiagnostic(_diagnostics.GetValueOrDefault(instance)?.Type);
      ShowInfoDiagnostics();
    }

    private void ShowDiagnostic(string? type)
    {
      if (type == null)
        return;
      var messages = _diagnostics.Values.Where(message => message.Type == type && message.Level != InfoLevel).ToList();
      if (messages.Count == 0)
      {
        ResolveIssue($"{DiagnosticIssuePrefix}{type}");
        return;
      }
      RaiseOngoingIssue($"{DiagnosticIssuePrefix}{type}", string.Join("; ", messages.Select(DiagnosticText)),
        messages.Max(message => DiagnosticSeverity(message.Level)));
    }

    private static string DiagnosticText(DiagnosticsMessage message) =>
      message.References is null or EmptyString
        ? $"{message.Level} {message.Type}: {Unquote(message.Description)}"
        : $"{message.Level} {message.Type}: {Unquote(message.Description)} ({Unquote(message.References)})";

    private static IssueSeverity DiagnosticSeverity(string? level) => level switch
    {
      "Critical" => IssueSeverity.Critical,
      "Error" => IssueSeverity.Major,
      _ => IssueSeverity.Minor
    };

    private void ShowInfoDiagnostics()
    {
      var infoMessages = _diagnostics.Values.Where(message => message.Level == InfoLevel && message.Type != null).ToList();
      if (infoMessages.Count == 0)
        RemoveDetail(InfoDiagnosticsLabel);
      else
        SetDetail(InfoDiagnosticsLabel, string.Join("; ", infoMessages.Select(message => $"{message.Type}: {Unquote(message.Description)}")));
    }

    private void ResolveClearedDiagnostics()
    {
      var listedKeys = _diagnostics.Values.Where(message => message.Level != InfoLevel).Select(message => $"{DiagnosticIssuePrefix}{message.Type}").ToHashSet();
      foreach (var cleared in GetOngoingIssues().Where(issue => issue.Key.StartsWith(DiagnosticIssuePrefix) && !listedKeys.Contains(issue.Key)))
        ResolveIssue(cleared.Key);
    }

    private void UpdateConnectedDevice(string id, string field, string value, string line)
    {
      var device = _connectedDevices.GetValueOrDefault(id, new ConnectedDevice(null, null, null, null, null, null));
      switch (field)
      {
        case GhostMarker:
          _connectedDevices.Remove(id);
          RemoveDetail($"{PeripheralLabelPrefix}{id}");
          ResolveIssue($"{PeripheralIssuePrefix}{id}");
          return;
        case "ID:":
          device = device with { Id = value };
          break;
        case "Name:":
          device = device with { Name = value };
          break;
        case "Type:":
          device = device with { Type = value };
          break;
        case "Status:":
          device = device with { Status = value };
          break;
        case "SoftwareInfo:":
          device = device with { SoftwareInfo = value };
          break;
        case "SerialNumber:":
          device = device with { SerialNumber = value };
          break;
        default:
          LogDebug("Unhandled feedback {Response}", line);
          return;
      }
      _connectedDevices[id] = device;
      ShowConnectedDevice(id, device);
    }

    private void ShowConnectedDevice(string id, ConnectedDevice device)
    {
      string label = $"{PeripheralLabelPrefix}{id}";
      string issueKey = $"{PeripheralIssuePrefix}{id}";
      string text = $"{Unquote(device.Name)}, {device.Type}, {device.Status}, software {Unquote(device.SoftwareInfo)}, serial {Unquote(device.SerialNumber)}";
      if (device.Type == null)
      {
        SetDetail(label, text);
        return;
      }
      if (device.Type == ControlSystemType && Unquote(device.Id) != _moduleIdentifier)
      {
        SetDetail(label, text, DetailTone.Normal);
        ResolveIssue(issueKey);
        return;
      }
      switch (device.Status)
      {
        case null:
          SetDetail(label, text);
          break;
        case "Connected":
          SetDetail(label, text);
          ResolveIssue(issueKey);
          break;
        case "Unpairing":
          SetDetail(label, text, DetailTone.Warning);
          break;
        case "LostConnection":
        case "ResponseTimedOut":
          SetDetail(label, text, DetailTone.Error);
          RaiseOngoingIssue(issueKey, $"Peripheral {id}, {Unquote(device.Name)} ({device.Type}), is {device.Status}", IssueSeverity.Major);
          break;
        default:
          SetDetail(label, text, DetailTone.Warning);
          RaiseOngoingIssue(issueKey, $"Peripheral {id}, {Unquote(device.Name)} ({device.Type}), reports a status this driver does not recognise: {device.Status}", IssueSeverity.Minor);
          break;
      }
    }

    private void RemoveUnlistedConnectedDevices()
    {
      foreach (var detail in Details.Where(detail => detail.Label.StartsWith(PeripheralLabelPrefix) && !_connectedDevices.ContainsKey(detail.Label[PeripheralLabelPrefix.Length..])))
        RemoveDetail(detail.Label);
      foreach (var issue in GetOngoingIssues().Where(issue => issue.Key.StartsWith(PeripheralIssuePrefix) && !_connectedDevices.ContainsKey(issue.Key[PeripheralIssuePrefix.Length..])))
        ResolveIssue(issue.Key);
    }

    private void RemoveUnlistedSipRegistrations()
    {
      foreach (var detail in Details.Where(detail => detail.Label.StartsWith(SipRegistrationLabelPrefix) && !_sipRegistrations.ContainsKey(detail.Label[SipRegistrationLabelPrefix.Length..])))
        RemoveDetail(detail.Label);
      foreach (var issue in GetOngoingIssues().Where(issue => issue.Key.StartsWith(SipRegistrationIssuePrefix) && !_sipRegistrations.ContainsKey(issue.Key[SipRegistrationIssuePrefix.Length..])))
        ResolveIssue(issue.Key);
    }

    private static string ReadIssueKey(string read) => $"{read}-read";

    private static string StatusPath(string response) => response.Split(':', 2) is [var path, _] ? path[StatusMarker.Length..] : response;

    private static string StatusValue(string response) => response.Split(':', 2) is [_, var value] ? value.Trim() : string.Empty;

    private static string Unquote(string? value) =>
      value != null && value.Length > EmptyString.Length && value.StartsWith('"') && value.EndsWith('"')
        ? value[1..^1]
        : value ?? string.Empty;

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

    private void ProcessCallResponse(string[] responses)
    {
      int callId = int.Parse(responses[2]);
      if (!ActiveCalls.ContainsKey(callId))
      {
        ActiveCalls[callId] = new Call(CallStatus.Unknown, string.Empty, string.Empty);
      }

      if (responses[3] == "Status:")
      {
        if (!CallStatuses.TryGetValue(responses[4].Trim(), out var status))
        {
          LogWarning("Unknown status {Status} for call {CallId}", responses[4], callId);
          return;
        }
        ActiveCalls[callId].Status = status;
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
    }

    private void EndCall(int callId)
    {
      if (!ActiveCalls.Remove(callId))
        return;
      CallStatusFeedback();
      RaiseActiveCallsChanged(GetActiveCalls());
    }

    private void CallStatusFeedback()
    {
      var idleCalls = ActiveCalls.Where(x => x.Value.Status == CallStatus.Idle).Select(x => x.Key).ToList();
      idleCalls.ForEach(x => ActiveCalls.Remove(x));

      CallStatus = CallStatusPriority.FirstOrDefault(
        status => ActiveCalls.Values.Any(call => call.Status == status), CallStatus.Idle);
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
      _desiredDoNotDisturbState = state;
      SendCommand($"xCommand Conference DoNotDisturb {(state == PowerState.On ? "Activate": "Deactivate")}");
      DoNotDisturbState = state;
    }

    private record DiagnosticsMessage(string? Level, string? Type, string? Description, string? References);

    private record SipRegistration(string? Status, string? Reason);

    private record ConnectedDevice(string? Id, string? Name, string? Type, string? Status, string? SoftwareInfo, string? SerialNumber);
  }