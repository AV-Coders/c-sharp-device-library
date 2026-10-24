using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Dsp;

public record QscAudioBlockInfo(string Name, string LevelInstanceTag, string MuteInstanceTag);

public record QscAudioBlockWithSelectInfo(
    string Name,
    string LevelInstanceTag,
    string MuteInstanceTag,
    string SelectInstanceTag): QscAudioBlockInfo(Name, LevelInstanceTag, MuteInstanceTag);

public class QscGain(VolumeLevelHandler volumeLevelHandler) : Fader(volumeLevelHandler);

public class QscMute(MuteStateHandler muteStateHandler) : Mute(muteStateHandler);

public class QscInt : StringValue
{
    public QscInt(StringValueHandler stringValueHandler) : base(stringValueHandler)
    {
    }
}

public class QsysVolumeControl : VolumeControl
{
    private readonly string _levelNamedControl;
    private readonly string _muteNamedControl;
    private readonly QsysEcp _dsp;

    public QsysVolumeControl(QscAudioBlockInfo audioBlockInfo, VolumeType type, QsysEcp dsp) : base(audioBlockInfo.Name, type)
    {
        _dsp = dsp;
        _levelNamedControl = audioBlockInfo.LevelInstanceTag;
        _muteNamedControl = audioBlockInfo.MuteInstanceTag;
        _dsp.AddControl(volumeLevel => Volume = volumeLevel, _levelNamedControl);
        _dsp.AddControl(muteState => MuteState = muteState, _muteNamedControl);
    }
    
    public override void LevelUp(int amount) => _dsp.LevelUp(_levelNamedControl, amount);

    public override void LevelDown(int amount) => _dsp.LevelDown(_levelNamedControl, amount);

    public override void SetLevel(int percentage) => _dsp.SetLevel(_levelNamedControl, percentage);

    public override void ToggleAudioMute() => _dsp.ToggleAudioMute(_muteNamedControl);
    
    public override void SetAudioMute(MuteState state) => _dsp.SetAudioMute(_muteNamedControl, state);
}

public class QsysVolumeOnlyControl : VolumeControl
{
    private readonly string _levelNamedControl;
    private readonly QsysEcp _dsp;

    public QsysVolumeOnlyControl(string name, string levelInstanceTag, VolumeType type, QsysEcp dsp) : base(name, type)
    {
        _dsp = dsp;
        _levelNamedControl = levelInstanceTag;
        _dsp.AddControl(volumeLevel => Volume = volumeLevel, _levelNamedControl);
    }

    public override bool SupportsMute => false;

    public override void LevelUp(int amount) => _dsp.LevelUp(_levelNamedControl, amount);

    public override void LevelDown(int amount) => _dsp.LevelDown(_levelNamedControl, amount);

    public override void SetLevel(int percentage) => _dsp.SetLevel(_levelNamedControl, percentage);

    public override void ToggleAudioMute() { using (PushProperties("ToggleAudioMute")) LogWarning("{Name} does not support mute", Name); }

    public override void SetAudioMute(MuteState state) { using (PushProperties("SetAudioMute")) LogWarning("{Name} does not support mute", Name); }
}

public class QsysMuteOnlyControl : VolumeControl
{
    private readonly string _muteNamedControl;
    private readonly QsysEcp _dsp;

    public QsysMuteOnlyControl(string name, string muteInstanceTag, VolumeType type, QsysEcp dsp) : base(name, type)
    {
        _dsp = dsp;
        _muteNamedControl = muteInstanceTag;
        _dsp.AddControl(muteState => MuteState = muteState, _muteNamedControl);
    }

    public override bool SupportsLevel => false;

    public override void LevelUp(int amount) { using (PushProperties("LevelUp")) LogWarning("{Name} does not support volume", Name); }

    public override void LevelDown(int amount) { using (PushProperties("LevelDown")) LogWarning("{Name} does not support volume", Name); }

    public override void SetLevel(int percentage) { using (PushProperties("SetLevel")) LogWarning("{Name} does not support volume", Name); }

    public override void ToggleAudioMute() => _dsp.ToggleAudioMute(_muteNamedControl);

