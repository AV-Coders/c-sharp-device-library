using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public enum ExtronSmp351UsbPort
{
    Unknown,
    Front,
    Rear,
    RCP
}

public class ExtronSmp351 : Recorder
{
    public static readonly ushort DefaultPort = 22023;
    private const int RecordingStateField = 1;
    private const int DestinationField = 2;
    private const int FreeSpaceField = 3;
    private const int RecordTimeField = 4;
    private const int TimeRemainingField = 5;
    private const int UsbNameField = 0;
    private const int UsbUsedField = 1;
    private const int UsbTotalField = 2;
    private const int UsbFreeField = 3;
    private const int UsbTimeRemainingField = 4;
    private const int TimeParts = 3;
    private const uint HealthReadPolls = 10;
    private const uint UsbStatusPolls = 60;
    private const int MaxMissedReplies = 3;
    private const string NoReply = "no reply";
    private const string ErrorReplyIssueKey = "error-reply";
    private const string AlarmIssuePrefix = "alarm:";
    private const string FirmwareReplyTag = "Bld";
    private const string PartNumberReplyTag = "Pno";
    private const string ModelReplyTag = "Inf01*";
    private const string SerialReplyTag = "Inf99*";
    private const string MacReplyTag = "Inf98*";
    private const string TemperatureReplyTag = "20Stat";
    private const string AlarmsReplyTag = "Inf39*";
    private const string NoActiveAlarms = "None active";
    private const string DisabledAlarmLevel = "Disabled";
    private const string AlarmNameGroup = "name";
    private const string AlarmLevelGroup = "level";
    private const string FirmwareLabel = "Firmware";
    private const string PartNumberLabel = "Part Number";
    private const string ModelLabel = "Model";
    private const string SerialLabel = "Serial";
    private const string MacLabel = "MAC";
    private const string TemperatureLabel = "Temperature";
    private const string AlarmsLabel = "Alarms";
    private const string FreeSpaceLabel = "Free Space (KB)";
    private const string TimeRemainingLabel = "Record Time Remaining";
    public const string EscapeHeader = "\x1b";
    private static readonly TimeSpan VerboseModeDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan UsbStatusQuerySpacing = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);
    private static readonly char[] LineEndings = ['\r', '\n'];
    private static readonly SmpCommand VerboseModeCommand = new($"{EscapeHeader}3CV\r", "Vrb3");
    private static readonly SmpCommand InformationQuery = new("I", "Inf*");
    private static readonly SmpCommand FrontUsbQuery = new("56I", "Inf56");
    private static readonly SmpCommand RearUsbQuery = new("57I", "Inf57");
    private static readonly SmpCommand RcpUsbQuery = new("58I", "Inf58");
    private static readonly SmpCommand RecordCommand = new($"{EscapeHeader}Y1RCDR\r", "RcdrY1");
    private static readonly SmpCommand StopCommand = new($"{EscapeHeader}Y0RCDR\r", "RcdrY0");
    private static readonly SmpCommand PauseCommand = new($"{EscapeHeader}Y2RCDR\r", "RcdrY2");
    private static readonly SmpCommand MarkerCommand = new($"{EscapeHeader}BRCDR\r", "RcdrB");
    private static readonly SmpCommand[] RecorderCommands = [RecordCommand, StopCommand, PauseCommand, MarkerCommand];
    private static readonly SmpRead FirmwareRead = new("*Q", FirmwareReplyTag, "firmware-read", FirmwareLabel);
    private static readonly SmpRead PartNumberRead = new("N", PartNumberReplyTag, "part-number-read", PartNumberLabel);
    private static readonly SmpRead ModelRead = new("1I", ModelReplyTag, "model-read", ModelLabel);
    private static readonly SmpRead SerialRead = new("99I", SerialReplyTag, "serial-read", SerialLabel);
    private static readonly SmpRead MacRead = new("98I", MacReplyTag, "mac-read", MacLabel);
    private static readonly SmpRead TemperatureRead = new($"{EscapeHeader}20STAT\r", TemperatureReplyTag, "temperature-read", TemperatureLabel);
    private static readonly SmpRead AlarmsRead = new("39I", AlarmsReplyTag, "alarms-read", AlarmsLabel);
    private static readonly SmpRead[] IdentityReads = [FirmwareRead, PartNumberRead, ModelRead, SerialRead, MacRead];
    private static readonly SmpRead[] HealthReads = [TemperatureRead, AlarmsRead];
    private const string InvalidPortNumber = "E12";
    private const string InvalidCommandForSignalType = "E17";
    private const string SystemTimedOut = "E18";
    private const string Busy = "E22";
    private const string PrivilegeViolation = "E24";
    private const string MaximumConnectionsExceeded = "E26";
    private const string BadFileName = "E28";
    private static readonly Regex ErrorReplyParser = new(@"^E\d+\s*$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex AlarmParser = new($@"\[name:(?<{AlarmNameGroup}>[^\]]+)\],\[level:(?<{AlarmLevelGroup}>[^\]]+)\]",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Dictionary<string, IssueSeverity> AlarmSeverities = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Notify", IssueSeverity.Major },
        { "Display", IssueSeverity.Minor },
        { "Log", IssueSeverity.Minor }
    };
    private static readonly Dictionary<string, string> ErrorMessages = new()
    {
        { ExtronSis.InvalidCommand, "Unrecognized command" },
        { InvalidPortNumber, "Invalid port number" },
        { ExtronSis.InvalidParameter, "Invalid parameter" },
        { ExtronSis.NotValidForThisConfiguration, "Not valid for this configuration" },
        { InvalidCommandForSignalType, "Invalid command for signal type" },
        { SystemTimedOut, "System timed out" },
        { Busy, "Busy" },
        { PrivilegeViolation, "Privilege violation" },
        { MaximumConnectionsExceeded, "Maximum connections exceeded" },
        { BadFileName, "Bad file name or file not found" }
    };
    private readonly ThreadWorker _pollWorker;
    private readonly TimeProvider _timeProvider;
    private readonly SentCommandTracker<SmpCommand> _sent;
    private readonly object _verboseModeTimerLock = new();
    private ITimer? _verboseModeTimer;
    private readonly Regex _responseParser = new("<([^>]*)>", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private readonly ConcurrentQueue<SmpRead> _pendingReads = new();
    private readonly HashSet<SmpRead> _unsupportedReads = [];
    private readonly Dictionary<SmpRead, int> _missedReplies = new();
    private readonly object _readsLock = new();
    private string _storageReply = string.Empty;
    private ConnectionState _frontUsbConnectionState = ConnectionState.Unknown;
    private ConnectionState _rearUsbConnectionState = ConnectionState.Unknown;
    private ConnectionState _rcpUsbConnectionState = ConnectionState.Unknown;
    private TimeSpan _frontUsbTimeRemaining = TimeSpan.Zero;
    private TimeSpan _rearUsbTimeRemaining = TimeSpan.Zero;
    private TimeSpan _rcpUsbTimeRemaining = TimeSpan.Zero;
    public ConnectionStateHandler? FrontUsbConnectionStateHandlers;
    public ConnectionStateHandler? RearUsbConnectionStateHandlers;
    public ConnectionStateHandler? RcpUsbConnectionStateHandlers;
    public TimeSpanHandler? FrontUsbRemainingRecordTimeHandlers;
    public TimeSpanHandler? RearUsbRemainingRecordTimeHandlers;
    public TimeSpanHandler? RcpUsbRemainingRecordTimeHandlers;
    private uint _counter = 0;

    public ConnectionState FrontUsbConnectionState
    {
        get => _frontUsbConnectionState;
        private set
        {
            if (_frontUsbConnectionState == value)
                return;
            _frontUsbConnectionState = value;
            FrontUsbConnectionStateHandlers?.Invoke(value);
        }
    }

    public ConnectionState RearUsbConnectionState
    {
        get => _rearUsbConnectionState;
        private set
        {
            if (_rearUsbConnectionState == value)
                return;
            _rearUsbConnectionState = value;
            RearUsbConnectionStateHandlers?.Invoke(value);
        }
    }

    public ConnectionState RcpUsbConnectionState
    {
        get => _rcpUsbConnectionState;
        private set
        {
            if (_rcpUsbConnectionState == value)
                return;
            _rcpUsbConnectionState = value;
            RcpUsbConnectionStateHandlers?.Invoke(value);
        }
    }

    public TimeSpan FrontUsbTimeRemaining
    {
        get => _frontUsbTimeRemaining;
        private set
        {
            if(_frontUsbTimeRemaining == value)
                return;
            _frontUsbTimeRemaining = value;
            FrontUsbRemainingRecordTimeHandlers?.Invoke(value);
        }
    }

    public TimeSpan RearUsbTimeRemaining
    {
        get => _rearUsbTimeRemaining;
        private set
        {
            if(_rearUsbTimeRemaining == value)
                return;
            _rearUsbTimeRemaining = value;
            RearUsbRemainingRecordTimeHandlers?.Invoke(value);
        }
    }

    public TimeSpan RcpUsbTimeRemaining
    {
        get => _rcpUsbTimeRemaining;
        private set
        {
            if(_rcpUsbTimeRemaining == value)
                return;
            _rcpUsbTimeRemaining = value;
            RcpUsbRemainingRecordTimeHandlers?.Invoke(value);
        }
    }

    public ExtronSmp351(CommunicationClient communicationClient, ulong memoryLowKBytes, ulong memoryFullKBytes, string name, int pollTime = 1000)
        : this(communicationClient, memoryLowKBytes, memoryFullKBytes, name, pollTime, null)
    {
    }

    internal ExtronSmp351(CommunicationClient communicationClient, ulong memoryLowKBytes, ulong memoryFullKBytes, string name, int pollTime,
        TimeProvider? timeProvider)
        : base(name, communicationClient)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sent = new SentCommandTracker<SmpCommand>(ReplyTimeout, _timeProvider);
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromMilliseconds(pollTime), false, _timeProvider);
        _pollWorker.Restart();
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        _sent.Clear();
        if (connectionState != ConnectionState.Connected)
            return;
        lock (_verboseModeTimerLock)
        {
            _verboseModeTimer?.Dispose();
            _verboseModeTimer = _timeProvider.CreateTimer(_ => SetVerboseMode(), null, VerboseModeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SetVerboseMode()
    {
        try
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return;
            // This is required so all responses have a prefix.
            // Changing this will result in the device not returning the expected strings below
            Send(VerboseModeCommand);
            QueueTheReads();
        }
        catch (Exception e)
        {
            LogException(e, "Setting verbose mode failed");
        }
    }

    private void QueueTheReads()
    {
        _pendingReads.Clear();
        lock (_readsLock)
        {
            _unsupportedReads.Clear();
            _missedReplies.Clear();
        }
        foreach (var read in IdentityReads.Concat(HealthReads))
            _pendingReads.Enqueue(read);
    }

    private void QueueTheHealthReads()
    {
        lock (_readsLock)
        {
            foreach (var read in HealthReads.Where(read => !_unsupportedReads.Contains(read)))
                _pendingReads.Enqueue(read);
        }
    }

    private void SendTheNextRead()
    {
        if (_pendingReads.TryDequeue(out var read))
            Send(read);
    }

    private void Send(SmpCommand command)
    {
        if (_sent.Add(command) is { } dropped)
            using (PushProperties())
                LogWarning("Dropped {Command}: more than {Max} commands awaiting a reply",
                    ExtronSis.Printable(dropped.Command.Text), SentCommandTracker<SmpCommand>.MaxCommands);
        CommunicationClient.Send(command.Text);
    }

    private void ReportTheMissedReplies()
    {
        foreach (var missed in _sent.TakeMissed())
            using (Trigger.Resume(missed.TriggerId))
            using (PushProperties())
                ReportMissedReply(missed.Command);
    }

    private void ReportMissedReply(SmpCommand command)
    {
        if (command is SmpRead read)
            CountMiss(read, NoReply);
        else
            LogDebug("The SMP gave no reply to {Command} within {Timeout} s", ExtronSis.Printable(command.Text), ReplyTimeout.TotalSeconds);
    }

    private void CountMiss(SmpRead read, string result)
    {
        int missed;
        lock (_readsLock)
        {
            missed = _missedReplies.GetValueOrDefault(read) + 1;
            _missedReplies[read] = missed;
        }
        if (IdentityReads.Contains(read))
            _pendingReads.Enqueue(read);
        if (missed < MaxMissedReplies)
        {
            LogInformation("The SMP gave {Result} to {Command}, asking again later ({Missed} of {MaxMissedReplies})",
                result, ExtronSis.Printable(read.Text), missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(read.IssueKey,
            $"The SMP did not give a real reply to {ExtronSis.Printable(read.Text)} {MaxMissedReplies} times in a row, the last was {result}",
            IssueSeverity.Minor);
    }

    private void ResolveRead(SmpRead read)
    {
        lock (_readsLock)
            _missedReplies.Remove(read);
        ResolveIssue(read.IssueKey);
    }

    private void MarkUnsupported(SmpRead read)
    {
        lock (_readsLock)
            _unsupportedReads.Add(read);
    }

    protected override void DoRecord() => Send(RecordCommand);

    protected override void DoStop() => Send(StopCommand);

    protected override void DoPause() => Send(PauseCommand);

    public override void AddMarker() => Send(MarkerCommand);

    private void HandleResponse(string response)
    {
        if (ErrorReplyParser.IsMatch(response))
        {
            HandleErrorReply(response.Trim());
            return;
        }
        var sent = _sent.TakeReply(command => response.StartsWith(command.ReplyTag, StringComparison.Ordinal));
        using (Trigger.Resume(sent?.TriggerId))
            HandleReply(response);
    }

    private void HandleReply(string response)
    {
        if (response.StartsWith("Inf*"))
        {
            ProcessInformation(response);
        }
        else if (response.StartsWith("Inf56"))
        {
            ProcessUsbStatus(ExtronSmp351UsbPort.Front, response.Remove(0,6).Trim());

        }
        else if (response.StartsWith("Inf57"))
        {
            ProcessUsbStatus(ExtronSmp351UsbPort.Rear, response.Remove(0,6).Trim());
        }
        else if (response.StartsWith("Inf58"))
        {
            ProcessUsbStatus(ExtronSmp351UsbPort.RCP, response.Remove(0,6).Trim());
        }
        else if (response.StartsWith("RcdrN0*"))
        {
            switch (response.Remove(0, 7).Trim())
            {
                case "usbfront":
                    FrontUsbConnectionState = ConnectionState.Disconnected;
                    FrontUsbTimeRemaining = TimeSpan.Zero;
                    break;
                case "usbrear":
                    RearUsbConnectionState = ConnectionState.Disconnected;
                    RearUsbTimeRemaining = TimeSpan.Zero;
                    break;
                case "usbrcp":
                    RcpUsbConnectionState = ConnectionState.Disconnected;
                    RcpUsbTimeRemaining = TimeSpan.Zero;
                    break;
            }
        }
        else if (response.StartsWith("RcdrN1*"))
        {
            switch (response.Remove(0, 7).Trim())
            {
                case "usbfront":
                    FrontUsbConnectionState = ConnectionState.Connected;
                    break;
                case "usbrear":
                    RearUsbConnectionState = ConnectionState.Connected;
                    break;
                case "usbrcp":
                    RcpUsbConnectionState = ConnectionState.Connected;
                    break;
            }
        }
        else if (response.StartsWith("RecStart"))
        {
            TransportState = TransportState.PreparingToRecord;
        }
        else if (response.StartsWith("RcdrY"))
        {
            ProcessRecorderStatus(response);
        }
        else if (response.StartsWith(FirmwareReplyTag))
        {
            ShowValue(FirmwareRead, response);
        }
        else if (response.StartsWith(PartNumberReplyTag))
        {
            ShowValue(PartNumberRead, response);
        }
        else if (response.StartsWith(ModelReplyTag))
        {
            ShowValue(ModelRead, response);
        }
        else if (response.StartsWith(SerialReplyTag))
        {
            ShowValue(SerialRead, response);
        }
        else if (response.StartsWith(MacReplyTag))
        {
            ShowValue(MacRead, response);
        }
        else if (response.StartsWith(TemperatureReplyTag))
        {
            ShowValue(TemperatureRead, response);
        }
        else if (response.StartsWith(AlarmsReplyTag))
        {
            ShowAlarms(response);
        }
        else
        {
            return;
        }

        CommunicationState = CommunicationState.Okay;
    }

    private void ProcessInformation(string response)
    {
        var reply = response.Trim();
        var fields = _responseParser.Matches(reply.Remove(0, 4));
        ShowStorage(fields, reply);
        if (fields.Count <= RecordTimeField)
        {
            using (PushProperties())
                LogWarning("Unexpected information response {Response}", reply);
            return;
        }

        ProcessRecordingState(fields[RecordingStateField].Groups[1].Value);
        if (TransportState is TransportState.Recording or TransportState.RecordingPaused)
        {
            TimestampHandlers?.Invoke(fields[RecordTimeField].Groups[1].Value);
        }
    }

    private void ShowStorage(MatchCollection fields, string reply)
    {
        if (reply == _storageReply)
            return;
        _storageReply = reply;
        var freeSpace = StorageText(fields, FreeSpaceField);
        var timeRemaining = StorageText(fields, TimeRemainingField);
        if (freeSpace == null || timeRemaining == null)
            using (PushProperties())
                LogDebug("The information response does not give the free space and time remaining for each destination: {Response}", reply);
        SetDetail(FreeSpaceLabel, freeSpace ?? reply);
        SetDetail(TimeRemainingLabel, timeRemaining ?? reply);
    }

    private static string? StorageText(MatchCollection fields, int field)
    {
        if (fields.Count <= field)
            return null;
        var destinations = fields[DestinationField].Groups[1].Value.Split('*');
        var values = fields[field].Groups[1].Value.Split('*');
        return values.Length == destinations.Length
            ? string.Join(", ", destinations.Zip(values, (destination, value) => $"{destination}: {value}"))
            : null;
    }

    private void ProcessRecorderStatus(string response)
    {
        if (!int.TryParse(response.Remove(0, 5).Trim(), out var state))
        {
            using (PushProperties())
                LogWarning("Unexpected recorder status {Response}", response);
            return;
        }

        switch (state)
        {
            case 0:
                TransportState = TransportState.Stopped;
                TimestampHandlers?.Invoke(string.Empty);
                break;
            case 1:
                TransportState = TransportState.Recording;
                break;
            case 2:
                TransportState = TransportState.RecordingPaused;
                break;
        }
    }

    private void ProcessUsbStatus(ExtronSmp351UsbPort port, string status)
    {
        ConnectionState connectionState;
        TimeSpan reaminingTime = TimeSpan.Zero;
        if (status == "N/A")
        {
            connectionState = ConnectionState.Disconnected;
        }
        else
        {
            connectionState = ConnectionState.Connected;
            var parts = status.Split('*');
            var hms = parts.Length > UsbTimeRemainingField ? parts[UsbTimeRemainingField].Split(':') : [];
            if (hms.Length != TimeParts
                || !int.TryParse(hms[0], out var hours)
                || !int.TryParse(hms[1], out var minutes)
                || !int.TryParse(hms[2], out var seconds))
            {
                using (PushProperties())
                    LogWarning("Unexpected {Port} USB status {Status}", port, status);
                SetDetail($"{port} USB", status);
                return;
            }

            reaminingTime = new TimeSpan(hours, minutes, seconds);
        }

        switch (port)
        {
            case ExtronSmp351UsbPort.Front:
                FrontUsbConnectionState = connectionState;
                FrontUsbTimeRemaining = reaminingTime;
                break;
            case ExtronSmp351UsbPort.Rear:
                RearUsbConnectionState = connectionState;
                RearUsbTimeRemaining = reaminingTime;
                break;
            case ExtronSmp351UsbPort.RCP:
                RcpUsbConnectionState = connectionState;
                RcpUsbTimeRemaining = reaminingTime;
                break;
        }
        SetDetail($"{port} USB", string.Join(", ", status.Split(',').Select(UsbPartitionText)));
    }

    private static string UsbPartitionText(string partition)
    {
        var fields = partition.Split('*');
        return fields.Length > UsbFreeField
            ? $"{fields[UsbNameField]}: {fields[UsbUsedField]} used of {fields[UsbTotalField]}, {fields[UsbFreeField]} free"
            : partition;
    }

    private void ShowValue(SmpRead read, string response)
    {
        using (PushProperties())
        {
            var value = response[read.ReplyTag.Length..].Trim();
            SetDetail(read.Label, value.Length == 0 ? response.TrimEnd(LineEndings) : value);
            ResolveRead(read);
        }
    }

    private void ShowAlarms(string response)
    {
        using (PushProperties())
        {
            var payload = response[AlarmsReplyTag.Length..].Trim();
            if (payload.Length == 0)
            {
                SetDetail(AlarmsLabel, response.TrimEnd(LineEndings));
                ResolveRead(AlarmsRead);
                return;
            }
            var alarms = AlarmParser.Matches(payload)
                .Select(match => new ExtronAlarm(match.Groups[AlarmNameGroup].Value, match.Groups[AlarmLevelGroup].Value))
                .ToList();
            if (alarms.Count == 0 && payload != NoActiveAlarms)
            {
                CountMiss(AlarmsRead, response.TrimEnd(LineEndings));
                return;
            }
            ResolveRead(AlarmsRead);
            var enabled = alarms.Where(alarm => !alarm.Level.Equals(DisabledAlarmLevel, StringComparison.OrdinalIgnoreCase)).ToList();
            SetDetail(AlarmsLabel, payload, enabled.Count == 0 ? DetailTone.Normal : DetailTone.Warning);

            var activeKeys = enabled.Select(alarm => AlarmIssueKey(alarm.Name)).ToHashSet();
            foreach (var cleared in GetOngoingIssues().Where(issue => issue.Key.StartsWith(AlarmIssuePrefix) && !activeKeys.Contains(issue.Key)))
                ResolveIssue(cleared.Key);
            foreach (var alarm in enabled)
                RaiseOngoingIssue(AlarmIssueKey(alarm.Name), AlarmMessage(alarm), AlarmSeverities.GetValueOrDefault(alarm.Level, IssueSeverity.Minor));
        }
    }

    private static string AlarmIssueKey(string name) => $"{AlarmIssuePrefix}{name}";

    private static string AlarmMessage(ExtronAlarm alarm) => AlarmSeverities.ContainsKey(alarm.Level)
        ? $"The SMP reports the {alarm.Name} alarm at level {alarm.Level}"
        : $"The SMP reports the {alarm.Name} alarm at a level its manual doesn't list: {alarm.Level}";

    private void HandleErrorReply(string error)
    {
        var description = ErrorMessages.TryGetValue(error, out var message) ? $"{error} ({message})" : error;
        var sent = _sent.TakeError();
        using (Trigger.Resume(sent?.TriggerId))
        using (PushProperties())
        {
            switch (sent?.Command)
            {
                case SmpRead read:
                    RejectRead(read, error, description);
                    break;
                case { } command when RecorderCommands.Contains(command):
                    RaiseMomentaryIssue($"The SMP returned an error: {description} to {ExtronSis.Printable(command.Text)}", ErrorReplyIssueKey);
                    break;
                case { } command:
                    LogWarning("The SMP returned {Error} to {Command}", description, ExtronSis.Printable(command.Text));
                    break;
                default:
                    LogWarning("The SMP returned {Error} with no command awaiting a reply", description);
                    break;
            }
        }
    }

    private void RejectRead(SmpRead read, string error, string description)
    {
        if (!ExtronSis.MeansNotSupported(error))
        {
            CountMiss(read, description);
            return;
        }
        MarkUnsupported(read);
        LogInformation("The SMP does not support {Command}: {Error}", ExtronSis.Printable(read.Text), description);
        SetDetail(read.Label, $"Not supported: {error}", DetailTone.Normal);
        ResolveRead(read);
    }

    private void ProcessRecordingState(string state)
    {
        TransportState = state switch
        {
            "recording" => TransportState.Recording,
            "paused" => TransportState.RecordingPaused,
            "stopped" => TransportState.Stopped,
            "setup" => TransportState.PreparingToRecord,
            _ => TransportState.Unknown
        };
    }

    private async Task Poll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;
        ReportTheMissedReplies();
        Send(InformationQuery);
        _counter++;
        if (_counter % HealthReadPolls == 0)
            QueueTheHealthReads();
        if (_counter > UsbStatusPolls)
        {
            _counter = 0;
            await Task.Delay(UsbStatusQuerySpacing, _timeProvider);
            Send(FrontUsbQuery);
            await Task.Delay(UsbStatusQuerySpacing, _timeProvider);
            Send(RearUsbQuery);
            await Task.Delay(UsbStatusQuerySpacing, _timeProvider);
            Send(RcpUsbQuery);
        }
        else
        {
            SendTheNextRead();
        }
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    private record SmpCommand(string Text, string ReplyTag);

    private sealed record SmpRead(string Text, string ReplyTag, string IssueKey, string Label) : SmpCommand(Text, ReplyTag);

    private record ExtronAlarm(string Name, string Level);
}
