using System.Net;
using System.Text.Json;
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
    public const ushort CoreManagerPort = 443;
    public const string CoreManagerProtocol = "https";
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
    public const string CoreManagerIssueKey = "core-manager";

    private const int EscalateAfterRepeats = 3;
    private const int ChangeGroupSetupStepMs = 500;
    private const uint ChangeGroupPollPeriodMs = 100;
    private const uint MeterPollPeriodMs = 1000;
    private const int ControlGetGapMs = 100;
    private const int MaxControlStateAttempts = 3;
    private const int MeterNameToken = 1;
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
    private const string FirmwareLabel = "Firmware";
    private const string SerialLabel = "Serial";
    private const string HardwareIdLabel = "Hardware ID";

    private const string CoreInfoPath = "/api/v0/cores/self";
    private const string LogonPath = "/api/v0/logon";
    private const string AuthorizationHeader = "Authorization";
    private const string AcceptHeader = "Accept";
    private const string JsonContentType = "application/json";
    private static readonly Uri CoreInfoUri = new(CoreInfoPath, UriKind.Relative);
    private static readonly Uri LogonUri = new(LogonPath, UriKind.Relative);

    private readonly TcpClient _tcpClient;
    private readonly RestComms? _coreManager;
    private readonly string? _coreManagerUsername;
    private readonly string _coreManagerPassword;
    private readonly Dictionary<string, QscGain> _gains = new();
    private readonly Dictionary<string, QscMute> _mutes = new();
    private readonly Dictionary<string, QscInt> _strings = new();
    private readonly Dictionary<string, QscGain> _meters = new();
    private readonly HashSet<string> _missingControls = new();
    private readonly object _controlsLock = new();
    private readonly Regex _responseParser;
    private readonly Regex _badIdParser;
    private readonly Regex _statusParser = new("^sr \"(?<design>[^\"]*)\" \"(?<id>[^\"]*)\" (?<primary>[01]) (?<active>[01])$",
        RegexOptions.None, TimeSpan.FromMilliseconds(30));
    private readonly Regex _meterTokenParser = new("\"(?<quoted>[^\"]*)\"|(?<bare>[^ ]+)",
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

    public TimeSpan ControlStatesRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public QsysEcp(TcpClient tcpClient, string name = "Qsys", int pollIntervalInMilliseconds = DefaultPollIntervalInMilliseconds,
        RestComms? coreManager = null, string? coreManagerUsername = null, string coreManagerPassword = "")
        : base(name, tcpClient, pollIntervalInMilliseconds)
    {
        _tcpClient = tcpClient;
        _coreManager = coreManager;
        _coreManagerUsername = coreManagerUsername;
        _coreManagerPassword = coreManagerPassword;
        tcpClient.ResponseHandlers += HandleResponse;
        tcpClient.ConnectionStateHandlers += HandleConnectionState;
        if (coreManager != null)
            coreManager.HttpResponseHandlers += HandleCoreManagerResponse;

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
        FetchCoreManagerInfo();
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
        bool added;
        lock (_controlsLock)
            added = _missingControls.Add(controlName);
        if (added)
            ShowMissingControls();
    }

    private void ShowMissingControls()
    {
        string missingControls;
        lock (_controlsLock)
            missingControls = string.Join(", ", _missingControls.Order());
        if (missingControls.Length == 0)
            RemoveDetail(MissingControlsLabel);
        else
            SetDetail(MissingControlsLabel, missingControls, DetailTone.Warning);
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
        var tokens = _meterTokenParser.Matches(line)
            .Select(token => token.Groups["quoted"].Success ? token.Groups["quoted"].Value : token.Groups["bare"].Value)
            .ToArray();
        var meter = FindControl(_meters, tokens[MeterNameToken]);
        if (meter == null)
            return;

        var stringCountToken = MeterNameToken + 1;
        var valueCountToken = stringCountToken + int.Parse(tokens[stringCountToken]) + 1;
        var positionCountToken = valueCountToken + int.Parse(tokens[valueCountToken]) + 1; // Eg:cvv "rec_meter" 2 "-45.3dB" "-11.8dB" 2 -45.2769 -11.759 2 0.0736156 0.764821
        meter.SetVolumeFromPercentage(double.Parse(tokens[positionCountToken + 1]) * 100);
    }

    private void ProcessValueChange(string response)
    {
        if (response.Length < 2)
            return;

        try
        {
            var match = _responseParser.Match(response);
            if (!match.Success)
            {
                LogWarning("Unparsed control value: {Line}", response);
                return;
            }

            var controlName = match.Groups[1].Value;
            ResolveIssue($"{BadControlIssuePrefix}{controlName}");
            bool wasMissing;
            QscGain? gain;
            QscMute? mute;
            QscInt? text;
            lock (_controlsLock)
            {
                wasMissing = _missingControls.Remove(controlName);
                gain = _gains.GetValueOrDefault(controlName);
                mute = _mutes.GetValueOrDefault(controlName);
                text = _strings.GetValueOrDefault(controlName);
            }
            if (wasMissing)
                ShowMissingControls();
            ResolveIssue(BadCommandIssueKey);
            ResolveIssue(ReadOnlyIssueKey);

            if (gain != null) // Eg:cv "Zone 1 BGM Gain" "-6.40dB" -6.4 0.989744
                gain.SetVolumeFromPercentage(double.Parse(match.Groups[5].Value) * 100);

            if (mute != null) // Eg:cv "Zone 1 BGM Mute" "unmuted" 1 1
                mute.MuteState = FigureOutMuteState(match.Groups[2].Value);

            if (text != null) // Eg:cv "Zone 1 BGM Select" "5 sfasdfa" 5 0.571429
                text.Value = match.Groups[2].Value;
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
                FetchCoreManagerInfo();
            }
            else
            {
                CommunicationState = CommunicationClient.DesiredConnectionState == ConnectionState.Disconnected
                    ? CommunicationState.NotAttempted
                    : CommunicationState.Unknown;
            }
        }
    }

    private void FetchCoreManagerInfo()
    {
        if (_coreManager == null)
            return;

        _coreManager.RemoveDefaultHeader(AuthorizationHeader);
        _coreManager.AddDefaultHeader(AcceptHeader, JsonContentType);
        _ = _coreManager.Get(CoreInfoUri);
    }

    private void HandleCoreManagerResponse(HttpResponseMessage response)
    {
        using (PushProperties())
        {
            switch (response.RequestMessage?.RequestUri?.AbsolutePath)
            {
                case CoreInfoPath:
                    HandleCoreInfo(response);
                    break;
                case LogonPath:
                    HandleLogon(response);
                    break;
            }
        }
    }

    private void HandleCoreInfo(HttpResponseMessage response)
    {
        string body = response.Content.ReadAsStringAsync().Result;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            HandleCoreInfoUnauthorised(response, body);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            RaiseOngoingIssue(CoreManagerIssueKey,
                $"Core Manager did not give the firmware and serial number: {DescribeReply(response, body)}", IssueSeverity.Minor);
            return;
        }

        if (ParseJsonObject(body) is not { } reply)
        {
            RaiseOngoingIssue(CoreManagerIssueKey,
                $"Core Manager's reply is not a JSON object: {DescribeReply(response, body)}", IssueSeverity.Minor);
            return;
        }

        ResolveIssue(CoreManagerIssueKey);
        ShowFirmware(reply, body);
        ShowSerial(reply, body);
        ShowHardwareId(reply, body);
    }

    private void HandleCoreInfoUnauthorised(HttpResponseMessage response, string body)
    {
        if (response.RequestMessage!.Headers.Contains(AuthorizationHeader))
        {
            RaiseOngoingIssue(CoreManagerIssueKey,
                $"Core Manager refused the token from the logon as {_coreManagerUsername}: {DescribeReply(response, body)}",
                IssueSeverity.Minor);
            return;
        }

        if (_coreManagerUsername == null)
        {
            RaiseOngoingIssue(CoreManagerIssueKey,
                $"Core Manager needs a login to give the firmware and serial number, and no username is set: {DescribeReply(response, body)}",
                IssueSeverity.Minor);
            return;
        }

        LogOnToCoreManager();
    }

    private void LogOnToCoreManager()
    {
        LogInformation("Logging on to Core Manager as {Username}", _coreManagerUsername);
        var credentials = JsonSerializer.Serialize(new { username = _coreManagerUsername, password = _coreManagerPassword });
        _ = _coreManager!.Post(LogonUri, credentials, JsonContentType);
    }

    private void HandleLogon(HttpResponseMessage response)
    {
        string body = response.Content.ReadAsStringAsync().Result;
        if (!response.IsSuccessStatusCode)
        {
            RaiseOngoingIssue(CoreManagerIssueKey,
                $"Core Manager rejected the logon as {_coreManagerUsername}: {DescribeReply(response, body)}", IssueSeverity.Minor);
            return;
        }

        if (ParseJsonObject(body) is not { } reply || !reply.TryGetProperty("token", out var token)
            || token.ValueKind != JsonValueKind.String)
        {
            RaiseOngoingIssue(CoreManagerIssueKey,
                $"Core Manager's logon reply as {_coreManagerUsername} has no token: {DescribeReply(response, body)}",
                IssueSeverity.Minor);
            return;
        }

        _coreManager!.AddDefaultHeader(AuthorizationHeader, $"Bearer {token.GetString()}");
        _ = _coreManager.Get(CoreInfoUri);
    }

    private void ShowFirmware(JsonElement reply, string body)
    {
        if (!reply.TryGetProperty("firmware", out var firmware))
        {
            LogWarning("Core Manager's reply has no firmware: {Body}", body);
            return;
        }
        SetDetail(FirmwareLabel, FirmwareText(firmware));
    }

    private void ShowSerial(JsonElement reply, string body)
    {
        if (!reply.TryGetProperty("serialNo", out var serial))
        {
            LogWarning("Core Manager's reply has no serialNo: {Body}", body);
            return;
        }
        SetDetail(SerialLabel, RawText(serial));
    }

    private void ShowHardwareId(JsonElement reply, string body)
    {
        if (!reply.TryGetProperty("hardwareId", out var hardwareId) && !reply.TryGetProperty("serial", out hardwareId))
        {
            LogWarning("Core Manager's reply has no hardwareId or serial: {Body}", body);
            return;
        }
        SetDetail(HardwareIdLabel, RawText(hardwareId));
    }

    private static string FirmwareText(JsonElement firmware) =>
        firmware.ValueKind == JsonValueKind.Object
        && firmware.TryGetProperty("name", out var name) && firmware.TryGetProperty("build", out var build)
            ? $"{RawText(name)}-{RawText(build)}"
            : RawText(firmware);

    private static string RawText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static JsonElement? ParseJsonObject(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeReply(HttpResponseMessage response, string body) =>
        $"{response.RequestMessage!.Method} {response.RequestMessage.RequestUri} answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}, body: {body}";

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

            AddControlsToChangeGroup(ChangeGroupGains, ControlNames(_gains));
            Thread.Sleep(ChangeGroupSetupStepMs);
            AddControlsToChangeGroup(ChangeGroupMutes, ControlNames(_mutes));
            Thread.Sleep(ChangeGroupSetupStepMs);
            AddControlsToChangeGroup(ChangeGroupStrings, ControlNames(_strings));
            Thread.Sleep(ChangeGroupSetupStepMs);
            AddControlsToChangeGroup(ChangeGroupMeters, ControlNames(_meters));
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
        new Thread(_ => GetAllControlStatesWithRetries()).Start();
    }

    private void GetAllControlStatesWithRetries()
    {
        using (PushProperties("GetAllControlStates"))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    GetEachControlState();
                    return;
                }
                catch (Exception e)
                {
                    LogException(e);
                }
                if (attempt == MaxControlStateAttempts)
                {
                    LogError("Gave up getting the control states after {Attempts} attempts", attempt);
                    return;
                }
                Thread.Sleep(ControlStatesRetryDelay);
            }
        }
    }

    private void GetEachControlState()
    {
        List<string> controlNames;
        lock (_controlsLock)
            controlNames = [.._gains.Keys, .._mutes.Keys, .._strings.Keys, .._meters.Keys];

        foreach (string controlName in controlNames)
        {
            GetControl(controlName);
            Thread.Sleep(ControlGetGapMs);
        }
    }

    private List<string> ControlNames<T>(Dictionary<string, T> controls)
    {
        lock (_controlsLock)
            return controls.Keys.ToList();
    }

    private T? FindControl<T>(Dictionary<string, T> controls, string controlName) where T : class
    {
        lock (_controlsLock)
            return controls.GetValueOrDefault(controlName);
    }

    private void GetControl(string controlName)
    {
        CommunicationClient.Send($"cg \"{controlName}\"\n");
    }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string levelName)
    {
        QscGain? gain;
        lock (_controlsLock)
        {
            if (_gains.TryGetValue(levelName, out gain))
                gain.VolumeLevelHandlers += volumeLevelHandler;
            else
                _gains.Add(levelName, new QscGain(volumeLevelHandler));
        }
        if (gain != null)
            volumeLevelHandler.Invoke(gain.Volume);

        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        if (gain == null)
            AddControlsToChangeGroup(ChangeGroupGains, [levelName]);
        GetControl(levelName);
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        QscMute? mute;
        lock (_controlsLock)
        {
            if (_mutes.TryGetValue(muteName, out mute))
                mute.MuteStateHandlers += muteStateHandler;
            else
                _mutes.Add(muteName, new QscMute(muteStateHandler));
        }
        if (mute != null)
            muteStateHandler.Invoke(mute.MuteState);

        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        if (mute == null)
            AddControlsToChangeGroup(ChangeGroupMutes, [muteName]);
        GetControl(muteName);
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        QscInt? block;
        lock (_controlsLock)
        {
            if (_strings.TryGetValue(controlName, out block))
                block.StringValueHandlers += stringValueHandler;
            else
                _strings.Add(controlName, new QscInt(stringValueHandler));
        }
        if (block != null)
            stringValueHandler.Invoke(block.Value);

        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        if (block == null)
            AddControlsToChangeGroup(ChangeGroupStrings, [controlName]);
        GetControl(controlName);
    }

    public void AddMonitor(VolumeLevelHandler volumeLevelHandler, string levelName)
    {
        QscGain? meter;
        lock (_controlsLock)
        {
            if (_meters.TryGetValue(levelName, out meter))
                meter.VolumeLevelHandlers += volumeLevelHandler;
            else
                _meters.Add(levelName, new QscGain(volumeLevelHandler));
        }
        if (meter != null)
            volumeLevelHandler.Invoke(meter.Volume);

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
        var gain = FindControl(_gains, controlName);
        if (gain == null)
        {
            using (PushProperties("LevelUp"))
                LogWarning("Unknown level {ControlName}, not raising it by {Amount}", controlName, amount);
            return;
        }
        SetLevel(controlName, gain.Volume + amount);
    }

    public override void LevelDown(string controlName, int amount = 1)
    {
        var gain = FindControl(_gains, controlName);
        if (gain == null)
        {
            using (PushProperties("LevelDown"))
                LogWarning("Unknown level {ControlName}, not lowering it by {Amount}", controlName, amount);
            return;
        }
        SetLevel(controlName, gain.Volume - amount);
    }

    public override int GetLevel(string gainName)
    {
        var gain = FindControl(_gains, gainName);
        if (gain == null)
        {
            using (PushProperties("GetLevel"))
                LogWarning("Unknown level {ControlName}, returning 0", gainName);
            return 0;
        }
        return gain.Volume;
    }

    public override void SetAudioMute(string muteName, MuteState state)
    {
        CommunicationClient.Send($"css \"{muteName}\" {_muteStateDictionary[state]}\n");
    }

    public override void ToggleAudioMute(string muteName)
    {
        var mute = FindControl(_mutes, muteName);
        if (mute == null)
        {
            using (PushProperties("ToggleAudioMute"))
                LogWarning("Unknown mute {ControlName}, not toggling it", muteName);
            return;
        }
        switch (mute.MuteState)
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
        var mute = FindControl(_mutes, muteName);
        if (mute == null)
        {
            using (PushProperties("GetAudioMute"))
                LogWarning("Unknown mute {ControlName}, returning Unknown", muteName);
            return MuteState.Unknown;
        }
        return mute.MuteState;
    }

    public override void SetValue(string stringName, string value)
    {
        CommunicationClient.Send($"css \"{stringName}\" {value}\n");
    }

    public override string GetValue(string intName)
    {
        var block = FindControl(_strings, intName);
        if (block == null)
        {
            using (PushProperties("GetValue"))
                LogWarning("Unknown string {ControlName}, returning an empty value", intName);
            return "";
        }
        return block.Value;
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