    public override void SetAudioMute(MuteState state) => _dsp.SetAudioMute(_muteNamedControl, state);
}

public class QsysEcp : Dsp
{
    public static readonly ushort DefaultPort = 1702;
    public const int DefaultPollIntervalInMilliseconds = 20000;
    public const int MaxUnansweredStatusRequests = 3;
    public const string BadControlIssuePrefix = "bad-control:";
    public const string UnansweredStatusIssueKey = "unanswered-status";
    public const string StatusParseIssueKey = "status-parse";
    public const string CoreStandbyIssueKey = "core-standby";
    public const string LoginIssueKey = "login";
    public const string ChangeGroupsIssueKey = "change-groups";
    public const string BadChangeGroupIssueKey = "bad-change-group";
    public const string BadCommandIssueKey = "bad-command";
    public const string ReadOnlyIssueKey = "read-only-control";
    public const string ConnectionClosedIssueKey = "connection-closed";

    private const int EscalateAfterRepeats = 3;
    private const int ChangeGroupSetupStepMs = 500;
    private const uint ChangeGroupPollPeriodMs = 100;
    private const uint MeterPollPeriodMs = 1000;
    private const string StatusRequest = "sg\n";
    private const string StatusFlagSet = "1";
    private const string StandbyCoreMessage = "Connected to the standby Core, commands are ignored";

    private const string ControlValueReply = "cv";
    private const string ControlVectorValueReply = "cvv";
    private const string StatusReply = "sr";
    private const string BadIdReply = "bad_id";
    private const string BadCommandReply = "bad_command";
    private const string BadChangeGroupReply = "bad_change_group_handle";
    private const string TooManyChangeGroupsReply = "too_many_change_groups";
    private const string ControlReadOnlyReply = "control_read_only";
    private const string CoreReadOnlyReply = "core_read_only";
    private const string CoreNotActiveReply = "core_not_active";
    private const string LoginRequiredReply = "login_required";
    private const string LoginFailedReply = "login_failed";
    private const string LoginSuccessReply = "login_success";
    private const string ConnectionClosedReply = "rc";

    private const string DesignLabel = "Design";
    private const string DesignIdLabel = "Design ID";
    private const string CoreLabel = "Core";
    private const string MissingControlsLabel = "Missing Controls";

    private readonly TcpClient _tcpClient;
    private readonly Dictionary<string, QscGain> _gains = new();
    private readonly Dictionary<string, QscMute> _mutes = new();
    private readonly Dictionary<string, QscInt> _strings = new();
    private readonly Dictionary<string, QscGain> _meters = new();
    private readonly HashSet<string> _missingControls = new();
    private readonly Regex _responseParser;
    private readonly Regex _badIdParser;
    private readonly Regex _statusParser = new("^sr \"(?<design>[^\"]*)\" \"(?<id>[^\"]*)\" (?<primary>[01]) (?<active>[01])$",
        RegexOptions.None, TimeSpan.FromMilliseconds(30));

    // There is a limit of 4 change groups.
    private const int ChangeGroupGains = 1;
    private const int ChangeGroupMutes = 2;
    private const int ChangeGroupStrings = 3;
    private const int ChangeGroupMeters = 4;

    private readonly Dictionary<MuteState, string> _muteStateDictionary;

    private int _unansweredStatusRequests;
    private string? _designId;
    private bool _changeGroupsBuiltThisPoll;
    private bool _changeGroupsRefused;

