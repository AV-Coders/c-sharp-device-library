using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Dsp;

public class YamahaXmv : Dsp
{
    public const ushort DefaultPort = 49280;
    public const int PollIntervalMs = 5000;
    public const int MaxUnansweredRunModeQueries = 3;
    public const int MaxMissedReads = 3;
    public const double MinAttenuationDb = -99;
    public const double MaxAttenuationDb = 0;
    public const string AlertIssuePrefix = "alert:";
    public const string UnreadableAlertIssueKey = "alert-unreadable";
    public const string RunModeIssueKey = "run-mode";
    public const string UnansweredRunModeIssueKey = "unanswered-run-mode";
    public const string UnansweredAlertStatusIssueKey = "unanswered-alert-status";
    public const string RejectedCommandIssueKey = "rejected-command";
    public const string ErrorReplyIssueKey = "error-reply";
    public const string UnreadableReplyIssueKey = "unreadable-reply";
    public const string LimiterIssuePrefix = "limiter:";
    public const string PowerValueIssueKey = "power-value";
    public const string LevelScaleIssuePrefix = "level-scale:";
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private const int EscalateAfterRepeats = 3;
    private const int MaxChannels = 8;
    private const string ChannelLetters = "ABCDEFGH";
    private const double RawPerDb = 100;
    private const double LevelDisplayTolerance = 0.005;
    private const int NegativeInfinityRaw = -13801;
    private const int MemoryNumber = 512;
    private const int SignalProcessingId = 1;
    private const int ChannelElement = 4;
    private const int PowerElement = 6;
    private const int InputElement = 7;
    private const int ChannelInputElement = 11;
    private const int AttParameter = 0;
    private const int DigitalAttParameter = 1;
    private const int MuteParameter = 2;
    private const int PolarityParameter = 3;
    private const int UnmutedValue = 0;
    private const int MutedValue = 1;
    private const int PowerOnValue = 0;
    private const int StandbyValue = 1;
    private const string PowerOnDisplay = "ON";
    private const string StandbyDisplay = "Standby";
    private const int LimiterMeterIntervalMs = 1000;
    private const string LimiterMeterAddress = "MTX:mtr_512/80001/meter";
    private const int MaxAlertLogEntriesPerQuery = 10;
    private const string AlertTimeFormat = "yyyy/M/d H:m:s";
    private const int MaxUnterminatedLength = 64 * 1024;
    private const int ShownUnterminatedLength = 200;
    private const char LineEnd = '\n';
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private const string OkReply = "OK";
    private const string OkAdjustedReply = "OKm";
    private const string ErrorReply = "ERROR";
    private const string Notification = "NOTIFY";
    private const string DeviceStatusCommand = "devstatus";
    private const string DeviceInfoCommand = "devinfo";
    private const string GetCommand = "get";
    private const string SetCommand = "set";
    private const string MeterStartCommand = "mtrstart";
    private const string MeterNotification = "mtr";
    private const string EventCommand = "event";
    private const string RunModeOption = "runmode";
    private const string AlertOption = "error";
    private const string SamplingRateOption = "fs";
    private const string WordClockOption = "lockstatus";
    private const string AlertLogCountTarget = "MTX:EventLogGetLogNumber";
    private const string AlertLogListTarget = "MTX:EventLogGetLogList";
    private const string AlertLogEntryTarget = "MTX:EventLogGetLog";
    private const string RunModeQuery = $"{DeviceStatusCommand} {RunModeOption}";
    private const string AlertStatusQuery = $"{DeviceStatusCommand} {AlertOption}";
    private const string AlertLogListKey = $"{EventCommand} {AlertLogListTarget}";
    private const string AlertLogEntryKey = $"{EventCommand} {AlertLogEntryTarget}";
    private const string NormalRunMode = "normal";
    private const string NoAlerts = "none";
    private const string UnknownSamplingRate = "unknown";
    private const string WordClockUnlocked = "unlock";
    private const string AlertOn = "on";
    private const string AlertOff = "off";
    private const string FaultAlert = "flt";
    private const string ErrorAlert = "err";
    private const string WarningAlert = "wrn";

    private const string ModelLabel = "Model";
    private const string FirmwareLabel = "Firmware";
    private const string SerialLabel = "Serial";
    private const string DeviceNameLabel = "Device Name";
    private const string UnitIdLabel = "Unit ID";
    private const string ProtocolVersionLabel = "Protocol Version";
    private const string ParameterSetVersionLabel = "Parameter Set Version";
    private const string SamplingRateLabel = "Sampling Rate";
    private const string WordClockLabel = "Word Clock";
    private const string RunModeLabel = "Run Mode";
    private const string AlertLabel = "Alert";
    private const string AlertLogLabel = "Alert Log";
    private const string LimiterLabel = "Limiter";
    private const string PowerLabel = "Power";
    private const string LevelDisplayLabel = "Level Display";
    private const string PolaritySuffix = " Polarity";
    private const string InputSelectName = "Input Select";

    private static readonly string[] PolarityValues = ["Normal", "Inverse"];
    private static readonly string[] ChannelInputValues = ["Analog", "Digital"];
    private static readonly string[] InputValues = ["Analog", "Digital", "Multiple"];
    private static readonly HashSet<string> RejectionCodes =
    [
        "UnknownCommand", "WrongFormat", "InvalidArgument", "UnknownAddress", "UnknownEventID", "TooLongCommand", "ReadOnly", "NoPermission",
        "InternalError"
    ];
    private static readonly HashSet<string> CriticalAlertNumbers = ["103", "104"];

    private static readonly Regex TokenParser = new("\"(?<quoted>(?:\\\\.|[^\"\\\\])*)\"|(?<bare>\\S+)", RegexOptions.None, RegexTimeout);
    private static readonly Regex AlertParser = new(
        "^(?<type>[^/]*)/(?<message>.*)// x(?<number>[0-9A-Fa-f]+) (?<state>\\S+) \\((?<count>\\d+)\\) ID-(?<unit>\\S+) (?<at>.+)$",
        RegexOptions.None, RegexTimeout);
    private static readonly Regex AlertLogCountParser = new("^lognum=(?<count>\\d+)$", RegexOptions.None, RegexTimeout);
    private static readonly Regex AlertLogEntryParser = new("log(?<index>\\d+)=(?<alert>.*?)(?=\\|log\\d+=|$)", RegexOptions.None, RegexTimeout);

