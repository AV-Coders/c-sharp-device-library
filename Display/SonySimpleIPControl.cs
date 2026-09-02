using System.Text;
using AVCoders.Core;
using AVCoders.MediaPlayer;

namespace AVCoders.Display;

public class SonySimpleIpControl : Display, ISetTopBox
{
    public static readonly ushort DefaultPort = 20060;
    public const int MaxOutstandingPerCommand = 8;
    public const int ExternalAudioConfirmations = 2;
    public const int ExternalAudioReprobeInterval = 4;
    public const int HomeConfirmations = 2;
    public static TimeSpan OutstandingExpiry { get; set; } = TimeSpan.FromSeconds(8);
    public static TimeSpan ExternalAudioGrace { get; set; } = TimeSpan.FromSeconds(45);
    public static TimeSpan PowerOnBootWindow { get; set; } = TimeSpan.FromSeconds(30);
    private const int MaxGatherLength = 4096;
    private const int FrameLength = 23;
    private const string SuccessValue = "0000000000000000";
    private const string NotApplicableValue = "FFFFFFFFFFFFFFFF";
    private const string UnsupportedValue = "NNNNNNNNNNNNNNNN";
    private const string EnquiryParameter = "################";
    private const string TunerInputValue = "0000000000000000";
    private const string AudioDetailLabel = "Audio";
    private const string ChannelDetailLabel = "Channel";
    private const string UnsupportedDetailLabel = "Unsupported Commands";
    private const string RejectedIssuePrefix = "rejected-";

    private static readonly Dictionary<Input, string> InputDictionary = new()
    {
        { Input.Hdmi1, "0000000100000001" },
        { Input.Hdmi2, "0000000100000002" },
        { Input.Hdmi3, "0000000100000003" },
        { Input.Hdmi4, "0000000100000004" },
        { Input.DvbtTuner, TunerInputValue },
        { Input.Scart, "0000000200000001" },
        { Input.Composite, "0000000300000001" },
        { Input.Component, "0000000400000001" },
        { Input.ScreenMirroring, "0000000500000001" },
        { Input.Pc, "0000000600000001" }
    };

    private static readonly List<RemoteButton> UnsupportedButtons = [RemoteButton.Guide];