    public QsysEcp(TcpClient tcpClient, string name = "Qsys", int pollIntervalInMilliseconds = DefaultPollIntervalInMilliseconds) : base(name, tcpClient, pollIntervalInMilliseconds)
    {
        _tcpClient = tcpClient;
        tcpClient.ResponseHandlers += HandleResponse;
        tcpClient.ConnectionStateHandlers += HandleConnectionState;

        string responsePattern = "cv\\s\"([^\"]+)\"\\s\"([^\"]*)\"\\s(-?\\d+(\\.\\d+)?)\\s(-?\\d+(\\.\\d+)?)";
        _responseParser = new Regex(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(30));
        _badIdParser = new Regex("bad_id\\s\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromMilliseconds(30));

        _muteStateDictionary = new Dictionary<MuteState, string>
        {
            { MuteState.On, "muted" },
            { MuteState.Off, "unmuted" }
        };

        HandleConnectionState(tcpClient.ConnectionState);
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (string received in response.Split('\n'))
            {
                string line = received.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                switch (line.Split(' ', 2)[0])
                {
                    case ControlValueReply:
                        ProcessValueChange(line);
                        CommunicationState = CommunicationState.Okay;
                        break;
                    case ControlVectorValueReply:
                        ProcessMeterChange(line);
                        break;
                    case StatusReply:
                        HandleStatus(line);
                        break;
                    case BadIdReply:
                        HandleBadId(line);
                        break;
                    case BadCommandReply:
                        RaiseMomentaryIssue($"The Core rejected a command as invalid: {line}",
                            BadCommandIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);
                        break;
                    case ControlReadOnlyReply:
                    case CoreReadOnlyReply:
                        RaiseMomentaryIssue($"The Core rejected a change to a read-only control: {line}",
                            ReadOnlyIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);
                        break;
                    case CoreNotActiveReply:
                        RaiseOngoingIssue(CoreStandbyIssueKey, $"{StandbyCoreMessage}: {line}", IssueSeverity.Critical);
                        break;
                    case BadChangeGroupReply:
                        HandleBadChangeGroup(line);
                        break;
                    case TooManyChangeGroupsReply:
                        HandleTooManyChangeGroups(line);
                        break;
                    case LoginRequiredReply:
                    case LoginFailedReply:
                        HandleLoginRequired(line);
                        break;
                    case LoginSuccessReply:
                        CommunicationState = CommunicationState.Okay;
                        break;
                    case ConnectionClosedReply:
                        HandleConnectionClosed(line);
                        break;
                    default:
                        LogWarning("Unrecognised reply: {Line}", line);
                        break;
                }
            }
        }
    }

    private void HandleStatus(string line)
    {
        Interlocked.Exchange(ref _unansweredStatusRequests, 0);
        ResolveIssue(UnansweredStatusIssueKey);
        ResolveIssue(BadCommandIssueKey);
        ResolveIssue(LoginIssueKey);
        if (!_changeGroupsRefused)
            ResolveIssue(ChangeGroupsIssueKey);
        CommunicationState = CommunicationState.Okay;

        var status = _statusParser.Match(line);
        if (!status.Success)
        {
            RaiseMomentaryIssue($"Unrecognised status reply: {line}", StatusParseIssueKey);
            return;
        }

        UpdateCoreRole(line, status.Groups["primary"].Value == StatusFlagSet, status.Groups["active"].Value == StatusFlagSet);
        UpdateDesign(status.Groups["design"].Value, status.Groups["id"].Value);
    }

    private void UpdateCoreRole(string line, bool isPrimary, bool isActive)
    {
        string role = isPrimary ? "Primary" : "Backup";
        string activity = isActive ? "active" : "standby";
        SetDetail(CoreLabel, $"{role}, {activity}", isActive ? DetailTone.Normal : DetailTone.Error);

        if (isActive)
            ResolveIssue(CoreStandbyIssueKey);
        else
            RaiseOngoingIssue(CoreStandbyIssueKey, $"{StandbyCoreMessage}: {line}", IssueSeverity.Critical);
    }

    private void UpdateDesign(string designName, string designId)
    {
        SetDetail(DesignLabel, designName);
        SetDetail(DesignIdLabel, designId);

        string? previousDesignId = _designId;
        _designId = designId;
        if (previousDesignId == null || previousDesignId == designId)
            return;

        AddEvent(EventType.Other, $"Design redeployed: {designName} ({designId})");
        RebuildChangeGroups($"design redeployed as {designId}");
    }

    private void HandleBadId(string line)
    {
        var badId = _badIdParser.Match(line);
        if (!badId.Success)
        {
            RaiseMomentaryIssue($"Invalid named control found: {line}");
            return;
        }

        string controlName = badId.Groups[1].Value;
        RaiseOngoingIssue($"{BadControlIssuePrefix}{controlName}", $"Invalid named control {controlName}: {line}");
        if (_missingControls.Add(controlName))
            ShowMissingControls();
    }