    private static readonly Read ModelRead = new($"{DeviceInfoCommand} productname", ModelLabel, "model-read");
    private static readonly Read FirmwareRead = new($"{DeviceInfoCommand} version", FirmwareLabel, "firmware-read");
    private static readonly Read SerialRead = new($"{DeviceInfoCommand} serialno", SerialLabel, "serial-read");
    private static readonly Read DeviceNameRead = new($"{DeviceInfoCommand} devicename", DeviceNameLabel, "device-name-read");
    private static readonly Read UnitIdRead = new($"{DeviceInfoCommand} deviceid", UnitIdLabel, "unit-id-read");
    private static readonly Read ProtocolVersionRead = new($"{DeviceInfoCommand} protocolver", ProtocolVersionLabel, "protocol-version-read");
    private static readonly Read ParameterSetVersionRead = new($"{DeviceInfoCommand} paramsetver", ParameterSetVersionLabel, "parameter-set-version-read");
    private static readonly Read SamplingRateRead = new($"{DeviceStatusCommand} {SamplingRateOption}", SamplingRateLabel, "sampling-rate-read");
    private static readonly Read WordClockRead = new($"{DeviceStatusCommand} {WordClockOption}", WordClockLabel, "word-clock-read");
    private static readonly Read AlertLogRead = new($"{EventCommand} {AlertLogCountTarget} \"\"", AlertLogLabel, "alert-log-read");
    private static readonly Read LimiterRead = new($"{MeterStartCommand} {LimiterMeterAddress} {LimiterMeterIntervalMs}", LimiterLabel, "limiter-read");
    private static readonly Read[] Reads =
    [
        ModelRead, FirmwareRead, SerialRead, DeviceNameRead, UnitIdRead, ProtocolVersionRead, ParameterSetVersionRead,
        SamplingRateRead, WordClockRead, AlertLogRead, LimiterRead
    ];

    private static readonly XmvParameter PowerParameter = new(ParameterKind.Power, 0, Address(PowerElement, 0, 0));
    private static readonly XmvParameter InputParameter = new(ParameterKind.Input, 0, Address(InputElement, 0, 0));
    private static readonly Dictionary<string, XmvParameter> ParametersByAddress = Enumerable.Range(0, MaxChannels)
        .SelectMany(channel => new XmvParameter[]
        {
            new(ParameterKind.Level, channel, Address(ChannelElement, channel, AttParameter)),
            new(ParameterKind.Level, channel, Address(ChannelElement, channel, DigitalAttParameter)),
            new(ParameterKind.Mute, channel, Address(ChannelElement, channel, MuteParameter)),
            new(ParameterKind.Polarity, channel, Address(ChannelElement, channel, PolarityParameter)),
            new(ParameterKind.ChannelInput, channel, Address(ChannelInputElement, channel, 0))
        })
        .Append(PowerParameter)
        .Append(InputParameter)
        .ToDictionary(parameter => parameter.Address);
    private static readonly Dictionary<string, XmvParameter> StringControls = Enumerable.Range(0, MaxChannels)
        .SelectMany(channel => new (string Name, XmvParameter Parameter)[]
        {
            ($"{ChannelLetters[channel]}{PolaritySuffix}", new(ParameterKind.Polarity, channel, Address(ChannelElement, channel, PolarityParameter))),
            ($"{ChannelLetters[channel]} {InputSelectName}", new(ParameterKind.ChannelInput, channel, Address(ChannelInputElement, channel, 0)))
        })
        .Append((Name: InputSelectName, Parameter: InputParameter))
        .ToDictionary(control => control.Name, control => control.Parameter);

    private readonly TcpClient _client;
    private readonly FaderCurve _faderCurve;
    private readonly SentCommandTracker<XmvCommand> _sent;
    private readonly NamedControls _controls;
    private readonly StatusRequests _runModeQueries;
    private readonly Dictionary<Read, ReadState> _readStates = new();
    private readonly Dictionary<Read, int> _missedReads = new();
    private readonly HashSet<string> _owedGets = new();
    private readonly Dictionary<string, string> _momentaryAlerts = new();
    private readonly SortedDictionary<int, string> _alertLog = new();
    private readonly bool[] _limiting = new bool[MaxChannels];
    private readonly object _lock = new();
    private string _unterminated = string.Empty;
    private bool _runModeNormal;
    private bool _alertStatusNone;
    private int _alertLogCount;
    private int _alertLogNext;
    private DateTime? _latestAlertTime;
    private DateTime? _latestAlertTimeAtConnect;
    private bool _reconciledThisConnection;

    private enum ParameterKind
    {
        Level,
        Mute,
        Polarity,
        ChannelInput,
        Input,
        Power
    }

    private enum ReadState
    {
        Owed,
        Asked,
        Answered,
        NotSupported
    }

    private sealed record XmvParameter(ParameterKind Kind, int Channel, string Address);

    private sealed record Read(string Command, string Label, string IssueKey)
    {
        public string Key => string.Join(' ', Command.Split(' ').Take(2));
    }

    private sealed record XmvCommand(string Name, string Target, string Text)
    {
        public string Key => $"{Name} {Target}";
    }

    private sealed record LoggedAlert(int Index, string Key, bool On, DateTime At, Match Alert, string Raw);

    public YamahaXmv(TcpClient tcpClient, string name = "XMV", FaderCurve faderCurve = FaderCurve.Linear)
        : this(tcpClient, name, faderCurve, PollIntervalMs, null)
    {
    }

