using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Display;

public enum PjLinkErrorState
{
    Normal,
    Warning,
    Error
}

public record PjLinkErrorStatus(
    PjLinkErrorState Fan,
    PjLinkErrorState Lamp,
    PjLinkErrorState Temperature,
    PjLinkErrorState CoverOpen,
    PjLinkErrorState Filter,
    PjLinkErrorState Other)
{
    public IEnumerable<(string Component, PjLinkErrorState State)> Components =>
    [
        ("Fan", Fan), ("Lamp", Lamp), ("Temperature", Temperature),
        ("Cover", CoverOpen), ("Filter", Filter), ("Other", Other)
    ];

    public bool HasError => Components.Any(x => x.State == PjLinkErrorState.Error);
    public bool HasWarning => Components.Any(x => x.State == PjLinkErrorState.Warning);
}

public record PjLinkLamp(int Hours, bool IsOn);

public class PjLink : Display
{
    public static readonly ushort DefaultPort = 4352;
    public const string DefaultPassword = "JBMIAProjectorLink";
    public const string HardwareIssueKey = "hardware";
    public const string InputMapIssueKey = "input-map";
    public const string BannerIssueKey = "banner";
    public const string PasswordIssueKey = "password";
    public const int MissedBannerPollLimit = 2;
    public const int MissedBannerReconnectLimit = 3;
    public const int MaxInputNameRetryTicks = 8;

    public static readonly Dictionary<Input, int> DefaultInputMap = new ()
    {
        { Input.Hdmi1, 31 },
        { Input.Hdmi2, 32 },
        { Input.Hdmi3, 33 },
        { Input.Hdmi4, 34 },
        { Input.Network6, 56 }
    };

    private static readonly Dictionary<PowerState, int> PowerStateDictionary = new ()
    {
        { PowerState.Off, 0 },
        { PowerState.On, 1 },
        { PowerState.Cooling, 2 },
        { PowerState.Warming, 3 }
    };

    private static readonly string[] DiscoveryCommands = ["CLSS", "INF1", "INF2", "INFO", "NAME", "INST"];
    private static readonly string[] Class2DiscoveryCommands = ["SNUM", "SVER", "RLMP", "RFIL", "RRES"];
    private static readonly string[] OptionalSetCommands = ["FREZ", "SVOL", "MVOL"];
    private static readonly string[] CoreCommands = ["POWR", "INPT", "AVMT"];

    private enum PollTask
    {
        ErrorStatus,
        Lamp,
        FilterUsage,
        InputResolution,
        Freeze
    }

    private static readonly PollTask[] SecondaryPollSequence =
    [
        PollTask.ErrorStatus, PollTask.Lamp, PollTask.FilterUsage, PollTask.InputResolution, PollTask.Freeze
    ];

    private const int MaxGatherLength = 4096;
    private const string InputResolutionLabel = "Input Resolution";
    private const string FrozenLabel = "Frozen";
    private const string UnmappedInputsLabel = "Unmapped Inputs";

    private readonly TcpClient _tcpClient;
    private readonly string _password;
    private readonly Dictionary<Input, int>? _explicitInputMap;
    private readonly object _stateLock = new();
    private readonly HashSet<string> _unsupportedCommands = new();
    private readonly HashSet<string> _failingCommands = new();
    private readonly HashSet<string> _answeredDiscovery = new();
    private readonly Dictionary<string, bool> _lastRequestWasQuery = new();
    private readonly Queue<int> _pendingInputNameQueries = new();
    private readonly Dictionary<int, string> _inputNames = new();
    private Dictionary<Input, int> _inputMap;
    private List<int> _unmappedInputCodes = [];
    private List<string> _unrecognisedInputCodes = [];
    private List<string> _lampDetailLabels = [];
    private string _gather = string.Empty;
    private int _pollIndex;
    private bool _authenticated;
    private bool _authRejected;
    private bool _passwordRejected;
    private int _pollsWithoutBanner;
    private int _missedBannerReconnects;
    private bool _class2InformationRequested;
    private bool _instReceivedThisConnection;
    private bool _inputNamesRequested;
    private bool _inputListDirty;
    private bool _inputNamesIncomplete;
    private int _inputNameRetryDelay = 1;
    private int _inputNameRetryCountdown;
    private int? _inFlightInputNameCode;
    private string? _parseFailedCommand;
    private DateTime _lastDiscoverySentAt = DateTime.MinValue;
    private string _manufacturerName = string.Empty;
    private string _productName = string.Empty;
    private string _otherInformation = string.Empty;
    private string _projectorName = string.Empty;
    private string _serialNumber = string.Empty;
    private string _softwareVersion = string.Empty;
    private string _lampReplacementModelNumber = string.Empty;
    private string _filterReplacementModelNumber = string.Empty;
    private string _recommendedResolution = string.Empty;

    public string ManufacturerName => _manufacturerName;
    public string ProductName => _productName;
    public string OtherInformation => _otherInformation;
    public string ProjectorName => _projectorName;
    public int PjLinkClass { get; private set; } = 1;
    public string SerialNumber => _serialNumber;
    public string SoftwareVersion => _softwareVersion;
    public string LampReplacementModelNumber => _lampReplacementModelNumber;
    public string FilterReplacementModelNumber => _filterReplacementModelNumber;
    public string RecommendedResolution => _recommendedResolution;
    public string InputResolution { get; private set; } = string.Empty;
    public int FilterUsageHours { get; private set; }
    public bool FreezeActive { get; private set; }
    public IReadOnlyList<PjLinkLamp> Lamps { get; private set; } = [];
    public PjLinkErrorStatus? ErrorStatus { get; private set; }
    public IReadOnlyList<int> AvailableInputCodes { get; private set; } = [];
    public IReadOnlyList<int> UnmappedInputCodes => _unmappedInputCodes;
    public IReadOnlyList<string> UnrecognisedInputCodes => _unrecognisedInputCodes;
    public TimeSpan DiscoveryRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    public IReadOnlyDictionary<int, string> InputNames
    {
        get { lock (_stateLock) return new Dictionary<int, string>(_inputNames); }
    }