    private void ShowMissingControls()
    {
        if (_missingControls.Count == 0)
            RemoveDetail(MissingControlsLabel);
        else
            SetDetail(MissingControlsLabel, string.Join(", ", _missingControls.Order()), DetailTone.Warning);
    }

    private void HandleBadChangeGroup(string line)
    {
        RaiseMomentaryIssue($"The Core does not recognise a change group: {line}", BadChangeGroupIssueKey);
        RebuildChangeGroups(line);
    }

    private void HandleTooManyChangeGroups(string line)
    {
        _changeGroupsRefused = true;
        RaiseOngoingIssue(ChangeGroupsIssueKey, $"The Core refused to create a change group: {line}", IssueSeverity.Major);
    }

    private void HandleLoginRequired(string line)
    {
        Interlocked.Exchange(ref _unansweredStatusRequests, 0);
        RaiseOngoingIssue(LoginIssueKey, $"The Core needs an ECP login, which this driver does not support: {line}",
            IssueSeverity.Critical);
        CommunicationState = CommunicationState.Error;
    }

    private void HandleConnectionClosed(string line)
    {
        AddEvent(EventType.Connection, $"The Core is closing the connection: {line}");
        RaiseMomentaryIssue($"The Core closed the connection: {line}", ConnectionClosedIssueKey, IssueSeverity.Major);
    }

    private void ProcessMeterChange(string line)
    {
        var values = line.Split(' ');
        string controlName = values[1].TrimStart('"').TrimEnd('"');
        if (_meters.ContainsKey(controlName)) // Eg:cvv "rec_meter" 2 "-45.3dB" "-11.8dB" 2 -45.2769 -11.759 2 0.0736156 0.764821
        {
            _meters[controlName].SetVolumeFromPercentage(double.Parse(values[9]) * 100);
        }
    }

    private void ProcessValueChange(string response)
    {
        if (response.Length < 2)
            return;

        try
        {
            var matches = _responseParser.Matches(response);

            var controlName = matches[0].Groups[1].Value;
            ResolveIssue($"{BadControlIssuePrefix}{controlName}");
            if (_missingControls.Remove(controlName))
                ShowMissingControls();
            ResolveIssue(BadCommandIssueKey);
            ResolveIssue(ReadOnlyIssueKey);

            if (_gains.ContainsKey(controlName)) // Eg:cv "Zone 1 BGM Gain" "-6.40dB" -6.4 0.989744
                _gains[controlName].SetVolumeFromPercentage(double.Parse(matches[0].Groups[5].Value) * 100);

            if (_mutes.ContainsKey(controlName)) // Eg:cv "Zone 1 BGM Mute" "unmuted" 1 1
                _mutes[controlName].MuteState = FigureOutMuteState(matches[0].Groups[2].Value);

            if (_strings.ContainsKey(controlName)) // Eg:cv "Zone 1 BGM Select" "5 sfasdfa" 5 0.571429
                _strings[controlName].Value = matches[0].Groups[2].Value;
        }
        catch (Exception e)
        {
            LogException(e);
        }
    }

    private MuteState FigureOutMuteState(string value)
    {
        if (value.Contains("unmuted") || value.Contains("false"))
            return MuteState.Off;
        return MuteState.On;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        using (PushProperties("HandleConnectionState"))
        {
            AddEvent(EventType.Connection, connectionState.ToString());
            Interlocked.Exchange(ref _unansweredStatusRequests, 0);
            _designId = null;
            if (connectionState == ConnectionState.Connected)
            {
                BuildChangeGroups();
                GetAllControlStates();
            }
            else
            {
                CommunicationState = CommunicationClient.DesiredConnectionState == ConnectionState.Disconnected
                    ? CommunicationState.NotAttempted
                    : CommunicationState.Unknown;
            }
        }
    }

    private void RebuildChangeGroups(string reason)
    {
        using (PushProperties("RebuildChangeGroups"))
        {
            if (_changeGroupsBuiltThisPoll)
            {
                LogInformation("The change groups were already built this poll cycle, not rebuilding them for {Reason}", reason);
                return;
            }

            AddEvent(EventType.Other, $"Rebuilding the change groups: {reason}");
            BuildChangeGroups();
        }
    }