    internal YamahaXmv(TcpClient tcpClient, string name, FaderCurve faderCurve, int pollIntervalInMilliseconds, TimeProvider? timeProvider)
        : base(name, tcpClient, pollIntervalInMilliseconds, timeProvider)
    {
        _client = tcpClient;
        _faderCurve = faderCurve;
        _sent = new SentCommandTracker<XmvCommand>(ReplyTimeout, TimeProvider);
        _controls = new NamedControls(PushProperties, LogWarning, (key, message) => RaiseOngoingIssue(key, message), ResolveIssue, SetDetail, RemoveDetail);
        _runModeQueries = new StatusRequests(MaxUnansweredRunModeQueries, SendRunModeQuery, ReportUnansweredRunMode, ReconnectAfterUnansweredRunMode);
        foreach (var read in Reads)
            _readStates[read] = ReadState.Owed;
        tcpClient.RequestHandlers += RequestWritten;
        tcpClient.ResponseHandlers += HandleResponse;
        tcpClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(tcpClient.ConnectionState);
    }

    public override void PowerOn()
    {
        DesiredPowerState = PowerState.On;
        Send($"{SetCommand} {PowerParameter.Address} 0 0 {PowerOnValue}");
    }

    public override void PowerOff()
    {
        DesiredPowerState = PowerState.Off;
        Send($"{SetCommand} {PowerParameter.Address} 0 0 {StandbyValue}");
    }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName)
    {
        if (ChannelOf(controlName) is not { } channel)
        {
            ShowUnknownControlName(controlName, LevelControlType);
            return;
        }
        if (_controls.AddLevel(controlName, volumeLevelHandler, NewLevel) && IsRunModeNormal())
            Get(Address(ChannelElement, channel, AttParameter));
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        if (ChannelOf(muteName) is not { } channel)
        {
            ShowUnknownControlName(muteName, MuteControlType);
            return;
        }
        if (_controls.AddMute(muteName, muteStateHandler, handler => new Mute(handler)) && IsRunModeNormal())
            Get(Address(ChannelElement, channel, MuteParameter));
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (!StringControls.TryGetValue(controlName, out var parameter))
        {
            ShowUnknownControlName(controlName, StringControlType);
            return;
        }
        if (_controls.AddString(controlName, stringValueHandler, handler => new StringValue(handler)) && IsRunModeNormal())
            Get(parameter.Address);
    }

    public override void SetLevel(string controlName, int percentage)
    {
        var level = _controls.FindLevel(controlName);
        if (level == null || ChannelOf(controlName) is not { } channel)
        {
            using (PushProperties("SetLevel"))
                LogWarning("Unknown level {ControlName}, not setting it to {Percentage}", controlName, percentage);
            return;
        }
        var raw = (int)Math.Round(level.PercentageToDb(percentage) * RawPerDb, MidpointRounding.AwayFromZero);
        Send($"{SetCommand} {Address(ChannelElement, channel, AttParameter)} 0 0 {raw.ToString(CultureInfo.InvariantCulture)}");
    }

    public override void LevelUp(string controlName, int amount = 1) => _controls.LevelUp(controlName, amount, SetLevel);

    public override void LevelDown(string controlName, int amount = 1) => _controls.LevelDown(controlName, amount, SetLevel);

    public override void SetAudioMute(string controlName, MuteState muteState)
    {
        if (ChannelOf(controlName) is not { } channel || muteState is not (MuteState.On or MuteState.Off))
        {
            using (PushProperties("SetAudioMute"))
                LogWarning("Not setting the mute {ControlName} to {State}: the XMV takes a channel letter A to H, On or Off", controlName, muteState);
            return;
        }
        var value = muteState == MuteState.On ? MutedValue : UnmutedValue;
        Send($"{SetCommand} {Address(ChannelElement, channel, MuteParameter)} 0 0 {value}");
    }

    public override void ToggleAudioMute(string controlName) => _controls.ToggleMute(controlName, SetAudioMute);

    public override void SetValue(string controlName, string value)
    {
        if (!StringControls.TryGetValue(controlName, out var parameter))
        {
            using (PushProperties("SetValue"))
                LogWarning("Unknown string control {ControlName}, not setting it to {Value}", controlName, value);
            return;
        }
        var labels = Labels(parameter.Kind);
        var raw = Array.FindIndex(labels, label => label.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (raw < 0)
        {
            using (PushProperties("SetValue"))
                LogWarning("Not setting {ControlName} to {Value}: it takes {Values}", controlName, value, string.Join(", ", labels));
            return;
        }
        Send($"{SetCommand} {parameter.Address} 0 0 {raw}");
    }

    public override int GetLevel(string controlName) => _controls.GetLevel(controlName);

    public override MuteState GetAudioMute(string controlName) => _controls.GetMute(controlName);

    public override string GetValue(string controlName) => _controls.GetString(controlName);

    public override Task Reinitialise(CancellationToken token = default)
    {
        using (PushProperties("Reinitialise"))
        {
            lock (_lock)
            {
                foreach (var read in Reads)
                    _readStates[read] = ReadState.Owed;
                _missedReads.Clear();
            }
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return Task.CompletedTask;
            if (IsRunModeNormal())
                Sync();
            else
                _runModeQueries.Request();
        }
        return Task.CompletedTask;
    }

    protected override Task Poll(CancellationToken token)
    {
        using (PushProperties("Poll"))
        {
            ReportMissedReplies();
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return Task.CompletedTask;
            _runModeQueries.Request();
            if (!IsRunModeNormal())
                return Task.CompletedTask;
            Send(AlertStatusQuery);
            SendOwedReads();
            SendOwedGets();
            RequestLimiter();
        }
        return Task.CompletedTask;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        using (PushProperties("HandleConnectionState"))
        {
            AddEvent(EventType.Connection, connectionState.ToString());
            _sent.Clear();
            _runModeQueries.Answered();
            lock (_lock)
            {
                _unterminated = string.Empty;
                _runModeNormal = false;
                foreach (var read in Reads)
                    _readStates[read] = ReadState.Owed;
                _missedReads.Clear();
                _owedGets.Clear();
                _alertLog.Clear();
                _alertLogCount = 0;
                _alertStatusNone = false;
                _latestAlertTimeAtConnect = _latestAlertTime;
                _reconciledThisConnection = false;
                Array.Clear(_limiting);
            }
            if (connectionState == ConnectionState.Connected)
            {
                _runModeQueries.Request();
                return;
            }
            ShowNotConnected();
        }
    }

    private void SendRunModeQuery() => Send(RunModeQuery);

    private void ReportUnansweredRunMode() =>
        RaiseMomentaryIssue($"The amp did not answer {RunModeQuery}", UnansweredRunModeIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);

    private void ReconnectAfterUnansweredRunMode()
    {
        using (PushProperties("ReconnectAfterUnansweredRunMode"))
        {
            LogWarning("The amp has not answered {Count} run mode queries, reconnecting", MaxUnansweredRunModeQueries);
            CommunicationState = CommunicationState.Error;
            _client.Reconnect();
        }
    }

    private void RunModeAnswered()
    {
        _runModeQueries.Answered();
        ResolveIssue(UnansweredRunModeIssueKey);
    }

    private bool IsRunModeNormal()
    {
        lock (_lock)
            return _runModeNormal && CommunicationClient.ConnectionState == ConnectionState.Connected;
    }

    private void Sync()
    {
        lock (_lock)
            _owedGets.Clear();
        Send(AlertStatusQuery);
        SendOwedReads();
        RequestLimiter();
        Get(PowerParameter.Address);
        foreach (var address in RegisteredControlAddresses())
            Get(address);
    }

    private List<string> RegisteredControlAddresses() =>
    [
        .._controls.LevelNames().Select(name => Address(ChannelElement, ChannelOf(name)!.Value, AttParameter)),
        .._controls.MuteNames().Select(name => Address(ChannelElement, ChannelOf(name)!.Value, MuteParameter)),
        .._controls.StringNames().Select(name => StringControls[name].Address)
    ];

    private Fader NewLevel(VolumeLevelHandler handler)
    {
        var level = new Fader(handler, _faderCurve);
        level.SetMinGain(MinAttenuationDb);
        level.SetMaxGain(MaxAttenuationDb);
        return level;
    }

    private void SendOwedReads()
    {
        List<Read> owed;
        lock (_lock)
        {
            owed = Reads.Where(read => read != LimiterRead && _readStates[read] == ReadState.Owed).ToList();
            foreach (var read in owed)
                _readStates[read] = ReadState.Asked;
        }
        foreach (var read in owed)
            Send(read.Command);
    }

    private void SendOwedGets()
    {
        List<string> owed;
        lock (_lock)
        {
            owed = _owedGets.ToList();
            _owedGets.Clear();
        }
        foreach (var address in owed)
            Get(address);
    }

    private void OweGet(string address)
    {
        lock (_lock)
            _owedGets.Add(address);
    }

    private void RequestLimiter()
    {
        lock (_lock)
        {
            if (_readStates[LimiterRead] == ReadState.NotSupported)
                return;
        }
        Send(LimiterRead.Command);
    }

    private void Get(string address) => Send($"{GetCommand} {address} 0 0");

    private void Send(string command) => CommunicationClient.Send($"{command}{LineEnd}");

    private void RequestWritten(string request)
    {
        var tokens = Tokens(request);
        if (tokens.Count < 2)
            return;
        var dropped = _sent.Add(new XmvCommand(tokens[0], tokens[1], request.TrimEnd('\r', LineEnd)));
        if (dropped == null)
            return;
        using (Trigger.Resume(dropped.TriggerId))
        using (PushProperties("RequestWritten"))
            LogWarning("Forgot {Command}: more than {Max} commands are awaiting a reply", dropped.Command.Text, SentCommandTracker<XmvCommand>.MaxCommands);
    }

    private void ReportMissedReplies()
    {
        foreach (var missed in _sent.TakeMissed())
        {
            using (Trigger.Resume(missed.TriggerId))
            using (PushProperties("ReportMissedReplies"))
            {
                var command = missed.Command;
                if (command.Key == RunModeQuery)
                    continue;
                if (command.Key == AlertStatusQuery)
                {
                    RaiseMomentaryIssue($"The amp did not answer {AlertStatusQuery} within {ReplyTimeout.TotalSeconds} s",
                        UnansweredAlertStatusIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);
                    continue;
                }
                if (ReadFor(command.Key) is { } read)
                {
                    ReadMissed(read, $"no reply within {ReplyTimeout.TotalSeconds} s");
                    continue;
                }
                if (command.Name == GetCommand && ParametersByAddress.ContainsKey(command.Target))
                {
                    OweGet(command.Target);
                    LogWarning("No reply from the amp to {Command} within {Timeout} s, asking again at the next poll", command.Text, ReplyTimeout.TotalSeconds);
                    continue;
                }
                LogWarning("No reply from the amp to {Command} within {Timeout} s", command.Text, ReplyTimeout.TotalSeconds);
            }
        }
    }

    private void HandleResponse(string response)
    {
        List<string> lines = [];
        string? dropped = null;
        lock (_lock)
        {
            var received = (_unterminated + response).Split(LineEnd);
            _unterminated = received[^1];
            lines.AddRange(received[..^1].Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0));
            if (_unterminated.Length > MaxUnterminatedLength)
            {
                dropped = $"Dropped {_unterminated.Length} characters from the amp with no line end: {Bytes.Escaped(Bytes.FromString(_unterminated), ShownUnterminatedLength)}";
                _unterminated = string.Empty;
            }
        }
        using (PushProperties("HandleResponse"))
        {
            if (dropped != null)
                RaiseMomentaryIssue(dropped, UnreadableReplyIssueKey);
            foreach (var line in lines)
            {
                try
                {
                    HandleLine(line);
                }
                catch (Exception e)
                {
                    LogException(e, $"Handling a line from the amp threw: {line}");
                }
            }
        }
    }

    private void HandleLine(string line)
    {
        var tokens = Tokens(line);
        switch (tokens[0])
        {
            case OkReply:
            case OkAdjustedReply:
                HandleOk(tokens, line);
                return;
            case ErrorReply:
                HandleError(tokens, line);
                return;
            case Notification:
                HandleNotification(tokens, line);
                return;
            default:
                ShowUnreadable(line);
                return;
        }
    }

    private void HandleOk(List<string> tokens, string line)
    {
        if (tokens.Count < 3)
        {
            ShowUnreadable(line);
            return;
        }
        var sent = _sent.TakeReply(command => command.Name == tokens[1] && command.Target == tokens[2]);
        using (Trigger.Resume(sent?.TriggerId))
        using (PushProperties("HandleOk"))
        {
            CommunicationState = CommunicationState.Okay;
            switch (tokens[1])
            {
                case DeviceStatusCommand:
                    ShowDeviceStatus(tokens, line, true);
                    return;
                case DeviceInfoCommand:
                    ShowDeviceInfo(tokens, line);
                    return;
                case GetCommand:
                case SetCommand:
                    ApplyParameter(tokens, line);
                    if (tokens[0] == OkAdjustedReply)
                        LogInformation("The amp adjusted {Command} to keep it in range: {Reply}", sent?.Command.Text, line);
                    return;
                case MeterStartCommand when tokens[2] == LimiterMeterAddress:
                    LimiterStarted();
                    return;
                case EventCommand:
                    ShowAlertLog(tokens, line);
                    return;
                default:
                    LogDebug("A reply this driver doesn't follow: {Reply}", line);
                    return;
            }
        }
    }

    private void HandleError(List<string> tokens, string line)
    {
        if (tokens.Count < 3)
        {
            ShowUnreadable(line);
            return;
        }
        var code = tokens[2];
        var sent = _sent.TakeError(command => command.Name == tokens[1]);
        using (Trigger.Resume(sent?.TriggerId))
        using (PushProperties("HandleError"))
        {
            CommunicationState = CommunicationState.Okay;
            if (sent == null)
            {
                RaiseMomentaryIssue($"The amp sent {line} when no {tokens[1]} command was awaiting a reply", ErrorReplyIssueKey,
                    IssueSeverity.Minor, EscalateAfterRepeats);
                return;
            }

            var command = sent.Command;
            if (ReadFor(command.Key) is { } read)
            {
                if (RejectionCodes.Contains(code))
                    ReadNotSupported(read, line);
                else
                    ReadMissed(read, line);
                return;
            }
            if (command.Key == RunModeQuery)
                RunModeAnswered();
            if (command.Key == AlertStatusQuery)
                ResolveIssue(UnansweredAlertStatusIssueKey);
            if (command.Name == SetCommand)
            {
                RaiseMomentaryIssue($"The amp rejected {command.Text} with {line}", RejectedCommandIssueKey);
                return;
            }
            if (command.Name == GetCommand && ParametersByAddress.TryGetValue(command.Target, out var parameter))
            {
                if (RejectionCodes.Contains(code) && ControlNames(parameter) is { Count: > 0 } names)
                {
                    foreach (var name in names)
                        _controls.ShowBadControl(name, $"{BadControlIssuePrefix}{name}", $"The amp rejected the read of {name} ({command.Text}) with {line}");
                    return;
                }
                if (!RejectionCodes.Contains(code))
                    OweGet(command.Target);
            }
            RaiseMomentaryIssue($"The amp answered {command.Text} with {line}", ErrorReplyIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);
        }
    }

    private void HandleNotification(List<string> tokens, string line)
    {
        if (tokens.Count < 3)
        {
            ShowUnreadable(line);
            return;
        }
        switch (tokens[1])
        {
            case DeviceStatusCommand:
                ShowDeviceStatus(tokens, line, false);
                return;
            case SetCommand:
                ApplyParameter(tokens, line);
                return;
            case MeterNotification when tokens[2] == LimiterMeterAddress:
                ShowLimiters(tokens, line);
                return;
            default:
                LogDebug("A notification this driver doesn't follow: {Notification}", line);
                return;
        }
    }

    private void ShowDeviceStatus(List<string> tokens, string line, bool isReply)
    {
        var value = tokens.Count > 3 ? tokens[3] : null;
        switch (tokens[2])
        {
            case RunModeOption:
                if (isReply)
                    RunModeAnswered();
                ShowRunMode(value, line);
                return;
            case AlertOption:
                if (isReply)
                {
                    ResolveIssue(ErrorReplyIssueKey);
                    ResolveIssue(UnansweredAlertStatusIssueKey);
                }
                ShowAlertStatus(value, line);
                return;
            case SamplingRateOption:
                ShowRead(SamplingRateRead, value, line, value == UnknownSamplingRate ? DetailTone.Warning : DetailTone.Normal);
                return;
            case WordClockOption:
                ShowRead(WordClockRead, value, line, value == WordClockUnlocked ? DetailTone.Warning : DetailTone.Normal);
                return;
            default:
                LogDebug("A device status this driver doesn't follow: {Line}", line);
                return;
        }
    }

    private void ShowDeviceInfo(List<string> tokens, string line)
    {
        if (ReadFor($"{DeviceInfoCommand} {tokens[2]}") is not { } read)
        {
            LogDebug("Device information this driver doesn't follow: {Reply}", line);
            return;
        }
        ShowRead(read, tokens.Count > 3 ? tokens[3] : null, line, DetailTone.Normal);
    }

    private void ShowRunMode(string? runMode, string line)
    {
        if (runMode == null)
        {
            ShowUnreadable(line);
            return;
        }
        if (runMode != NormalRunMode)
        {
            lock (_lock)
                _runModeNormal = false;
            SetDetail(RunModeLabel, runMode, DetailTone.Error);
            RaiseOngoingIssue(RunModeIssueKey, $"The amp is in {runMode} run mode, not {NormalRunMode}, so it doesn't take control: {line}",
                IssueSeverity.Critical);
            return;
        }

        SetDetail(RunModeLabel, runMode);
        ResolveIssue(RunModeIssueKey);
        bool started;
        lock (_lock)
        {
            started = !_runModeNormal;
            _runModeNormal = true;
        }
        if (started)
            Sync();
    }

    private void ShowAlertStatus(string? status, string line)
    {
        if (status == null)
        {
            ShowUnreadable(line);
            return;
        }
        lock (_lock)
            _alertStatusNone = status == NoAlerts;
        if (status == NoAlerts)
        {
            SetDetail(AlertLabel, status);
            foreach (var alert in GetOngoingIssues().Where(issue => issue.Key.StartsWith(AlertIssuePrefix) || issue.Key == UnreadableAlertIssueKey))
                ResolveIssue(alert.Key);
            return;
        }

        var parsed = AlertParser.Match(status);
        if (!IsReadableAlert(parsed))
        {
            SetDetail(AlertLabel, status, DetailTone.Error);
            RaiseOngoingIssue(UnreadableAlertIssueKey, $"The amp reported an alert this driver can't read: {status}");
            return;
        }

        var on = parsed.Groups["state"].Value == AlertOn;
        SetDetail(AlertLabel, status, !on ? DetailTone.Normal : parsed.Groups["type"].Value == WarningAlert ? DetailTone.Warning : DetailTone.Error);
        ApplyAlert(parsed, status);
    }

    private void ApplyAlert(Match alert, string raw)
    {
        var type = alert.Groups["type"].Value;
        var number = alert.Groups["number"].Value;
        var key = AlertKey(alert);
        if (AlertTime(alert) is { } at)
        {
            lock (_lock)
                _latestAlertTime = _latestAlertTime > at ? _latestAlertTime : at;
        }
        if (alert.Groups["state"].Value == AlertOff)
        {
            ResolveIssue(key);
            return;
        }

        switch (type)
        {
            case FaultAlert:
                RaiseOngoingIssue(key, $"The amp reports fault x{number}: {raw}", AlertSeverity(number, IssueSeverity.Critical));
                return;
            case ErrorAlert:
                RaiseOngoingIssue(key, $"The amp reports error x{number}: {raw}", AlertSeverity(number, IssueSeverity.Major));
                return;
            case WarningAlert:
                RaiseOngoingIssue(key, $"The amp reports warning x{number}: {raw}", AlertSeverity(number, IssueSeverity.Minor));
                return;
            default:
                bool repeated;
                lock (_lock)
                {
                    repeated = _momentaryAlerts.GetValueOrDefault(key) == raw;
                    _momentaryAlerts[key] = raw;
                }
                if (!repeated)
                    RaiseMomentaryIssue($"The amp reports alert x{number} of a type this driver doesn't know ({type}): {raw}", key);
                return;
        }
    }

    private static IssueSeverity AlertSeverity(string number, IssueSeverity yamahaSeverity) =>
        CriticalAlertNumbers.Contains(number) ? IssueSeverity.Critical : yamahaSeverity;

    private static string AlertKey(Match alert) =>
        $"{AlertIssuePrefix}{alert.Groups["number"].Value.ToLowerInvariant()}:{alert.Groups["message"].Value}";

    private static bool IsReadableAlert(Match alert) =>
        alert.Success && alert.Groups["state"].Value is AlertOn or AlertOff;

    private static DateTime? AlertTime(Match alert) =>
        DateTime.TryParseExact(alert.Groups["at"].Value, AlertTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private void ShowAlertLog(List<string> tokens, string line)
    {
        var body = OptionText(tokens, line);
        switch (tokens[2])
        {
            case AlertLogCountTarget:
                AlertLogCounted(body, line);
                return;
            case AlertLogListTarget:
            case AlertLogEntryTarget:
                AlertLogEntriesRead(body, line);
                return;
            default:
                LogDebug("An event reply this driver doesn't follow: {Reply}", line);
                return;
        }
    }

    private void AlertLogCounted(string body, string line)
    {
        var parsed = AlertLogCountParser.Match(body);
        if (!parsed.Success || !int.TryParse(parsed.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            SetDetail(AlertLogLabel, line, DetailTone.Warning);
            ReadMissed(AlertLogRead, line);
            return;
        }
        lock (_lock)
        {
            _alertLog.Clear();
            _alertLogCount = count;
        }
        if (count == 0)
            ReconcileAlerts();
        else
            RequestAlertLog(0);
    }

    private void RequestAlertLog(int first)
    {
        int count;
        lock (_lock)
        {
            count = _alertLogCount;
            _alertLogNext = first;
        }
        if (count == 1)
        {
            Send($"{EventCommand} {AlertLogEntryTarget} \"logindex=0\"");
            return;
        }
        var last = Math.Min(first + MaxAlertLogEntriesPerQuery - 1, count - 1);
        Send($"{EventCommand} {AlertLogListTarget} \"logindex={Math.Min(first, last - 1)}-{last}\"");
    }

    private void AlertLogEntriesRead(string body, string line)
    {
        var entries = AlertLogEntryParser.Matches(body);
        int next;
        int count;
        bool progressed;
        lock (_lock)
        {
            foreach (Match entry in entries)
                if (int.TryParse(entry.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    _alertLog[index] = entry.Groups["alert"].Value;
            count = _alertLogCount;
            next = _alertLog.Count == 0 ? 0 : _alertLog.Keys.Last() + 1;
            progressed = entries.Count > 0 && next > _alertLogNext;
        }
        if (!progressed)
        {
            SetDetail(AlertLogLabel, line, DetailTone.Warning);
            ReadMissed(AlertLogRead, line);
            return;
        }
        if (next < count)
            RequestAlertLog(next);
        else
            ReconcileAlerts();
    }

    private void ReconcileAlerts()
    {
        List<KeyValuePair<int, string>> entries;
        int count;
        DateTime? seen;
        bool noAlerts;
        lock (_lock)
        {
            entries = _alertLog.ToList();
            count = _alertLogCount;
            seen = _reconciledThisConnection ? _latestAlertTime : _latestAlertTimeAtConnect;
            noAlerts = _alertStatusNone;
        }

        List<LoggedAlert> logged = [];
        foreach (var (index, raw) in entries)
        {
            var alert = AlertParser.Match(raw);
            if (IsReadableAlert(alert) && AlertTime(alert) is { } at)
                logged.Add(new LoggedAlert(index, AlertKey(alert), alert.Groups["state"].Value == AlertOn, at, alert, raw));
            else
                LogWarning("An alert log entry this driver can't read: {Entry}", raw);
        }

        var direction = LogDirection(logged);
        foreach (var latest in logged.GroupBy(entry => entry.Key).Select(alerts => Latest(alerts, direction)))
        {
            if (!latest.On)
                ResolveIssue(latest.Key);
            else if (!noAlerts && latest.At > seen)
                ApplyAlert(latest.Alert, latest.Raw);
        }

        lock (_lock)
        {
            if (logged.Count > 0)
            {
                var newest = logged.Max(entry => entry.At);
                _latestAlertTime = _latestAlertTime > newest ? _latestAlertTime : newest;
            }
            _reconciledThisConnection = true;
        }
        var unreadable = entries.Count - logged.Count;
        var order = direction switch
        {
            1 => "oldest first",
            -1 => "newest first",
            _ => "order unknown"
        };
        ShowRead(AlertLogRead, unreadable == 0 ? $"{count} entries, {order}" : $"{count} entries, {order}, {unreadable} unreadable", "",
            unreadable == 0 ? DetailTone.Normal : DetailTone.Warning);
    }

    private static int LogDirection(List<LoggedAlert> logged)
    {
        var later = 0;
        var earlier = 0;
        for (var entry = 1; entry < logged.Count; entry++)
        {
            if (logged[entry].At > logged[entry - 1].At)
                later++;
            else if (logged[entry].At < logged[entry - 1].At)
                earlier++;
        }
        return later > earlier ? 1 : earlier > later ? -1 : 0;
    }

    private static LoggedAlert Latest(IEnumerable<LoggedAlert> alerts, int direction) => direction == 0
        ? alerts.OrderBy(entry => entry.At).ThenBy(entry => entry.On).Last()
        : alerts.MaxBy(entry => entry.Index * direction)!;

    private static string OptionText(List<string> tokens, string line)
    {
        var target = line.IndexOf(tokens[2], StringComparison.Ordinal);
        if (target < 0)
            return string.Empty;
        var option = line[(target + tokens[2].Length)..].Trim();
        return option.Length >= 2 && option[0] == '"' && option[^1] == '"' ? Unescaped(option[1..^1]) : option;
    }

    private void ShowLimiters(List<string> tokens, string line)
    {
        var values = tokens.Skip(4).ToList();
        var readings = values.Select(value => byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var reading) ? reading : (byte?)null)
            .ToList();
        if (readings.Count == 0 || readings.Contains(null))
        {
            ShowUnreadable(line);
            return;
        }
        List<int> engaging = [];
        List<char> engaged = [];
        lock (_lock)
        {
            for (var channel = 0; channel < Math.Min(readings.Count, MaxChannels); channel++)
            {
                var limiting = readings[channel] != 0;
                if (limiting && !_limiting[channel])
                    engaging.Add(channel);
                if (limiting)
                    engaged.Add(ChannelLetters[channel]);
                _limiting[channel] = limiting;
            }
            _readStates[LimiterRead] = ReadState.Answered;
            _missedReads.Remove(LimiterRead);
        }
        ResolveIssue(LimiterRead.IssueKey);
        foreach (var channel in engaging)
            RaiseMomentaryIssue($"Output {ChannelLetters[channel]}'s limiter engaged ({LimiterMeterAddress} reads {values[channel]})",
                $"{LimiterIssuePrefix}{ChannelLetters[channel]}");
        if (engaged.Count == 0)
            SetDetail(LimiterLabel, "Off");
        else
            SetDetail(LimiterLabel, $"Engaged: {string.Join(", ", engaged)}", DetailTone.Warning);
    }

    private void LimiterStarted()
    {
        lock (_lock)
            _missedReads.Remove(LimiterRead);
        ResolveIssue(LimiterRead.IssueKey);
    }

    private void ApplyParameter(List<string> tokens, string line)
    {
        if (!ParametersByAddress.TryGetValue(tokens[2], out var parameter))
        {
            LogDebug("A parameter this driver doesn't follow: {Line}", line);
            return;
        }
        if (tokens.Count < 6 || !int.TryParse(tokens[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
        {
            ShowUnreadable(line);
            return;
        }

        var display = tokens.Count > 6 ? tokens[6] : null;
        foreach (var name in ControlNames(parameter))
            _controls.ControlReported(name);
        switch (parameter.Kind)
        {
            case ParameterKind.Level:
                CheckTheLevelScale(parameter.Channel, raw, display, line);
                _controls.FindLevel(ChannelLetters[parameter.Channel].ToString())?.SetVolumeFromDb(raw / RawPerDb);
                return;
            case ParameterKind.Mute:
                if (_controls.FindMute(ChannelLetters[parameter.Channel].ToString()) is { } mute)
                    mute.MuteState = MuteStateOf(raw, line);
                return;
            case ParameterKind.Power:
                ShowPower(raw, display, line);
                return;
            default:
                foreach (var name in ControlNames(parameter))
                    if (_controls.FindString(name) is { } value)
                        value.Value = LabelOf(parameter.Kind, raw);
                return;
        }
    }

    private void CheckTheLevelScale(int channel, int raw, string? display, string line)
    {
        if (display == null)
            return;
        var letter = ChannelLetters[channel];
        if (!double.TryParse(display, NumberStyles.Float, CultureInfo.InvariantCulture, out var shown))
        {
            SetDetail(LevelDisplayLabel, $"{letter}: \"{display}\" for value {raw} isn't a number, so it isn't checked", DetailTone.Warning);
            return;
        }
        var read = raw / RawPerDb;
        var key = $"{LevelScaleIssuePrefix}{letter}";
        var agrees = double.IsNegativeInfinity(shown) ? raw == NegativeInfinityRaw : Math.Abs(shown - read) <= LevelDisplayTolerance;
        if (agrees)
        {
            SetDetail(LevelDisplayLabel, $"{letter}: \"{display}\" for value {raw}");
            ResolveIssue(key);
            return;
        }
        SetDetail(LevelDisplayLabel, $"{letter}: \"{display}\" for value {raw}", DetailTone.Warning);
        RaiseOngoingIssue(key,
            $"Channel {letter}'s ATT value {raw} reads as {read.ToString("0.00", CultureInfo.InvariantCulture)} dB, but the amp shows \"{display}\": {line}");
    }

    private void ShowPower(int raw, string? display, string line)
    {
        var state = PowerStateOf(raw, line);
        var contradicts = display != null
                          && ((state == PowerState.On && display.Equals(StandbyDisplay, StringComparison.OrdinalIgnoreCase))
                              || (state == PowerState.Off && display.Equals(PowerOnDisplay, StringComparison.OrdinalIgnoreCase)));
        SetDetail(PowerLabel, display == null ? $"value {raw}" : $"{display} (value {raw})", contradicts ? DetailTone.Warning : DetailTone.Normal);
        if (contradicts)
            RaiseOngoingIssue(PowerValueIssueKey, $"The amp's power value {raw} reads as {state}, but the amp shows \"{display}\": {line}");
        else if (display != null)
            ResolveIssue(PowerValueIssueKey);
        PowerState = state;
    }

    private MuteState MuteStateOf(int raw, string line)
    {
        switch (raw)
        {
            case MutedValue:
                return MuteState.On;
            case UnmutedValue:
                return MuteState.Off;
            default:
                LogWarning("A mute value this driver doesn't know: {Line}", line);
                return MuteState.Unknown;
        }
    }

    private PowerState PowerStateOf(int raw, string line)
    {
        switch (raw)
        {
            case PowerOnValue:
                return PowerState.On;
            case StandbyValue:
                return PowerState.Off;
            default:
                LogWarning("A power value this driver doesn't know: {Line}", line);
                return PowerState.Unknown;
        }
    }

    private static string LabelOf(ParameterKind kind, int raw)
    {
        var labels = Labels(kind);
        return raw >= 0 && raw < labels.Length ? labels[raw] : raw.ToString(CultureInfo.InvariantCulture);
    }

    private static string[] Labels(ParameterKind kind) => kind switch
    {
        ParameterKind.Polarity => PolarityValues,
        ParameterKind.ChannelInput => ChannelInputValues,
        _ => InputValues
    };

    private static List<string> ControlNames(XmvParameter parameter) => parameter.Kind switch
    {
        ParameterKind.Level or ParameterKind.Mute => [ChannelLetters[parameter.Channel].ToString()],
        ParameterKind.Power => [],
        _ => StringControls.Where(control => control.Value == parameter).Select(control => control.Key).ToList()
    };

    private void ShowRead(Read read, string? value, string line, DetailTone tone)
    {
        if (value == null)
        {
            SetDetail(read.Label, line, DetailTone.Warning);
            ReadMissed(read, line);
            return;
        }
        lock (_lock)
        {
            _readStates[read] = ReadState.Answered;
            _missedReads.Remove(read);
        }
        SetDetail(read.Label, value.Length == 0 ? line : value, tone);
        ResolveIssue(read.IssueKey);
    }

    private void ReadNotSupported(Read read, string line)
    {
        lock (_lock)
        {
            _readStates[read] = ReadState.NotSupported;
            _missedReads.Remove(read);
        }
        var notSupported = $"Not supported: {line}";
        if (!Details.Contains(new DeviceDetail(read.Label, notSupported)))
            LogInformation("The amp does not support {Command}: {Reply}", read.Key, line);
        SetDetail(read.Label, notSupported);
        ResolveIssue(read.IssueKey);
    }

    private void ReadMissed(Read read, string what)
    {
        int missed;
        lock (_lock)
        {
            missed = _missedReads.GetValueOrDefault(read) + 1;
            _missedReads[read] = missed;
            if (_readStates[read] != ReadState.NotSupported)
                _readStates[read] = ReadState.Owed;
        }
        if (missed < MaxMissedReads)
        {
            LogInformation("The amp gave {Reply} to {Command}, asking again at the next poll ({Missed} of {MaxMissedReads})",
                what, read.Key, missed, MaxMissedReads);
            return;
        }
        RaiseOngoingIssue(read.IssueKey, $"The amp did not give a real reply to {read.Key} {MaxMissedReads} times in a row, the last was {what}",
            IssueSeverity.Minor);
    }

    private static Read? ReadFor(string key) =>
        key is AlertLogListKey or AlertLogEntryKey ? AlertLogRead : Array.Find(Reads, read => read.Key == key);

    private void ShowUnreadable(string line) => RaiseMomentaryIssue($"Unreadable line from the amp: {line}", UnreadableReplyIssueKey);

    private void ShowUnknownControlName(string controlName, string controlType)
    {
        using (PushProperties("AddControl"))
            _controls.ShowBadControl(controlName, $"{BadControlIssuePrefix}{controlName}", controlType == StringControlType
                ? $"\"{controlName}\" isn't an XMV string control, so it is not sent to the amp. They are \"<channel>{PolaritySuffix}\", \"<channel> {InputSelectName}\" and \"{InputSelectName}\", where the channel is a letter A to H"
                : $"The {controlType} control name \"{controlName}\" isn't a channel letter A to H, so it is not sent to the amp");
    }

    private static int? ChannelOf(string controlName) =>
        controlName.Length == 1 && ChannelLetters.IndexOf(controlName[0]) is >= 0 and var channel ? channel : null;

    private static string Address(int element, int channel, int parameter) =>
        $"MTX:mem_{MemoryNumber}/{SignalProcessingId}/{element}/{channel}/0/{parameter}/0";

    private static List<string> Tokens(string line) =>
        TokenParser.Matches(line)
            .Select(token => token.Groups["quoted"].Success ? Unescaped(token.Groups["quoted"].Value) : token.Groups["bare"].Value)
            .ToList();

    private static string Unescaped(string quoted)
    {
        var text = new StringBuilder(quoted.Length);
        for (var i = 0; i < quoted.Length; i++)
        {
            if (quoted[i] == '\\' && i + 1 < quoted.Length)
                i++;
            text.Append(quoted[i]);
        }
        return text.ToString();
    }
}