    public IReadOnlySet<string> UnsupportedCommands
    {
        get { lock (_stateLock) return _unsupportedCommands.ToHashSet(); }
    }

    public bool DiscoveryComplete
    {
        get { lock (_stateLock) return IsDiscoveryDone("CLSS") && IsDiscoveryDone("INST"); }
    }

    public event Action? OnDeviceInformationChanged;
    public event Action<PjLinkErrorStatus>? OnErrorStatusChanged;
    public event Action<IReadOnlyList<PjLinkLamp>>? OnLampsChanged;
    public event Action<int>? OnFilterUsageChanged;
    public event Action<string>? OnInputResolutionChanged;
    public event Action<bool>? OnFreezeChanged;

    public PjLink(TcpClient tcpClient, string name, Input? defaultInput, string password = DefaultPassword,
        Dictionary<Input, int>? inputMap = null)
        : base((inputMap ?? DefaultInputMap).Keys.ToList(), name, defaultInput, tcpClient, CommandStringFormat.Ascii)
    {
        _tcpClient = tcpClient;
        _password = password;
        _explicitInputMap = inputMap;
        _inputMap = new Dictionary<Input, int>(inputMap ?? DefaultInputMap);
        DesiredAudioMute = MuteState.Unknown;
        DesiredVideoMute = MuteState.Unknown;

        CommunicationClient.ResponseHandlers += HandleResponse;

        // The socket may already be open, in which case the banner was delivered before we subscribed.
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            _tcpClient.Reconnect();
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        lock (_stateLock)
        {
            _authenticated = false;
            _authRejected = false;
            _pollsWithoutBanner = 0;
            _gather = string.Empty;
            _class2InformationRequested = false;
            _instReceivedThisConnection = false;
            _inputNamesRequested = false;
            _inputListDirty = false;
            _pendingInputNameQueries.Clear();
            _inFlightInputNameCode = null;
            _unsupportedCommands.Clear();
            _answeredDiscovery.Clear();
            _lastRequestWasQuery.Clear();
            _lastDiscoverySentAt = DateTime.MinValue;
            _inputNamesIncomplete = false;
            _inputNameRetryDelay = 1;
            _inputNameRetryCountdown = 0;
        }
        if (connectionState == ConnectionState.Connected)
            RestartPolling();
    }