    private void BuildChangeGroups()
    {
        _changeGroupsBuiltThisPoll = true;
        _changeGroupsRefused = false;
        new Thread(_ =>
        {
            CreateEmptyChangeGroup(ChangeGroupGains);
            CreateEmptyChangeGroup(ChangeGroupMutes);
            CreateEmptyChangeGroup(ChangeGroupStrings);
            CreateEmptyChangeGroup(ChangeGroupMeters);
            Thread.Sleep(ChangeGroupSetupStepMs);

            AddControlsToChangeGroup(ChangeGroupGains, _gains.Keys.ToList());
            Thread.Sleep(ChangeGroupSetupStepMs);
            AddControlsToChangeGroup(ChangeGroupMutes, _mutes.Keys.ToList());
            Thread.Sleep(ChangeGroupSetupStepMs);
            AddControlsToChangeGroup(ChangeGroupStrings, _strings.Keys.ToList());
            Thread.Sleep(ChangeGroupSetupStepMs);
            AddControlsToChangeGroup(ChangeGroupMeters, _meters.Keys.ToList());
            Thread.Sleep(ChangeGroupSetupStepMs);

            ScheduleChangeGroupPoll(ChangeGroupGains);
            ScheduleChangeGroupPoll(ChangeGroupMutes);
            ScheduleChangeGroupPoll(ChangeGroupStrings);
            ScheduleChangeGroupPoll(ChangeGroupMeters, MeterPollPeriodMs);
        }).Start();
    }

    private void CreateEmptyChangeGroup(int changeGroupId)
    {
        // The Core silently ignores cgc for an id already in use on this connection.
        CommunicationClient.Send($"cgc {changeGroupId}\n");
        CommunicationClient.Send($"cgclr {changeGroupId}\n");
    }

    private void ScheduleChangeGroupPoll(int changeGroupId, uint pollPeriodInMs = ChangeGroupPollPeriodMs)
    {
        // The device only reports on a change
        CommunicationClient.Send($"cgsna {changeGroupId} {pollPeriodInMs}\n");
    }

    private void AddControlsToChangeGroup(int groupId, List<string> controlNames)
    {
        controlNames.ForEach(controlName => CommunicationClient.Send($"cga {groupId} \"{controlName}\"\n"));
    }