    private static readonly Dictionary<RemoteButton, int> RemoteButtonMap = new()
    {
        { RemoteButton.Button0, 27 },
        { RemoteButton.Button1, 18 },
        { RemoteButton.Button2, 19 },
        { RemoteButton.Button3, 20 },
        { RemoteButton.Button4, 21 },
        { RemoteButton.Button5, 22 },
        { RemoteButton.Button6, 23 },
        { RemoteButton.Button7, 24 },
        { RemoteButton.Button8, 25 },
        { RemoteButton.Button9, 26 },
        { RemoteButton.Enter, 13 },
        { RemoteButton.Back, 8 },
        { RemoteButton.Up, 9 },
        { RemoteButton.Down, 10 },
        { RemoteButton.Left, 12 },
        { RemoteButton.Right, 11 },
        { RemoteButton.Subtitle, 35 },
        { RemoteButton.Power, 98 },
        { RemoteButton.VolumeUp, 30 },
        { RemoteButton.VolumeDown, 31 },
        { RemoteButton.Mute, 32 },
        { RemoteButton.ChannelUp, 33 },
        { RemoteButton.ChannelDown, 34 },
        { RemoteButton.Play, 78 },
        { RemoteButton.Pause, 84 },
        { RemoteButton.Stop, 81 },
        { RemoteButton.Rewind, 79 },
        { RemoteButton.FastForward, 77 },
        { RemoteButton.Previous, 80 },
        { RemoteButton.Next, 82 },
        { RemoteButton.Home, 6 },
        { RemoteButton.Blue, 17 },
        { RemoteButton.Yellow, 16 },
        { RemoteButton.Green, 15 },
        { RemoteButton.Red, 14 },
        // { RemoteButton.Guide, },
        { RemoteButton.Menu, 7 },
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<(char Type, DateTime SentAt)>> _outstanding = new();
    private readonly HashSet<string> _overflowWarned = new();
    private readonly HashSet<string> _unsupportedCommands = new();
    private readonly HashSet<string> _warnedInputValues = new();
    private string _gather = string.Empty;
    private volatile bool _externalAudio;
    private volatile bool _tvConfirmedOn;
    private PowerState _tvReportedPower = PowerState.Unknown;
    private DateTime _onSince;
    private DateTime _powerOnSentAt = DateTime.MinValue;
    private int _externalAudioSkips;
    private int _consecutiveAudioNotApplicable;
    private bool _audioProbeAnsweredNotApplicable;
    private int _consecutiveHome;
    private bool _inputProbeAnsweredNotApplicable;

    public string Channel { get; private set; } = string.Empty;
    public bool ExternalAudio => _externalAudio;
    public bool TvConfirmedOn => _tvConfirmedOn;

    public IReadOnlyCollection<string> UnsupportedCommands
    {
        get
        {
            lock (_lock)
                return _unsupportedCommands.ToArray();
        }
    }

    public event Action<string>? ChannelChanged;

    public SonySimpleIpControl(TcpClient tcpClient, string name, Input? defaultInput) : base(
        InputDictionary.Keys.ToList(), name, defaultInput, tcpClient, CommandStringFormat.Ascii, 15)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        lock (_lock)
        {
            _gather = string.Empty;
            _outstanding.Clear();
            _overflowWarned.Clear();
            _unsupportedCommands.Clear();
            _externalAudioSkips = 0;
            _consecutiveAudioNotApplicable = 0;
            _audioProbeAnsweredNotApplicable = false;
            _consecutiveHome = 0;
            _inputProbeAnsweredNotApplicable = false;
            _tvReportedPower = PowerState.Unknown;
        }
        _externalAudio = false;
        _tvConfirmedOn = false;
        RemoveDetail(UnsupportedDetailLabel);
        RemoveDetail(AudioDetailLabel);
        ClearChannel();
        foreach (var issue in GetOngoingIssues().Where(i => i.Key.StartsWith(RejectedIssuePrefix)))
            ResolveIssue(issue.Key);
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
            QueryStatus();
        }
        return Task.CompletedTask;
    }

    public void QueryStatus()
    {
        Enquire("POWR");
        if (_tvConfirmedOn)
            QueryOnState();
    }

    private void QueryOnState()
    {
        bool probeAudio;
        lock (_lock)
        {
            _inputProbeAnsweredNotApplicable = false;
            probeAudio = !_externalAudio || ++_externalAudioSkips % ExternalAudioReprobeInterval == 0;
            if (probeAudio)
                _audioProbeAnsweredNotApplicable = false;
        }

        Enquire("INPT");
        Enquire("PMUT");
        if (probeAudio)
        {
            Enquire("VOLU");
            Enquire("AMUT");
        }
        if (Input == Input.DvbtTuner)
            Enquire("CHNN");
    }

    private void Enquire(string command)
    {
        if (IsUnsupported(command))
            return;
        Track(command, 'E');
        SendCommand(WrapMessage($"E{command}{EnquiryParameter}"));
    }

    private void Control(string command, string parameter)
    {
        if (IsUnsupported(command))
        {
            using (PushProperties("Control"))
                LogWarning("{Command} is not supported by this TV", command);
            return;
        }
        Track(command, 'C');
        SendCommand(WrapMessage($"C{command}{parameter}"));
    }

    private bool IsUnsupported(string command)
    {
        lock (_lock)
            return _unsupportedCommands.Contains(command);
    }

    private void Track(string command, char requestType)
    {
        bool warn = false;
        lock (_lock)
        {
            if (!_outstanding.TryGetValue(command, out var queue))
            {
                queue = new Queue<(char, DateTime)>();
                _outstanding[command] = queue;
            }
            PurgeExpired(queue);
            if (queue.Count >= MaxOutstandingPerCommand)
            {
                // Answers arrive in order, so only dropping the newest keeps the tracked entries aligned.
                warn = _overflowWarned.Add(command);
            }
            else
            {
                queue.Enqueue((requestType, DateTime.UtcNow));
            }
        }
        if (warn)
        {
            using (PushProperties("Track"))
                LogWarning("Too many unanswered {Command} requests; further answers are treated as untracked", command);
        }
    }

    private char? Untrack(string command)
    {
        lock (_lock)
        {
            if (!_outstanding.TryGetValue(command, out var queue))
                return null;
            PurgeExpired(queue);
            return queue.Count > 0 ? queue.Dequeue().Type : null;
        }
    }

    private static void PurgeExpired(Queue<(char Type, DateTime SentAt)> queue)
    {
        var cutoff = DateTime.UtcNow - OutstandingExpiry;
        while (queue.Count > 0 && queue.Peek().SentAt < cutoff)
            queue.Dequeue();
    }

    private void SendCommand(string command)
    {
        try
        {
            CommunicationClient.Send(command);
        }
        catch (Exception e)
        {
            CommunicationState = CommunicationState.Error;
            LogException(e);
        }
    }

    private string WrapMessage(string message)
    {
        StringBuilder builder = new StringBuilder("*S");
        builder.Append(message);
        builder.Append('\n');
        return builder.ToString();
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            var frames = new List<string>();
            lock (_lock)
            {
                _gather += response;

                int delimiterIndex;
                while ((delimiterIndex = _gather.IndexOf('\n')) >= 0)
                {
                    string frame = _gather.Substring(0, delimiterIndex).Trim();
                    _gather = _gather.Substring(delimiterIndex + 1);
                    if (frame.Length > 0)
                        frames.Add(frame);
                }

                if (_gather.Length > MaxGatherLength)
                {
                    LogWarning("Discarding {Length} buffered bytes with no message terminator", _gather.Length);
                    _gather = string.Empty;
                }
            }

            foreach (var frame in frames)
                ProcessFrame(frame);
        }
    }

    private void ProcessFrame(string frame)
    {
        if (frame.Length != FrameLength || !frame.StartsWith("*S"))
        {
            LogDebug("Ignoring malformed frame {Frame}", frame);
            return;
        }

        char type = frame[2];
        string command = frame.Substring(3, 4);
        string value = frame.Substring(7, 16);

        switch (type)
        {
            case 'N':
                ApplyState(command, value);
                CommunicationState = CommunicationState.Okay;
                break;
            case 'A':
                HandleAnswer(command, value);
                break;
            default:
                LogDebug("Ignoring frame type {Type} for {Command}", type, command);
                break;
        }
    }

    private void HandleAnswer(string command, string value)
    {
        char? requestType = Untrack(command);

        if (value == UnsupportedValue)
        {
            if (requestType == 'E')
                MarkUnsupported(command);
            else
                RejectControl(command, "is not supported by this TV");
            CommunicationState = CommunicationState.Okay;
            return;
        }

        if (value == NotApplicableValue)
        {
            if (requestType == 'C')
                RejectControl(command, "was rejected by the TV");
            else if (requestType == null)
                LogDebug("Ignoring an unattributable not-applicable answer for {Command}", command);
            else
                HandleNotApplicableEnquiry(command);
            CommunicationState = CommunicationState.Okay;
            return;
        }

        if (value == SuccessValue)
        {
            if (requestType == 'C')
            {
                ResolveIssue($"{RejectedIssuePrefix}{command}");
                CommunicationState = CommunicationState.Okay;
                return;
            }
            if (requestType == null)
            {
                LogDebug("Ignoring an unexpected ambiguous answer for {Command}", command);
                return;
            }
        }

        ApplyState(command, value);
        CommunicationState = CommunicationState.Okay;
    }

    private void MarkUnsupported(string command)
    {
        string detail;
        lock (_lock)
        {
            if (!_unsupportedCommands.Add(command))
                return;
            detail = string.Join(", ", _unsupportedCommands.Order());
        }
        LogInformation("{Command} is not supported by this TV", command);
        SetDetail(UnsupportedDetailLabel, detail, DetailTone.Warning);
    }

    private void RejectControl(string command, string reason)
    {
        LogWarning("{Command} {Reason}", command, reason);
        RaiseMomentaryIssue($"{command} command {reason}", $"{RejectedIssuePrefix}{command}", IssueSeverity.Minor, 3);
    }

    private void HandleNotApplicableEnquiry(string command)
    {
        switch (command)
        {
            case "VOLU":
            case "AMUT":
                if (!ShouldLatchExternalAudio())
                    return;
                _externalAudio = true;
                AudioMute = MuteState.Unknown;
                SetDetail(AudioDetailLabel, "External audio system");
                LogDebug("The TV is not controlling audio; volume and mute polling reduced");
                break;
            case "INPT":
                if (!ShouldReportHome())
                    return;
                Input = Input.Home;
                ClearChannel();
                ProcessInputResponse();
                break;
            case "CHNN":
                ClearChannel();
                break;
            default:
                LogDebug("{Command} is not applicable right now", command);
                break;
        }
    }

    private bool ShouldLatchExternalAudio()
    {
        if (!_tvConfirmedOn || _externalAudio)
            return false;
        lock (_lock)
        {
            if (_audioProbeAnsweredNotApplicable)
                return false;
            _audioProbeAnsweredNotApplicable = true;
            _consecutiveAudioNotApplicable++;
            if (_consecutiveAudioNotApplicable < ExternalAudioConfirmations)
                return false;
            if (DateTime.UtcNow - _onSince < ExternalAudioGrace)
                return false;
            _externalAudioSkips = 0;
            return true;
        }
    }

    private bool ShouldReportHome()
    {
        if (!_tvConfirmedOn || IsBooting())
            return false;
        lock (_lock)
        {
            if (_inputProbeAnsweredNotApplicable)
                return false;
            _inputProbeAnsweredNotApplicable = true;
            _consecutiveHome++;
            return _consecutiveHome >= HomeConfirmations;
        }
    }

    private void ApplyState(string command, string value)
    {
        switch (command)
        {
            case "POWR":
                ApplyPower(value);
                break;
            case "VOLU":
                if (!int.TryParse(value, out int volume) || volume is < 0 or > 100)
                {
                    LogWarning("Unable to parse volume {Value}", value);
                    return;
                }
                Volume = volume;
                MarkTvAudio();
                break;
            case "AMUT":
                if (!TryParseFlag(value, out bool audioMuted))
                {
                    LogWarning("Unable to parse audio mute {Value}", value);
                    return;
                }
                AudioMute = audioMuted ? MuteState.On : MuteState.Off;
                MarkTvAudio();
                break;
            case "PMUT":
                if (!TryParseFlag(value, out bool pictureMuted))
                {
                    LogWarning("Unable to parse picture mute {Value}", value);
                    return;
                }
                VideoMute = pictureMuted ? MuteState.On : MuteState.Off;
                break;
            case "INPT":
                lock (_lock)
                    _consecutiveHome = 0;
                Input = ParseInput(value);
                if (Input != Input.DvbtTuner)
                    ClearChannel();
                else if (Channel.Length == 0)
                    Enquire("CHNN");
                ProcessInputResponse();
                break;
            case "CHNN":
                if (!TryParseChannel(value, out string channel))
                {
                    LogWarning("Unable to parse channel {Value}", value);
                    return;
                }
                SetDetail(ChannelDetailLabel, channel);
                if (Channel == channel)
                    return;
                Channel = channel;
                ChannelChanged?.Invoke(channel);
                break;
            default:
                LogDebug("Unhandled state {Command}={Value}", command, value);
                break;
        }
    }

    private void ApplyPower(string value)
    {
        bool wasOn = PowerState == PowerState.On;
        switch (value)
        {
            case "0000000000000001":
                bool justConfirmed = !_tvConfirmedOn;
                bool tvWasOff;
                lock (_lock)
                {
                    tvWasOff = _tvReportedPower == PowerState.Off;
                    _tvReportedPower = PowerState.On;
                }
                PowerState = PowerState.On;
                if (justConfirmed)
                {
                    _tvConfirmedOn = true;
                    _onSince = DateTime.UtcNow;
                }
                if (tvWasOff)
                    ResetAudioDetection();
                ProcessPowerResponse();
                if (!wasOn || justConfirmed)
                    QueryOnState();
                break;
            case "0000000000000000":
                lock (_lock)
                    _tvReportedPower = PowerState.Off;
                PowerState = PowerState.Off;
                _tvConfirmedOn = false;
                HandlePoweredOff(reportedByTv: true);
                ProcessPowerResponse();
                break;
            default:
                LogWarning("Unable to parse power state {Value}", value);
                break;
        }
    }

    private void ResetAudioDetection()
    {
        bool wasExternal = _externalAudio;
        _externalAudio = false;
        lock (_lock)
        {
            _externalAudioSkips = 0;
            _consecutiveAudioNotApplicable = 0;
            _audioProbeAnsweredNotApplicable = false;
        }
        if (wasExternal)
            RemoveDetail(AudioDetailLabel);
    }

    private void HandlePoweredOff(bool reportedByTv)
    {
        ResolveIssue(InputIssueKey);
        lock (_lock)
            _consecutiveHome = 0;
        if (reportedByTv && IsBooting())
            return;
        Input = Input.Unknown;
        ClearChannel();
    }

    private bool IsBooting() =>
        DesiredPowerState == PowerState.On && DateTime.UtcNow - _powerOnSentAt < PowerOnBootWindow;

    private void MarkTvAudio()
    {
        lock (_lock)
            _consecutiveAudioNotApplicable = 0;
        _externalAudio = false;
        SetDetail(AudioDetailLabel, "TV speakers");
    }

    private void ClearChannel()
    {
        RemoveDetail(ChannelDetailLabel);
        if (Channel.Length == 0)
            return;
        Channel = string.Empty;
        ChannelChanged?.Invoke(Channel);
    }

    private Input ParseInput(string value)
    {
        var known = InputDictionary.FirstOrDefault(kv => kv.Value == value);
        if (known.Value != null)
            return known.Key;

        if (value.Length == 16 && int.TryParse(value.AsSpan(0, 8), out int type) &&
            int.TryParse(value.AsSpan(8, 8), out int number))
        {
            Input? mapped = type switch
            {
                0 => Input.DvbtTuner,
                1 when Enum.TryParse($"Hdmi{number}", out Input hdmi) => hdmi,
                2 => Input.Scart,
                3 => Input.Composite,
                4 => Input.Component,
                5 => Input.ScreenMirroring,
                6 => Input.Pc,
                _ => null
            };
            if (mapped is { } input)
                return input;
        }

        bool firstTime;
        lock (_lock)
            firstTime = _warnedInputValues.Add(value);
        if (firstTime)
            LogWarning("Unrecognised input {Value}", value);
        return Input.Unknown;
    }

    private static bool TryParseFlag(string value, out bool flag)
    {
        flag = false;
        if (!long.TryParse(value, out long number) || number is < 0 or > 1)
            return false;
        flag = number == 1;
        return true;
    }

    public static bool TryParseChannel(string value, out string channel)
    {
        channel = string.Empty;
        string[] parts = value.Split('.');
        if (parts.Length > 2 || !int.TryParse(parts[0], out int major))
            return false;
        if (parts.Length == 1)
        {
            channel = major.ToString();
            return true;
        }
        if (!int.TryParse(parts[1], out int minor))
            return false;
        channel = $"{major}.{minor}";
        return true;
    }

    protected override void DoPowerOff()
    {
        _tvConfirmedOn = false;
        HandlePoweredOff(reportedByTv: false);
        Control("POWR", $"{0:D16}");
    }

    protected override void DoPowerOn()
    {
        _powerOnSentAt = DateTime.UtcNow;
        Control("POWR", $"{1:D16}");
    }

    protected override void DoSetInput(Input input) => Control("INPT", InputDictionary[input]);

    public override void SetVolume(int volume)
    {
        if (RefuseWhileExternalAudio("Volume"))
            return;
        base.SetVolume(volume);
    }

    protected override void DoSetVolume(int volume) => Control("VOLU", $"{volume:D16}");

    public override void SetAudioMute(MuteState state)
    {
        if (RefuseWhileExternalAudio("Audio mute"))
            return;
        base.SetAudioMute(state);
    }

    protected override void DoSetAudioMute(MuteState state) =>
        Control("AMUT", $"{(state == MuteState.On ? 1 : 0):D16}");

    private bool RefuseWhileExternalAudio(string what)
    {
        if (!_externalAudio)
            return false;
        using (PushProperties("RefuseWhileExternalAudio"))
            LogWarning("{What} is controlled by the external audio system, not the TV", what);
        AddEvent(EventType.Error, $"{what} is controlled by the external audio system, not the TV");
        return true;
    }

    public void SetVideoMute(MuteState desiredState)
    {
        Control("PMUT", $"{(desiredState == MuteState.On ? 1 : 0):D16}");
        VideoMute = desiredState;
    }

    public void ChannelUp() => SendIRCode(RemoteButton.ChannelUp);

    public void ChannelDown() => SendIRCode(RemoteButton.ChannelDown);

    public void SendIRCode(RemoteButton button)
    {
        if (UnsupportedButtons.Contains(button))
        {
            using (PushProperties("SendIRCode"))
                LogWarning("Unsupported button - {UnsupportedRemoteButton}", button.ToString());
            AddEvent(EventType.Error, $"Unsupported button - {button.ToString()}");
            return;
        }

        if (button is RemoteButton.Home or RemoteButton.Menu)
        {
            DesiredInput = Input.Unknown;
            ResolveIssue(InputIssueKey);
        }

        Control("IRCC", $"{RemoteButtonMap[button]:D16}");

        if (button == RemoteButton.Power)
            DesiredPowerState = PowerState.Unknown;
    }

    public void SetChannel(int channel) => SetChannel(channel, 0);

    public void SetChannel(int major, int minor) => Control("CHNN", $"{major:D8}.{minor:D7}");

    public void ToggleSubtitles() => SendIRCode(RemoteButton.Subtitle);
}