    protected override Task DoPoll(CancellationToken token)
    {
        using (PushProperties("DoPoll"))
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            {
                LogDebug("Not polling");
                return Task.CompletedTask;
            }

            if (_authRejected)
            {
                LogDebug("Password rejected, waiting for a new banner");
                return Task.CompletedTask;
            }

            if (!_authenticated)
            {
                HandleMissingBanner();
                return Task.CompletedTask;
            }

            QueryMissingDiscovery();
            RetryIncompleteInputNames();

            Send("POWR ?");
            Send("INPT ?");
            Send("AVMT ?");

            for (int i = 0; i < SecondaryPollSequence.Length; i++)
            {
                PollTask task = SecondaryPollSequence[_pollIndex];
                _pollIndex = (_pollIndex + 1) % SecondaryPollSequence.Length;
                if (!IsPollApplicable(task))
                    continue;
                PollProjector(task);
                break;
            }
        }
        return Task.CompletedTask;
    }

    private void HandleMissingBanner()
    {
        _pollsWithoutBanner++;
        if (_pollsWithoutBanner < MissedBannerPollLimit)
        {
            LogDebug("Waiting for the PJLink banner before polling");
            return;
        }
        _pollsWithoutBanner = 0;
        _missedBannerReconnects++;
        if (_missedBannerReconnects >= MissedBannerReconnectLimit)
            RaiseOngoingIssue(BannerIssueKey, "No PJLink banner received from the projector", IssueSeverity.Critical);
        LogWarning("No PJLink banner received, reconnecting");
        _tcpClient.Reconnect();
    }

    private bool IsUnsupported(string command)
    {
        lock (_stateLock) return _unsupportedCommands.Contains(command);
    }

    private bool IsPollApplicable(PollTask task) => task switch
    {
        PollTask.ErrorStatus => !IsUnsupported("ERST"),
        PollTask.Lamp => !IsUnsupported("LAMP"),
        PollTask.FilterUsage => PjLinkClass >= 2 && !IsUnsupported("FILT"),
        PollTask.InputResolution => PjLinkClass >= 2 && PowerState == PowerState.On && !IsUnsupported("IRES"),
        PollTask.Freeze => PjLinkClass >= 2 && PowerState == PowerState.On && !IsUnsupported("FREZ"),
        _ => true
    };

    private void PollProjector(PollTask pollTask)
    {
        switch (pollTask)
        {
            case PollTask.ErrorStatus:
                Send("ERST ?");
                break;
            case PollTask.Lamp:
                Send("LAMP ?");
                break;
            case PollTask.FilterUsage:
                SendClass2("FILT ?");
                break;
            case PollTask.InputResolution:
                SendClass2("IRES ?");
                break;
            case PollTask.Freeze:
                SendClass2("FREZ ?");
                break;
        }
    }

    public void QueryDeviceInformation()
    {
        lock (_stateLock) _lastDiscoverySentAt = DateTime.UtcNow;
        foreach (string command in DiscoveryCommands)
            Send($"{command} ?");
        if (PjLinkClass >= 2)
            QueryClass2Information();
    }

    private void QueryMissingDiscovery()
    {
        List<string> missing;
        List<string> missingClass2;
        lock (_stateLock)
        {
            if (DateTime.UtcNow - _lastDiscoverySentAt < DiscoveryRetryInterval)
                return;
            missing = DiscoveryCommands.Where(c => !IsDiscoveryDone(c)).ToList();
            missingClass2 = PjLinkClass >= 2 ? Class2DiscoveryCommands.Where(c => !IsDiscoveryDone(c)).ToList() : [];
            if (missing.Count > 0 || missingClass2.Count > 0)
                _lastDiscoverySentAt = DateTime.UtcNow;
        }
        foreach (string command in missing)
            Send($"{command} ?");
        foreach (string command in missingClass2)
            SendClass2($"{command} ?");
    }

    // An incomplete name pass is retried straight away while the projector is on (INNM only fails in
    // transitions then) and with a growing back-off otherwise, so a vendor that rejects INNM in
    // standby is not asked nine questions every tick.
    private void RetryIncompleteInputNames()
    {
        lock (_stateLock)
        {
            if (!_inputNamesIncomplete || _inFlightInputNameCode != null)
                return;
            if (PowerState != PowerState.On)
            {
                if (--_inputNameRetryCountdown > 0)
                    return;
                _inputNameRetryDelay = Math.Min(_inputNameRetryDelay * 2, MaxInputNameRetryTicks);
                _inputNameRetryCountdown = _inputNameRetryDelay;
            }
            _inputNamesIncomplete = false;
            _inputNamesRequested = false;
        }
        RequestInputNames();
    }

    // Must be called under _stateLock.
    private bool IsDiscoveryDone(string command) =>
        _answeredDiscovery.Contains(command) || _unsupportedCommands.Contains(command);

    private void QueryClass2Information()
    {
        _class2InformationRequested = true;
        SendClass2("SNUM ?");
        SendClass2("SVER ?");
        SendClass2("RLMP ?");
        SendClass2("RFIL ?");
        SendClass2("RRES ?");
        SendClass2("FILT ?");
    }

    private void QueryInitialStatus()
    {
        Send("INPT ?");
        Send("AVMT ?");
        Send("ERST ?");
        Send("LAMP ?");
    }

    public void QueryStatus()
    {
        Send("POWR ?");
        Send("INPT ?");
        Send("AVMT ?");
        Send("ERST ?");
        Send("LAMP ?");
        if (PjLinkClass < 2)
            return;
        SendClass2("FILT ?");
        SendClass2("IRES ?");
        SendClass2("FREZ ?");
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            _gather += response;

            int delimiterIndex;
            while ((delimiterIndex = _gather.IndexOf('\r')) >= 0)
            {
                string frame = _gather.Substring(0, delimiterIndex);
                _gather = _gather.Substring(delimiterIndex + 1);
                if (frame.Length > 0)
                    ProcessFrame(frame);
            }

            if (_gather.Length > MaxGatherLength)
            {
                LogWarning("Discarding {Length} buffered bytes with no message terminator", _gather.Length);
                _gather = string.Empty;
            }
        }
    }

    private void ProcessFrame(string response)
    {
        using (PushProperties())
        {
            if (response.StartsWith("PJLINK", StringComparison.OrdinalIgnoreCase))
            {
                HandleBanner(response);
                return;
            }

            string[] parts = response.Split('=', 2);
            if (parts.Length < 2)
            {
                RaiseMomentaryIssue($"Unable to parse response: {response}", "parse-frame", IssueSeverity.Minor, 5);
                return;
            }
            ResolveIssue("parse-frame");

            string field = parts[0].Trim();
            string command = field.Length >= 4 ? field.Substring(field.Length - 4).ToUpperInvariant() : field;
            string value = parts[1].Trim();

            switch (value)
            {
                case "OK":
                    OnAcceptedAnswer();
                    ClearCommandIssues(command);
                    return;
                case "ERR1":
                    HandleUnsupported(command);
                    OnCommandRejected(command, value);
                    return;
                case "ERR2":
                    RaiseMomentaryIssue($"{command} was sent an out-of-range parameter", $"err2-{command}", IssueSeverity.Minor, 3);
                    OnCommandRejected(command, value);
                    return;
                case "ERR3": // Not the right time for this command, e.g. the projector is off
                    OnCommandRejected(command, value);
                    return;
                case "ERR4":
                    lock (_stateLock) _failingCommands.Add(command);
                    RaiseMomentaryIssue($"Projector reported a failure for {command}", FailureIssueKey(command), IssueSeverity.Major, 3);
                    OnCommandRejected(command, value);
                    return;
                case "ERRA":
                    HandlePasswordRejected();
                    return;
            }

            lock (_stateLock)
            {
                if (DiscoveryCommands.Contains(command) || Class2DiscoveryCommands.Contains(command))
                    _answeredDiscovery.Add(command);
            }
            OnAcceptedAnswer();
            ClearCommandIssues(command);
            _parseFailedCommand = null;

            switch (command)
            {
                case "POWR":
                    HandlePower(value);
                    break;
                case "INPT":
                    HandleInput(value);
                    break;
                case "AVMT":
                    HandleAvMute(value);
                    break;
                case "ERST":
                    HandleErrorStatus(value);
                    break;
                case "LAMP":
                    HandleLamp(value);
                    break;
                case "INST":
                    HandleInputList(value);
                    break;
                case "INNM":
                    AdvanceInputNames(value);
                    break;
                case "CLSS":
                    HandleClass(value);
                    break;
                case "NAME":
                    UpdateInformation(ref _projectorName, value, "Name");
                    break;
                case "INF1":
                    UpdateInformation(ref _manufacturerName, value, "Manufacturer");
                    break;
                case "INF2":
                    UpdateInformation(ref _productName, value, "Model");
                    break;
                case "INFO":
                    UpdateInformation(ref _otherInformation, value, "Other Information");
                    break;
                case "SNUM":
                    UpdateInformation(ref _serialNumber, value, "Serial Number");
                    break;
                case "SVER":
                    UpdateInformation(ref _softwareVersion, value, "Software Version");
                    break;
                case "RLMP":
                    UpdateInformation(ref _lampReplacementModelNumber, value, "Lamp Part Number");
                    break;
                case "RFIL":
                    UpdateInformation(ref _filterReplacementModelNumber, value, "Filter Part Number");
                    break;
                case "RRES":
                    UpdateInformation(ref _recommendedResolution, value, "Recommended Resolution");
                    break;
                case "IRES":
                    SetInputResolution(value);
                    break;
                case "FILT":
                    if (!int.TryParse(value, out int filterHours))
                    {
                        RaiseParseIssue(command, value);
                        break;
                    }
                    SetDetail("Filter Usage", $"{filterHours} h");
                    if (FilterUsageHours != filterHours)
                    {
                        FilterUsageHours = filterHours;
                        OnFilterUsageChanged?.Invoke(filterHours);
                    }
                    break;
                case "FREZ":
                    SetFreezeState(value == "1");
                    break;
                default:
                    LogDebug("Unhandled response {Command}={Value}", command, value);
                    break;
            }

            if (_parseFailedCommand != command)
                ResolveIssue($"parse-{command}");
            CommunicationState = CommunicationState.Okay;
        }
    }

    // Any answer other than ERRA proves the password.
    private void OnAcceptedAnswer()
    {
        CommunicationState = CommunicationState.Okay;
        bool rejected;
        lock (_stateLock)
        {
            rejected = _passwordRejected;
            _passwordRejected = false;
        }
        if (rejected)
            ResolveIssue(PasswordIssueKey);
    }

    private void HandlePasswordRejected()
    {
        lock (_stateLock)
        {
            _authRejected = true;
            _passwordRejected = true;
            _pollsWithoutBanner = 0;
            _missedBannerReconnects = 0;
        }
        LogError("Password not accepted");
        RaiseOngoingIssue(PasswordIssueKey, "PJLink password rejected", IssueSeverity.Critical);
    }

    // ERR1 to a query (or to one of the optional class-2 sets) means the projector lacks the command;
    // ERR1 to a POWR/INPT/AVMT set is just a rejected request and must not silence the poll.
    private void HandleUnsupported(string command)
    {
        bool eligible;
        bool added = false;
        lock (_stateLock)
        {
            bool wasQuery = _lastRequestWasQuery.GetValueOrDefault(command, true);
            // POWR/INPT/AVMT are mandatory in every class, so an ERR1 there is a rejected request, never a missing command.
            eligible = !CoreCommands.Contains(command) && (wasQuery || OptionalSetCommands.Contains(command));
            if (eligible)
                added = _unsupportedCommands.Add(command);
        }
        if (added)
        {
            LogInformation("{Command} is not supported by this projector, it will not be polled again", command);
            return;
        }
        if (!eligible)
            RaiseMomentaryIssue($"{command} was rejected as unsupported", $"err1-{command}", IssueSeverity.Minor, 3);
    }

    private void UpdateInformation(ref string field, string value, string label)
    {
        if (field == value)
            return;
        field = value;
        if (string.IsNullOrWhiteSpace(value))
            RemoveDetail(label);
        else
            SetDetail(label, value);
        OnDeviceInformationChanged?.Invoke();
    }

    private static string FailureIssueKey(string command) => $"projector-failure-{command}";

    private void ClearCommandIssues(string command)
    {
        bool wasFailing;
        lock (_stateLock) wasFailing = _failingCommands.Remove(command);
        if (wasFailing)
            ResolveIssue(FailureIssueKey(command));
        ResolveIssue($"err2-{command}");
        ResolveIssue($"err1-{command}");
    }

    private void RaiseParseIssue(string command, string value)
    {
        _parseFailedCommand = command;
        RaiseMomentaryIssue($"Unable to parse {command} value {value}", $"parse-{command}", IssueSeverity.Minor, 5);
    }

    private void OnCommandRejected(string command, string error)
    {
        OnAcceptedAnswer();
        switch (command)
        {
            case "INNM":
                if (error == "ERR3")
                    MarkInputNamesIncomplete();
                AdvanceInputNames(null);
                break;
            case "IRES":
                ClearInputResolution();
                break;
            case "FREZ":
                ClearFreeze();
                break;
        }
    }

    private void MarkInputNamesIncomplete()
    {
        lock (_stateLock)
        {
            if (_inputNamesIncomplete)
                return;
            _inputNamesIncomplete = true;
            _inputNameRetryCountdown = _inputNameRetryDelay;
        }
    }

    private void HandleBanner(string response)
    {
        string[] loginParams = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (loginParams.Length < 2)
        {
            CommunicationState = CommunicationState.Error;
            LogError("Malformed PJLink banner: {Banner}", response);
            return;
        }

        switch (loginParams[1])
        {
            case "ERRA":
                HandlePasswordRejected();
                return;
            case "0":
                MarkAuthenticated();
                Send("POWR ?");
                QueryDeviceInformation();
                QueryInitialStatus();
                return;
            case "1":
                if (loginParams.Length < 3)
                {
                    CommunicationState = CommunicationState.Error;
                    LogError("PJLink authentication banner is missing the random seed: {Banner}", response);
                    return;
                }
                MarkAuthenticated();
                SendAuthenticatedPowerPoll(loginParams[2]);
                QueryDeviceInformation();
                QueryInitialStatus();
                return;
            default:
                CommunicationState = CommunicationState.Error;
                LogError("Unexpected PJLink banner: {Banner}", response);
                return;
        }
    }

    // CommunicationState is left alone here: the first non-ERRA answer proves the password.
    private void MarkAuthenticated()
    {
        lock (_stateLock)
        {
            _authenticated = true;
            _authRejected = false;
            _pollsWithoutBanner = 0;
            _missedBannerReconnects = 0;
        }
        ResolveIssue(BannerIssueKey);
    }

    private void SendAuthenticatedPowerPoll(string seed)
    {
        byte[] answer = GetMd5Hash(seed + _password);
        byte[] poll = Bytes.FromString("%1POWR ?\r");
        byte[] combined = new byte[answer.Length + poll.Length];

        Buffer.BlockCopy(answer, 0, combined, 0, answer.Length);
        Buffer.BlockCopy(poll, 0, combined, answer.Length, poll.Length);

        lock (_stateLock) _lastRequestWasQuery["POWR"] = true;
        CommunicationClient.Send(combined);
    }

    private void HandlePower(string value)
    {
        if (!int.TryParse(value, out int code))
        {
            RaiseParseIssue("POWR", value);
            return;
        }
        PowerState = PowerStateDictionary.FirstOrDefault(x => x.Value == code).Key;
        if (PowerState != PowerState.On)
        {
            ClearFreeze();
            ClearInputResolution();
        }
        ProcessPowerResponse();
    }

    private void HandleInput(string value)
    {
        var map = _inputMap;
        // Class 2 allows alphanumeric input numbers ("3A"); those can't be in the map, so they read as Unknown.
        Input = int.TryParse(value, out int code) ? map.FirstOrDefault(x => x.Value == code).Key : Input.Unknown;
        ProcessInputResponse();
    }

    private void HandleAvMute(string value)
    {
        if (!int.TryParse(value, out int code))
        {
            RaiseParseIssue("AVMT", value);
            return;
        }
        switch (code)
        {
            case 11:
                VideoMute = MuteState.On;
                AudioMute = MuteState.Off;
                break;
            case 21:
                VideoMute = MuteState.Off;
                AudioMute = MuteState.On;
                break;
            case 30:
                VideoMute = MuteState.Off;
                AudioMute = MuteState.Off;
                break;
            case 31:
                VideoMute = MuteState.On;
                AudioMute = MuteState.On;
                break;
            default:
                return;
        }

        // Only re-assert a mute the operator has actually asked for; a mute set from the remote stays.
        bool audioWrong = DesiredAudioMute != MuteState.Unknown && AudioMute != DesiredAudioMute;
        bool videoWrong = DesiredVideoMute != MuteState.Unknown && VideoMute != DesiredVideoMute;
        if (audioWrong || videoWrong)
            SendMuteState();
    }

    private void HandleErrorStatus(string value)
    {
        if (value.Length < 6)
        {
            RaiseParseIssue("ERST", value);
            return;
        }

        var status = new PjLinkErrorStatus(
            ParseErrorState(value[0]), ParseErrorState(value[1]), ParseErrorState(value[2]),
            ParseErrorState(value[3]), ParseErrorState(value[4]), ParseErrorState(value[5]));

        if (status == ErrorStatus)
            return;
        ErrorStatus = status;
        OnErrorStatusChanged?.Invoke(status);

        if (status.HasError || status.HasWarning)
        {
            string detail = string.Join(", ", status.Components
                .Where(x => x.State != PjLinkErrorState.Normal)
                .Select(x => $"{x.Component} {x.State.ToString().ToLowerInvariant()}"));
            SetDetail("Errors", detail, status.HasError ? DetailTone.Error : DetailTone.Warning);
            RaiseOngoingIssue(HardwareIssueKey, $"Projector reports: {detail}",
                status.HasError ? IssueSeverity.Critical : IssueSeverity.Minor);
            AddEvent(EventType.Error, detail);
        }
        else
        {
            SetDetail("Errors", "None");
            ResolveIssue(HardwareIssueKey);
        }
    }

    private static PjLinkErrorState ParseErrorState(char c) => c switch
    {
        '1' => PjLinkErrorState.Warning,
        '2' => PjLinkErrorState.Error,
        _ => PjLinkErrorState.Normal
    };

    private void HandleLamp(string value)
    {
        string[] tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lamps = new List<PjLinkLamp>();
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (int.TryParse(tokens[i], out int hours) && int.TryParse(tokens[i + 1], out int state))
                lamps.Add(new PjLinkLamp(hours, state == 1));
        }

        var labels = new List<string>();
        for (int i = 0; i < lamps.Count; i++)
        {
            string label = lamps.Count == 1 ? "Lamp" : $"Lamp {i + 1}";
            labels.Add(label);
            SetDetail(label, $"{lamps[i].Hours} h, {(lamps[i].IsOn ? "on" : "off")}");
        }
        foreach (string stale in _lampDetailLabels.Except(labels))
            RemoveDetail(stale);
        _lampDetailLabels = labels;

        if (lamps.SequenceEqual(Lamps))
            return;
        Lamps = lamps;
        OnLampsChanged?.Invoke(lamps);
    }

    private void SetInputResolution(string value)
    {
        SetDetail(InputResolutionLabel, value == "-" ? "No signal" : value);
        if (InputResolution == value)
            return;
        InputResolution = value;
        OnInputResolutionChanged?.Invoke(value);
    }

    private void ClearInputResolution()
    {
        RemoveDetail(InputResolutionLabel);
        if (InputResolution.Length == 0)
            return;
        InputResolution = string.Empty;
        OnInputResolutionChanged?.Invoke(string.Empty);
    }

    private void SetFreezeState(bool frozen)
    {
        SetDetail(FrozenLabel, frozen ? "Yes" : "No");
        if (FreezeActive == frozen)
            return;
        FreezeActive = frozen;
        OnFreezeChanged?.Invoke(frozen);
    }

    private void ClearFreeze()
    {
        RemoveDetail(FrozenLabel);
        if (!FreezeActive)
            return;
        FreezeActive = false;
        OnFreezeChanged?.Invoke(false);
    }

    private void HandleInputList(string value)
    {
        string[] tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        var codes = tokens.Where(t => int.TryParse(t, out _)).Select(int.Parse).ToList();
        var unrecognised = tokens.Where(t => !int.TryParse(t, out _)).ToList();
        bool changed = !codes.SequenceEqual(AvailableInputCodes) || !unrecognised.SequenceEqual(_unrecognisedInputCodes);
        AvailableInputCodes = codes;
        _unrecognisedInputCodes = unrecognised;
        bool namesPossible;
        lock (_stateLock)
        {
            _instReceivedThisConnection = true;
            if (changed)
            {
                _inputNamesRequested = false;
                _inputNamesIncomplete = false;
                _inputNameRetryDelay = 1;
                _inputNameRetryCountdown = 0;
            }
            namesPossible = PjLinkClass >= 2 && !_unsupportedCommands.Contains("INNM");
        }
        if (changed)
            OnDeviceInformationChanged?.Invoke();
        if (namesPossible)
            RequestInputNames();
        else if (changed)
            RebuildInputMap();
    }

    private void HandleClass(string value)
    {
        if (!int.TryParse(value, out int pjLinkClass))
        {
            RaiseParseIssue("CLSS", value);
            return;
        }
        if (PjLinkClass != pjLinkClass)
        {
            PjLinkClass = pjLinkClass;
            SetDetail("PJLink Class", value);
            OnDeviceInformationChanged?.Invoke();
        }
        if (PjLinkClass >= 2 && !_class2InformationRequested)
            QueryClass2Information();
        bool instKnown;
        lock (_stateLock) instKnown = _instReceivedThisConnection;
        if (instKnown)
            RequestInputNames();
    }

    private void RequestInputNames()
    {
        lock (_stateLock)
        {
            if (_inputNamesRequested)
                return;
        }
        StartInputNamePass();
    }

    private void StartInputNamePass()
    {
        int? first;
        lock (_stateLock)
        {
            if (PjLinkClass < 2 || AvailableInputCodes.Count == 0 || !_instReceivedThisConnection)
                return;
            if (_inFlightInputNameCode != null)
            {
                // INNM answers don't echo the input code, so a pass can't be restarted mid-flight.
                _inputListDirty = true;
                return;
            }
            _inputNamesRequested = true;
            _inputListDirty = false;
            _inputNamesIncomplete = false;
            _inputNames.Clear();
            _pendingInputNameQueries.Clear();
            if (_unsupportedCommands.Contains("INNM"))
            {
                first = null;
            }
            else
            {
                foreach (int code in AvailableInputCodes)
                    _pendingInputNameQueries.Enqueue(code);
                first = _pendingInputNameQueries.Dequeue();
                _inFlightInputNameCode = first;
            }
        }
        if (first is { } toSend)
            SendClass2($"INNM ?{toSend}");
        else
            RebuildInputMap();
    }

    private void AdvanceInputNames(string? name)
    {
        int? next;
        bool restart;
        bool rebuild;
        lock (_stateLock)
        {
            if (_inFlightInputNameCode is { } code && name != null)
                _inputNames[code] = name;
            bool unsupported = _unsupportedCommands.Contains("INNM");
            if (unsupported)
            {
                _pendingInputNameQueries.Clear();
                _inputListDirty = false;
                _inputNamesIncomplete = false;
            }
            restart = _inputListDirty;
            if (restart)
            {
                _inFlightInputNameCode = null;
                _pendingInputNameQueries.Clear();
                _inputNamesRequested = false;
                next = null;
            }
            else
            {
                next = _pendingInputNameQueries.Count > 0 ? _pendingInputNameQueries.Dequeue() : null;
                _inFlightInputNameCode = next;
            }
            // A pass with gaps must not install a guessed map; the previous one stands until a retry completes.
            rebuild = next == null && !restart && (unsupported || !_inputNamesIncomplete);
            if (next == null && !restart && !rebuild)
                LogDebug("Input names incomplete, keeping the current input map until the retry");
            if (rebuild && !unsupported)
            {
                _inputNameRetryDelay = 1;
                _inputNameRetryCountdown = 0;
            }
        }
        if (restart)
            StartInputNamePass();
        else if (next is { } toSend)
            SendClass2($"INNM ?{toSend}");
        else if (rebuild)
            RebuildInputMap();
    }

    private void RebuildInputMap()
    {
        var codes = AvailableInputCodes;
        if (codes.Count == 0 && _unrecognisedInputCodes.Count == 0)
            return;

        Dictionary<int, string> names;
        lock (_stateLock) names = new Dictionary<int, string>(_inputNames);

        var byCode = new Dictionary<int, Input>();
        var claimed = new HashSet<Input>();
        if (_explicitInputMap != null)
        {
            foreach (var (input, code) in _explicitInputMap)
            {
                if (codes.Contains(code) && !byCode.ContainsKey(code) && claimed.Add(input))
                    byCode[code] = input;
            }
        }

        var unmapped = new List<int>();
        foreach (int code in codes)
        {
            if (byCode.ContainsKey(code))
                continue;
            Input? chosen = null;
            foreach (Input candidate in CandidateInputs(code, names.GetValueOrDefault(code)))
            {
                if (claimed.Add(candidate))
                {
                    chosen = candidate;
                    break;
                }
            }
            if (chosen is { } input)
                byCode[code] = input;
            else
                unmapped.Add(code);
        }

        var order = codes.Where(byCode.ContainsKey).Select(c => byCode[c]).ToList();
        var newMap = order.ToDictionary(i => i, i => byCode.First(kv => kv.Value == i).Key);
        var oldMap = _inputMap;
        bool unchanged = order.SequenceEqual(SupportedInputs)
                         && newMap.Count == oldMap.Count && newMap.All(kv => oldMap.TryGetValue(kv.Key, out int c) && c == kv.Value)
                         && unmapped.SequenceEqual(_unmappedInputCodes);
        if (!unchanged)
        {
            _inputMap = newMap;
            _unmappedInputCodes = unmapped;
            SetSupportedInputs(order);
        }

        var problems = new List<string>();
        if (unmapped.Count > 0)
        {
            string list = string.Join(", ", unmapped.Select(c =>
                names.TryGetValue(c, out var n) ? $"{c} ({n})" : c.ToString()));
            SetDetail(UnmappedInputsLabel, list, DetailTone.Warning);
            problems.Add($"inputs with no Input mapping: {list}");
        }
        else
        {
            RemoveDetail(UnmappedInputsLabel);
        }
        if (_unrecognisedInputCodes.Count > 0)
            problems.Add($"non-numeric input numbers the driver can't select: {string.Join(", ", _unrecognisedInputCodes)}");
        if (DesiredInput != Input.Unknown && !newMap.ContainsKey(DesiredInput))
        {
            problems.Add($"desired input {DesiredInput} is not one of the projector's inputs");
            DesiredInput = Input.Unknown;
        }
        if (DefaultInput is { } defaultInput && defaultInput != Input.Unknown && !newMap.ContainsKey(defaultInput))
            problems.Add($"default input {defaultInput} is not one of the projector's inputs");

        if (problems.Count > 0)
            RaiseOngoingIssue(InputMapIssueKey, "Projector " + string.Join("; ", problems), IssueSeverity.Minor);
        else
            ResolveIssue(InputMapIssueKey);

        if (!unchanged)
            OnDeviceInformationChanged?.Invoke();
    }

    // A recognised name goes first, then the code class — as the fallback when that Input is already
    // claimed (LAN and Wireless are both "network") or when nobody recognises the label at all.
    private static IEnumerable<Input> CandidateInputs(int code, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && TryMapInputName(name, out Input byName))
            yield return byName;
        if (TryMapInputCode(code, out Input byCode))
            yield return byCode;
    }

    public static bool TryMapInput(int code, string? name, out Input input)
    {
        if (!string.IsNullOrWhiteSpace(name) && TryMapInputName(name, out input))
            return true;
        return TryMapInputCode(code, out input);
    }

    private static bool TryMapInputCode(int code, out Input input)
    {
        input = code switch
        {
            >= 11 and <= 13 => Input.Vga1 + (code - 11),
            21 => Input.Composite,
            >= 31 and <= 34 => Input.Hdmi1 + (code - 31),
            35 => Input.Hdmi5,
            36 => Input.Hdmi6,
            41 => Input.Usb1,
            42 => Input.Usb2,
            >= 51 and <= 55 => Input.Network1 + (code - 51),
            56 => Input.Network6,
            61 => Input.Internal1,
            62 => Input.Internal2,
            _ => Input.Unknown
        };
        return input != Input.Unknown;
    }

    // Labels are matched on their leading token so suffixes like "/MHL", "(4K)" or "-D" don't defeat them.
    private static bool TryMapInputName(string name, out Input input)
    {
        string n = Regex.Replace(name.ToUpperInvariant(), "[ \\-_/]", "");
        Match m;
        if ((m = Regex.Match(n, @"^HDMI(\d?)")).Success)
            return Numbered("Hdmi", m.Groups[1].Value, 6, out input);
        if ((m = Regex.Match(n, @"^(?:COMPUTER|RGB|PC|VGA|ANALOG)(\d?)")).Success)
            return Numbered("Vga", m.Groups[1].Value, 3, out input);
        if ((m = Regex.Match(n, "^INPUT([A-C])")).Success)
            return Numbered("Vga", ((char)('1' + m.Groups[1].Value[0] - 'A')).ToString(), 3, out input);
        if ((m = Regex.Match(n, @"^DVI[DIA]?(\d?)")).Success)
            return Numbered("Dvi", m.Groups[1].Value, 2, out input);
        if (Regex.IsMatch(n, "^(?:DISPLAYPORT|DP)"))
            return Single(Input.DisplayPort, out input);
        if (Regex.IsMatch(n, "^SVIDEO"))
            return Single(Input.SVideo, out input);
        if (Regex.IsMatch(n, "^(?:VIDEO|COMPOSITE)"))
            return Single(Input.Composite, out input);
        if (Regex.IsMatch(n, "^(?:COMPONENT|YPBPR)"))
            return Single(Input.Component, out input);
        if (Regex.IsMatch(n, "^(?:HDBASET|DIGITALLINK)"))
            return Single(Input.HdBaseT, out input);
        if (n.StartsWith("SDI"))
            return Single(Input.Sdi, out input);
        if (n.StartsWith("USBDISPLAY"))
            return Single(Input.UsbDisplay, out input);
        if ((m = Regex.Match(n, "^USB([0-9AB]?)")).Success)
            return Numbered("Usb", m.Groups[1].Value switch { "A" => "1", "B" => "2", var d => d }, 2, out input);
        if (Regex.IsMatch(n, "^(?:SCREENMIRRORING|MIRACAST)"))
            return Single(Input.ScreenMirroring, out input);
        if ((m = Regex.Match(n, @"^(?:LAN|NETWORK|WIRELESS|WIFI)(\d?)")).Success)
            return Numbered("Network", m.Groups[1].Value, 6, out input);
        input = Input.Unknown;
        return false;
    }

    private static bool Single(Input value, out Input input)
    {
        input = value;
        return true;
    }

    private static bool Numbered(string prefix, string digit, int max, out Input input)
    {
        int number = digit.Length == 0 ? 1 : digit[0] - '0';
        input = Input.Unknown;
        return number >= 1 && number <= max && Enum.TryParse($"{prefix}{number}", out input);
    }

    public byte[] GetMd5Hash(string input)
    {
        byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(input));
        return Encoding.ASCII.GetBytes(Convert.ToHexString(hash).ToLowerInvariant());
    }

    private void Send(string command) => SendWithClass('1', command);

    private void SendClass2(string command) => SendWithClass('2', command);

    private void SendWithClass(char pjLinkClass, string command)
    {
        if (command.Length < 4)
            return;
        string mnemonic = command.Substring(0, 4);
        lock (_stateLock)
        {
            if (_unsupportedCommands.Contains(mnemonic))
                return;
            _lastRequestWasQuery[mnemonic] = command.Length > 5 && command[5] == '?';
        }
        CommunicationClient.Send($"%{pjLinkClass}{command}\r");
    }

    private void SetPowerState(PowerState desiredPowerState)
    {
        using (PushProperties("SetPowerState"))
        {
            if (!PowerStateDictionary.TryGetValue(desiredPowerState, out var value))
            {
                LogWarning("Desired PowerState {DesiredPowerState} is not appropriate", desiredPowerState);
                return;
            }

            Send($"POWR {value}");
            DesiredPowerState = desiredPowerState;
        }
    }

    protected override void DoPowerOn() => SetPowerState(PowerState.On);

    protected override void DoPowerOff() => SetPowerState(PowerState.Off);

    protected override void DoSetInput(Input input)
    {
        var map = _inputMap;
        if (!map.TryGetValue(input, out int code))
        {
            using (PushProperties("DoSetInput"))
                LogWarning("Input {Input} is not one of the projector's inputs", input);
            return;
        }
        Send($"INPT {code}");
    }

    protected override void DoSetVolume(int percentage)
    {
        using (PushProperties())
        {
            LogWarning("Volume control is not supported");
        }
    }

    protected override void DoSetAudioMute(MuteState state) => SendMuteState();

    public void SetPictureMute(MuteState state)
    {
        DesiredVideoMute = state;
        SendMuteState();
    }

    public void SetFreeze(bool freeze)
    {
        SendClass2($"FREZ {(freeze ? 1 : 0)}");
        SendClass2("FREZ ?");
    }

    public void Freeze() => SetFreeze(true);

    public void Unfreeze() => SetFreeze(false);

    public void SpeakerVolumeUp() => SendClass2("SVOL 1");

    public void SpeakerVolumeDown() => SendClass2("SVOL 0");

    public void MicrophoneVolumeUp() => SendClass2("MVOL 1");

    public void MicrophoneVolumeDown() => SendClass2("MVOL 0");

    private enum MuteCommandToSend
    {
        None = 30,
        AudioOnly = 21,
        VideoOnly = 11,
        Both = 31
    }

    // A mute nobody has asked for keeps whatever the projector currently reports, so muting the
    // picture doesn't silently un-mute audio that was muted from the remote.
    private void SendMuteState()
    {
        bool audio = (DesiredAudioMute == MuteState.Unknown ? AudioMute : DesiredAudioMute) == MuteState.On;
        bool video = (DesiredVideoMute == MuteState.Unknown ? VideoMute : DesiredVideoMute) == MuteState.On;
        MuteCommandToSend commandToSend = (audio, video) switch
        {
            (true, true) => MuteCommandToSend.Both,
            (true, false) => MuteCommandToSend.AudioOnly,
            (false, true) => MuteCommandToSend.VideoOnly,
            _ => MuteCommandToSend.None
        };
        Send($"AVMT {(int)commandToSend}");
    }
}