    protected override Task Poll(CancellationToken token)
    {
        using (PushProperties("Poll"))
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return Task.CompletedTask;

            _changeGroupsBuiltThisPoll = false;
            int unanswered = _unansweredStatusRequests;
            if (unanswered > 0)
                RaiseMomentaryIssue("The Core did not answer the status request", UnansweredStatusIssueKey,
                    IssueSeverity.Minor, EscalateAfterRepeats);

            if (unanswered >= MaxUnansweredStatusRequests)
            {
                ReconnectAfterUnansweredStatus();
                return Task.CompletedTask;
            }

            Interlocked.Increment(ref _unansweredStatusRequests);
            CommunicationClient.Send(StatusRequest);
        }
        return Task.CompletedTask;
    }

    private void ReconnectAfterUnansweredStatus()
    {
        using (PushProperties("ReconnectAfterUnansweredStatus"))
        {
            LogWarning("The Core has not answered {Count} status requests, reconnecting", MaxUnansweredStatusRequests);
            CommunicationState = CommunicationState.Error;
            Interlocked.Exchange(ref _unansweredStatusRequests, 0);
            _tcpClient.Reconnect();
        }
    }

    public void GetAllControlStates()
    {
        new Thread(_ =>
        {
            using (PushProperties("GetAllControlStates"))
            {
                try
                {
                    foreach (string key in _gains.Keys)
                    {
                        GetControl(key);
                        Thread.Sleep(100);
                    }

                    foreach (string key in _mutes.Keys)
                    {
                        GetControl(key);
                        Thread.Sleep(100);
                    }

                    foreach (string key in _strings.Keys)
                    {
                        GetControl(key);
                        Thread.Sleep(100);
                    }
                    
                    foreach (string key in _meters.Keys)
                    {
                        GetControl(key);
                        Thread.Sleep(100);
                    }
                }
                catch (Exception e)
                {
                    LogException(e);
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                    GetAllControlStates();
                }
            }
        }).Start();
    }

    private void GetControl(string controlName)
    {
        CommunicationClient.Send($"cg \"{controlName}\"\n");
    }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string levelName)
    {
        if (_gains.TryGetValue(levelName, out var gain))
            gain.VolumeLevelHandlers += volumeLevelHandler;
        else
            _gains.Add(levelName, new QscGain(volumeLevelHandler));

        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        if (gain == null)
            AddControlsToChangeGroup(ChangeGroupGains, [levelName]);
        GetControl(levelName);
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        if (_mutes.TryGetValue(muteName, out var mute))
            mute.MuteStateHandlers += muteStateHandler;
        else
            _mutes.Add(muteName, new QscMute(muteStateHandler));

        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        if (mute == null)
            AddControlsToChangeGroup(ChangeGroupMutes, [muteName]);
        GetControl(muteName);
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (_strings.TryGetValue(controlName, out var block))
            block.StringValueHandlers += stringValueHandler;
        else
            _strings.Add(controlName, new QscInt(stringValueHandler));

        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        if (block == null)
            AddControlsToChangeGroup(ChangeGroupStrings, [controlName]);
        GetControl(controlName);
    }

    public void AddMonitor(VolumeLevelHandler volumeLevelHandler, string levelName)
    {
        if (_meters.TryGetValue(levelName, out var meter))
            meter.VolumeLevelHandlers += volumeLevelHandler;
        else
            _meters.Add(levelName, new QscGain(volumeLevelHandler));

        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        if (meter == null)
            AddControlsToChangeGroup(ChangeGroupMeters, [levelName]);
        GetControl(levelName);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override void SetLevel(string gainName, int percentage)
    {
        CommunicationClient.Send($"csp \"{gainName}\" {Math.Round((double)percentage / 100, 2)}\n");
    }

    public override void LevelUp(string controlName, int amount = 1)
    {
        SetLevel(controlName, _gains[controlName].Volume + amount);
    }

    public override void LevelDown(string controlName, int amount = 1)
    {
        SetLevel(controlName, _gains[controlName].Volume - amount);
    }

    public override int GetLevel(string gainName)
    {
        if (!_gains.ContainsKey(gainName))
            return 0;
        return _gains[gainName].Volume;
    }

    public override void SetAudioMute(string muteName, MuteState state)
    {
        CommunicationClient.Send($"css \"{muteName}\" {_muteStateDictionary[state]}\n");
    }

    public override void ToggleAudioMute(string muteName)
    {
        switch (_mutes[muteName].MuteState)
        {
            case MuteState.On:
                SetAudioMute(muteName, MuteState.Off);
                break;
            case MuteState.Off:
            default:
                SetAudioMute(muteName, MuteState.On);
                break;
        }
    }

    public override MuteState GetAudioMute(string muteName)
    {
        if (!_mutes.ContainsKey(muteName))
            return MuteState.Unknown;
        return _mutes[muteName].MuteState;
    }

    public override void SetValue(string stringName, string value)
    {
        CommunicationClient.Send($"css \"{stringName}\" {value}\n");
    }

    public override string GetValue(string intName)
    {
        if (!_strings.ContainsKey(intName))
            return "";
        return _strings[intName].Value;
    }

    public override Task Reinitialise(CancellationToken token = default)
    {
        GetAllControlStates();
        return Task.CompletedTask;
    }

    [Obsolete("This method is deprecated in favor of TriggerPreset")]
    public void RecallPreset(string controlName) =>CommunicationClient.Send($"csv \"{controlName}\" 1 \n");
    
    public void TriggerPreset(string controlName) => CommunicationClient.Send($"ct \"{controlName}\"\n");
    
    public void RecallPreset(string controlName, int value, double rampTime = 0) 
        => CommunicationClient.Send($"ssl \"{controlName}\" {value} {rampTime}\n");
    
    public void RecallSnapshot(string controlName, int value, double rampTime = 0) => RecallPreset(controlName, value, rampTime);
}