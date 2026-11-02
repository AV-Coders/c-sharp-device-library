using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Dsp;

public enum BiampQuery
{
    Unknown,
    Level,
    MinGain,
    MaxGain,
    Mute,
    Firmware,
    Serial,
    DeviceInfo,
    NetworkStatus,
    Aliases
}

public record Query(string ArrayIndex, BiampQuery BiampQuery, string DspCommand)
{
    public string? TriggerId { get; init; }
}

public record BiampAudioBlockInfo(string Name, string InstanceTag, int BlockIndex);

public class BiampGain(VolumeLevelHandler volumeLevelHandler, string controlName, int controlIndex, FaderCurve curve = FaderCurve.Linear)
    : Fader(volumeLevelHandler, curve)
{
    public readonly int ControlIndex = controlIndex;
    public readonly string ControlName = controlName;
}

public class BiampMute(MuteStateHandler muteStateHandler, string controlName, int controlIndex)
    : Mute(muteStateHandler)
{
    public readonly int ControlIndex = controlIndex;
    public readonly string ControlName = controlName;
}

public class BiampInt(StringValueHandler stringValueHandler) : StringValue(stringValueHandler);

public class BiampVolumeControl : VolumeControl
{
    private readonly string _instanceTag;
    private readonly int _index;
    private readonly BiampTtp _dsp;

    public BiampVolumeControl(BiampAudioBlockInfo audioBlockInfo, VolumeType type, BiampTtp dsp) : base(audioBlockInfo.Name, type)
    {
        _dsp = dsp;
        _instanceTag = audioBlockInfo.InstanceTag;
        _index = audioBlockInfo.BlockIndex;
        
        _dsp.AddControl(volumeLevel => Volume = volumeLevel, _instanceTag, _index);
        _dsp.AddControl(muteState => MuteState = muteState, _instanceTag, _index);
    }
    public override void LevelUp(int amount) => _dsp.LevelUp(_instanceTag, _index, amount);

    public override void LevelDown(int amount) => _dsp.LevelDown(_instanceTag, _index, amount);

    public override void SetLevel(int percentage) => _dsp.SetLevel(_instanceTag, _index, percentage);

    public override void ToggleAudioMute() => _dsp.ToggleAudioMute(_instanceTag, _index);
    
    public override void SetAudioMute(MuteState state) => _dsp.SetAudioMute(_instanceTag, _index, state);
}

public class BiampTtp : Dsp
{
    public static readonly ushort DefaultPort = 22;
    public static readonly string DefaultUser = "default";
    public static readonly string DefaultPassword = string.Empty;
    public const int KeepAliveReplyWindowMs = 1000;
    public const int SlowKeepAliveReplyMs = 500;
    public const int MaxMissedKeepAlives = 3;
    public const int MaxSlowKeepAlives = 3;
    private const int KeepAliveIntervalMs = 10000;
    private const int MaxMissedReplies = 3;
    private const string UnansweredQueryIssueKey = "unanswered-query";
    private const string SlowRepliesIssueKey = "slow-replies";
    private const string FirmwareHealthIssueKey = "firmware-health";
    private const string ErrorReplyIssueKey = "error-reply";
    private const string FirmwareIssueKey = "firmware-read";
    private const string SerialIssueKey = "serial-read";
    private const string DeviceInfoIssueKey = "device-info-read";
    private const string NetworkStatusIssueKey = "network-status-read";
    private const string AliasesIssueKey = "aliases-read";
    private const string FaultsIssueKey = "fault-list-read";
    private const string FaultIssuePrefix = "fault:";
    private const string BadControlIssuePrefix = "bad-control:";
    private const string FirmwareLabel = "Firmware";
    private const string SerialLabel = "Serial";
    private const string ModelLabel = "Model";
    private const string RevisionLabel = "Revision";
    private const string HostnameLabel = "Hostname";
    private const string DnsLabel = "DNS";
    private const string MissingControlsLabel = "Missing Controls";
    private const string ReplyTimeLabel = "Reply Time";
    private const string ActiveFaultsLabel = "Active Faults";
    private const string AttributeNotSupportedError = "ATTRIBUTE_NOT_SUPPORTED";
    private const string AlreadySubscribedError = "ALREADY_SUBSCRIBED";
    private const string NotSupportedByError = "is not supported by";
    private const string FirmwareCommand = "DEVICE get version";
    private const string SerialCommand = "DEVICE get serialNumber";
    private const string DeviceInfoCommand = "DEVICE get deviceInfo";
    private const string NetworkStatusCommand = "DEVICE get networkStatus";
    private const string AliasesCommand = "SESSION get aliases";
    private const string FaultsCommand = "DEVICE get activeFaultList";
    private const string KeepAliveRequest = $"{FaultsCommand}\n";
    private const string MajorInDeviceIndicator = "INDICATOR_MAJOR_IN_DEVICE";
    private const string MajorInSystemIndicator = "INDICATOR_MAJOR_IN_SYSTEM";
    private const string ErrorReplyPrefix = "-";
    private const string PlainReplyPrefix = "+OK";
    private const string ReplyValueGroup = "value";
    private const char TtpStringQuote = '"';
    private const string EmptyTtpString = "\"\"";
    private static readonly Query FirmwareQuery = new("DEVICE", BiampQuery.Firmware, $"{FirmwareCommand}\n");
    private static readonly Query SerialQuery = new("DEVICE", BiampQuery.Serial, $"{SerialCommand}\n");
    private static readonly Query DeviceInfoQuery = new("DEVICE", BiampQuery.DeviceInfo, $"{DeviceInfoCommand}\n");
    private static readonly Query NetworkStatusQuery = new("DEVICE", BiampQuery.NetworkStatus, $"{NetworkStatusCommand}\n");
    private static readonly Query AliasesQuery = new("SESSION", BiampQuery.Aliases, $"{AliasesCommand}\n");
    private static readonly Regex QueryReplyParser = new($"^\\+OK \"\\w+\":(?<{ReplyValueGroup}>.*)$", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly TimeSpan InterruptedReplyTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DisconnectedPollWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnansweredQueryWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QueryGap = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SubscriptionGap = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReinitialiseSettleTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan KeepAliveReplyWindow = TimeSpan.FromMilliseconds(KeepAliveReplyWindowMs);
    private static readonly TimeSpan KeepAliveRest = TimeSpan.FromMilliseconds(KeepAliveIntervalMs - KeepAliveReplyWindowMs);
    private static readonly Version MinimumV4Firmware = new(4, 11);
    private static readonly Version MinimumV5Firmware = new(5, 7);
    private readonly Dictionary<string, BiampGain> _gains = new();
    private readonly Dictionary<string, BiampMute> _mutes = new();
    private readonly object _controlsLock = new();
    
    private readonly Dictionary<MuteState, string> _muteStateDictionary;
    private readonly Regex _subscriptionResponseParser;

    private readonly List<Query> _moduleQueries = [];
    private readonly object _moduleQueriesLock = new ();
    private readonly ConcurrentQueue<Query> _pendingQueries = new();
    private Query? _currentQuery = null;
    private Query? _interruptedQuery;
    private long _interruptedAt;
    private readonly object _interruptedQueryLock = new();
    private readonly List<string> _deviceSubscriptions = [];
    private readonly object _deviceSubscriptionsLock = new ();
    private int _loopsSinceLastInitialise = 0;
    private readonly Queue<KeepAlive> _keepAlives = new();
    private readonly List<long> _slowReplyTimes = [];
    private readonly object _keepAlivesLock = new();
    private int _missedKeepAlives;
    private readonly Dictionary<string, int> _missedReplies = new();
    private readonly object _missedRepliesLock = new();
    private bool _clientHasReconnectedSinceLastPollLoop;
    private int _initialising;
    private readonly Queue<OutstandingCommand> _outstandingCommands = new();
    private readonly object _outstandingCommandsLock = new();
    private readonly FaderCurve _faderCurve;

    public BiampTtp(CommunicationClient commsClient, string name = "Biamp", int pollIntervalInMilliseconds = 200, FaderCurve faderCurve = FaderCurve.Linear)
        : this(commsClient, name, pollIntervalInMilliseconds, faderCurve, null)
    {
    }

    internal BiampTtp(CommunicationClient commsClient, string name, int pollIntervalInMilliseconds, FaderCurve faderCurve, TimeProvider? timeProvider)
        : base(name, commsClient, pollIntervalInMilliseconds, timeProvider)
    {
        _faderCurve = faderCurve;
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        
        _muteStateDictionary = new Dictionary<MuteState, string>
        {
            { MuteState.On, "true" },
            { MuteState.Off, "false" }
        };
        
        string subscriptionResponsePattern = "\":\"(.+)\" \"value\":(.+)";
        _subscriptionResponseParser = new Regex(subscriptionResponsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));

        HandleConnectionState(commsClient.ConnectionState);
    }
    
    private void HandleConnectionState(ConnectionState connectionState)
    {
        using (PushProperties("HandleConnectionState"))
        {
            AddEvent(EventType.Connection, connectionState.ToString());
            
            if (connectionState == ConnectionState.Connected)
            {
                lock (_outstandingCommandsLock)
                    _outstandingCommands.Clear();
                lock (_missedRepliesLock)
                    _missedReplies.Clear();
                ForgetTheKeepAlives();
                _clientHasReconnectedSinceLastPollLoop = true;
                PollWorker.Restart();
            }
            else
                CommunicationState = CommunicationClient.DesiredConnectionState == ConnectionState.Disconnected
                    ? CommunicationState.NotAttempted
                    : CommunicationState.Unknown;
        }
    }

    private void SendNonPollCommand(string command)
    {
        var interrupted = _currentQuery;
        if (interrupted != null)
        {
            lock (_interruptedQueryLock)
            {
                _interruptedQuery = interrupted;
                _interruptedAt = TimeProvider.GetTimestamp();
            }
            _pendingQueries.Enqueue(interrupted);
        }
        _currentQuery = null;
        lock (_outstandingCommandsLock)
            _outstandingCommands.Enqueue(new OutstandingCommand(command, Trigger.Id));
        CommunicationClient.Send(command);
    }

    private void SendSubscription(string subscription)
    {
        lock (_outstandingCommandsLock)
            _outstandingCommands.Enqueue(new OutstandingCommand(subscription, Trigger.Id));
        CommunicationClient.Send(subscription);
    }

    private OutstandingCommand? TakeOutstandingCommand()
    {
        lock (_outstandingCommandsLock)
            return _outstandingCommands.TryDequeue(out var command) ? command : null;
    }

    private Query? TakeInterruptedQuery()
    {
        lock (_interruptedQueryLock)
        {
            var interrupted = _interruptedQuery;
            _interruptedQuery = null;
            return interrupted;
        }
    }

    private bool WaitingForTheInterruptedReply()
    {
        Query? interrupted;
        lock (_interruptedQueryLock)
        {
            interrupted = _interruptedQuery;
            if (interrupted == null)
                return false;
            if (TimeProvider.GetElapsedTime(_interruptedAt) < InterruptedReplyTimeout)
                return true;
            _interruptedQuery = null;
        }
        using (Trigger.Resume(interrupted.TriggerId))
        using (PushProperties("Poll"))
            LogWarning("The interrupted query {query} was not answered, polling the next query", interrupted.DspCommand);
        return false;
    }

    protected override async Task Poll(CancellationToken token)
    {
        using (PushProperties("Poll"))
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            {
                AddEvent(EventType.Other, "IP Comms disconnected, not polling");
                await Task.Delay(DisconnectedPollWait, TimeProvider, token);
                return;
            }

            if (_clientHasReconnectedSinceLastPollLoop)
            {
                LogVerbose("Connection state changed, getting the version");
                _clientHasReconnectedSinceLastPollLoop = false;
                await Reinitialise(token);
                return;
            }

            var current = _currentQuery;
            if (current != null)
            {
                using (Trigger.Resume(current.TriggerId))
                using (PushProperties("Poll"))
                {
                    RaiseMomentaryIssue($"The query {current.DspCommand.Trim()} was not answered",
                        key: UnansweredQueryIssueKey, escalateAfter: 3);
                    LogWarning("The query {query} was not answered, momentarily slowing down", current.DspCommand);
                }
                _pendingQueries.Enqueue(current);
                await Task.Delay(UnansweredQueryWait, TimeProvider, token);
                _currentQuery = null;
                return;
            }

            if (WaitingForTheInterruptedReply())
                return;

            if (!KeepAliveIsOwed() && _pendingQueries.TryDequeue(out var query))
            {
                using (Trigger.Resume(query.TriggerId))
                using (PushProperties("Poll"))
                {
                    LogVerbose("Polling for {query}", query.DspCommand);
                    _currentQuery = query;
                    CommunicationClient.Send(query.DspCommand);
                }
                await Task.Delay(QueryGap, TimeProvider, token);
            }
            else
            {
                LogVerbose("Sending the keep-alive");
                await SendTheKeepAlive(token);
                _loopsSinceLastInitialise++;
                if (_loopsSinceLastInitialise > 18)
                {
                    await Reinitialise(token);
                }
            }
        }
    }
    
    public override async Task Reinitialise(CancellationToken token = default)
    {
        using (PushProperties("Reinitialise"))
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return;
            if (Interlocked.CompareExchange(ref _initialising, 1, 0) != 0)
                return;
            try
            {
                AddEvent(EventType.Other, "Reinitialising");

                List<string> subscriptions;
                lock (_deviceSubscriptionsLock)
                    subscriptions = _deviceSubscriptions.ToList();

                foreach (var subscriptionCommand in subscriptions)
                {
                    SendSubscription(subscriptionCommand);
                    await Task.Delay(SubscriptionGap, TimeProvider, token);
                }

                await Task.Delay(ReinitialiseSettleTime, TimeProvider, token);

                lock (_moduleQueriesLock)
                {
                    while (_pendingQueries.TryDequeue(out _)) { }
                    _currentQuery = null;
                    lock (_interruptedQueryLock)
                        _interruptedQuery = null;
                    foreach (var query in _moduleQueries)
                    {
                        _pendingQueries.Enqueue(query with { TriggerId = Trigger.Id });
                    }
                    _pendingQueries.Enqueue(FirmwareQuery with { TriggerId = Trigger.Id });
                    _pendingQueries.Enqueue(SerialQuery with { TriggerId = Trigger.Id });
                    _pendingQueries.Enqueue(DeviceInfoQuery with { TriggerId = Trigger.Id });
                    _pendingQueries.Enqueue(NetworkStatusQuery with { TriggerId = Trigger.Id });
                    _pendingQueries.Enqueue(AliasesQuery with { TriggerId = Trigger.Id });
                }

                _loopsSinceLastInitialise = 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                LogException(e);
                AddEvent(EventType.Error, "Initialisation error");
            }
            finally
            {
                Interlocked.Exchange(ref _initialising, 0);
            }
        }
    }

    private async Task SendTheKeepAlive(CancellationToken token)
    {
        var keepAlive = new KeepAlive(TimeProvider.GetTimestamp(), Trigger.Id);
        lock (_keepAlivesLock)
            _keepAlives.Enqueue(keepAlive);
        CommunicationClient.Send(KeepAliveRequest);

        await Task.Delay(KeepAliveReplyWindow, TimeProvider, token);
        if (TryMarkMissed(keepAlive))
            HandleMissedKeepAlive(keepAlive);
        else if (TakeTooManySlowReplyTimes() is { } slowReplyTimes)
            ReconnectAfterSlowKeepAlives(slowReplyTimes);

        await Task.Delay(KeepAliveRest, TimeProvider, token);
    }

    private bool KeepAliveIsOwed()
    {
        lock (_keepAlivesLock)
            return _keepAlives.Count > 0;
    }

    private bool TryMarkMissed(KeepAlive keepAlive)
    {
        lock (_keepAlivesLock)
        {
            if (!_keepAlives.Contains(keepAlive))
                return false;
            keepAlive.Missed = true;
            return true;
        }
    }

    private KeepAlive? TakeKeepAlive()
    {
        lock (_keepAlivesLock)
            return _keepAlives.TryDequeue(out var keepAlive) ? keepAlive : null;
    }

    private List<long>? TakeTooManySlowReplyTimes()
    {
        lock (_keepAlivesLock)
        {
            if (_slowReplyTimes.Count < MaxSlowKeepAlives)
                return null;
            var slowReplyTimes = _slowReplyTimes.ToList();
            _slowReplyTimes.Clear();
            return slowReplyTimes;
        }
    }

    private void HandleMissedKeepAlive(KeepAlive keepAlive)
    {
        int missed;
        lock (_keepAlivesLock)
            missed = ++_missedKeepAlives;
        using (Trigger.Resume(keepAlive.TriggerId))
        using (PushProperties("HandleMissedKeepAlive"))
        {
            RaiseMomentaryIssue($"The query {FaultsCommand} was not answered within {KeepAliveReplyWindowMs} ms",
                key: UnansweredQueryIssueKey, escalateAfter: MaxMissedKeepAlives);
            if (missed < MaxMissedKeepAlives)
                return;
            LogWarning("The Tesira has not answered {Count} keep-alives in a row within {WindowMs} ms, reconnecting",
                MaxMissedKeepAlives, KeepAliveReplyWindowMs);
            AddEvent(EventType.Connection, $"Reconnecting: {MaxMissedKeepAlives} keep-alives in a row were not answered within {KeepAliveReplyWindowMs} ms");
            ReconnectTheTesira();
        }
    }

    private void ReconnectAfterSlowKeepAlives(List<long> slowReplyTimes)
    {
        string replyTimes = string.Join(", ", slowReplyTimes.Select(replyMs => $"{replyMs} ms"));
        using (PushProperties("ReconnectAfterSlowKeepAlives"))
        {
            LogWarning("The Tesira answered {Count} keep-alives in a row slower than {SlowMs} ms ({ReplyTimes}), reconnecting",
                MaxSlowKeepAlives, SlowKeepAliveReplyMs, replyTimes);
            AddEvent(EventType.Connection, $"Reconnecting: {MaxSlowKeepAlives} keep-alive replies in a row were slower than {SlowKeepAliveReplyMs} ms ({replyTimes})");
            ReconnectTheTesira();
        }
    }

    private void ReconnectTheTesira()
    {
        ForgetTheKeepAlives();
        if (CommunicationClient is not IpComms ipComms)
        {
            LogWarning("A {ClientType} cannot reconnect", CommunicationClient.GetType().Name);
            return;
        }
        ipComms.Reconnect();
    }

    private void ForgetTheKeepAlives()
    {
        lock (_keepAlivesLock)
        {
            _keepAlives.Clear();
            _slowReplyTimes.Clear();
            _missedKeepAlives = 0;
        }
    }

    private void HandleKeepAliveReply(KeepAlive keepAlive, string value, string line)
    {
        using (Trigger.Resume(keepAlive.TriggerId))
        using (PushProperties("HandleKeepAliveReply"))
        {
            TimeTheKeepAlive(keepAlive);
            ShowFaults(value, line);
        }
    }

    private void HandleKeepAliveError(KeepAlive keepAlive, string line)
    {
        using (Trigger.Resume(keepAlive.TriggerId))
        using (PushProperties("HandleKeepAliveError"))
        {
            TimeTheKeepAlive(keepAlive);
            HandleReadError(FaultsIssueKey, FaultsCommand, line, ActiveFaultsLabel);
        }
    }

    private void TimeTheKeepAlive(KeepAlive keepAlive)
    {
        long replyMs = (long)TimeProvider.GetElapsedTime(keepAlive.SentAt).TotalMilliseconds;
        CommunicationState = CommunicationState.Okay;
        SetDetail(ReplyTimeLabel, $"{replyMs} ms", replyMs > SlowKeepAliveReplyMs ? DetailTone.Warning : DetailTone.Normal);
        if (keepAlive.Missed)
            LogWarning("The {Command} reply took {ReplyMs} ms, so it was already counted as missed", FaultsCommand, replyMs);
        else if (replyMs > SlowKeepAliveReplyMs)
            HandleSlowKeepAlive(replyMs);
        else
            HandleFastKeepAlive();
    }

    private void HandleFastKeepAlive()
    {
        lock (_keepAlivesLock)
        {
            _slowReplyTimes.Clear();
            _missedKeepAlives = 0;
        }
        ResolveIssue(UnansweredQueryIssueKey);
        ResolveIssue(SlowRepliesIssueKey);
    }

    private void HandleSlowKeepAlive(long replyMs)
    {
        lock (_keepAlivesLock)
        {
            _slowReplyTimes.Add(replyMs);
            _missedKeepAlives = 0;
        }
        ResolveIssue(UnansweredQueryIssueKey);
        string firmware = Details.SingleOrDefault(detail => detail.Label == FirmwareLabel)?.Value ?? "not read yet";
        RaiseOngoingIssue(SlowRepliesIssueKey,
            $"The Tesira took {replyMs} ms to answer {FaultsCommand}, over the {SlowKeepAliveReplyMs} ms limit. Firmware: {firmware}");
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (string received in response.Split('\n'))
            {
                string line = received.TrimEnd('\r');
                var queryReply = QueryReplyParser.Match(line);
                if (queryReply.Success)
                {
                    if (TakeKeepAlive() is { } keepAlive)
                        HandleKeepAliveReply(keepAlive, queryReply.Groups[ReplyValueGroup].Value, line);
                    else
                        AllocateValueToBlock(queryReply.Groups[ReplyValueGroup].Value, line);
                }
                else if (line.StartsWith("!"))
                {
                    ProcessChangeNotification(line);
                }
                else if (line.StartsWith(PlainReplyPrefix))
                {
                    if (TakeOutstandingCommand() is { } command)
                        HandleCommandReply(command, line);
                }
                else if (line.StartsWith(ErrorReplyPrefix))
                {
                    HandleErrorReply(line);
                }
                else if (line.StartsWith("Welcome to the Tesira Text Protocol Server"))
                {
                    _clientHasReconnectedSinceLastPollLoop = true;
                    PollWorker.Restart();
                }
            }
        }
    }

    private void ProcessChangeNotification(string line)
    {
        if (!line.Contains("publishToken"))
            return;
        var match = _subscriptionResponseParser.Match(line);
        var gain = FindGain(match.Groups[1].Value);
        if (gain != null)
        {
            gain.SetVolumeFromDb(double.Parse(match.Groups[2].Value));
            CommunicationState = CommunicationState.Okay;
        }

        var mute = FindMute(match.Groups[1].Value);
        if (mute != null)
        {
            mute.MuteState = match.Groups[2].Value.Contains("true") ? MuteState.On : MuteState.Off;
            CommunicationState = CommunicationState.Okay;
        }
    }

    private void HandleCommandReply(OutstandingCommand command, string line)
    {
        using (Trigger.Resume(command.TriggerId))
        using (PushProperties("HandleCommandReply"))
            LogVerbose("The Tesira answered {Command} with {Reply}", command.Command.Trim(), line);
    }

    private void HandleCommandError(OutstandingCommand command, string line)
    {
        using (Trigger.Resume(command.TriggerId))
        using (PushProperties("HandleCommandError"))
        {
            if (line.Contains(AlreadySubscribedError))
                LogVerbose("The subscription {Command} is still in place: {Reply}", command.Command.Trim(), line);
            else
                RaiseMomentaryIssue($"The Tesira returned {line} to {command.Command.Trim()}", ErrorReplyIssueKey);
        }
    }

    private void HandleErrorReply(string line)
    {
        if (TakeOutstandingCommand() is { } command)
        {
            HandleCommandError(command, line);
            return;
        }
        if (TakeKeepAlive() is { } keepAlive)
        {
            HandleKeepAliveError(keepAlive, line);
            return;
        }
        var current = _currentQuery ?? TakeInterruptedQuery();
        _currentQuery = null;
        using (Trigger.Resume(current?.TriggerId))
        using (PushProperties("HandleErrorReply"))
        {
            ResolveIssue(UnansweredQueryIssueKey);
            switch (current?.BiampQuery)
            {
                case BiampQuery.Firmware:
                    HandleReadError(FirmwareIssueKey, FirmwareCommand, line, FirmwareLabel);
                    break;
                case BiampQuery.Serial:
                    HandleReadError(SerialIssueKey, SerialCommand, line, SerialLabel);
                    break;
                case BiampQuery.DeviceInfo:
                    HandleReadError(DeviceInfoIssueKey, DeviceInfoCommand, line, ModelLabel, RevisionLabel);
                    break;
                case BiampQuery.NetworkStatus:
                    HandleReadError(NetworkStatusIssueKey, NetworkStatusCommand, line, HostnameLabel, DnsLabel);
                    break;
                case BiampQuery.Aliases:
                    HandleReadError(AliasesIssueKey, AliasesCommand, line, MissingControlsLabel);
                    break;
                default:
                    RaiseMomentaryIssue(current == null
                            ? $"The Tesira returned an error: {line}"
                            : $"The Tesira returned an error to {current.DspCommand.Trim()}: {line}",
                        ErrorReplyIssueKey);
                    break;
            }
        }
    }

    private void HandleReadError(string issueKey, string command, string line, params string[] labels)
    {
        if (!line.Contains(AttributeNotSupportedError) && !line.Contains(NotSupportedByError))
        {
            CountMissedReply(issueKey, command, line);
            return;
        }
        string notSupported = $"Not supported: {line}";
        if (labels.Any(label => !Details.Contains(new DeviceDetail(label, notSupported))))
            LogInformation("The Tesira does not support {Command}: {Reply}", command, line);
        foreach (var label in labels)
            SetDetail(label, notSupported, DetailTone.Normal);
        ReadAnswered(issueKey);
    }

    private void CountGarbledReply(string issueKey, string command, string line, params string[] labels)
    {
        foreach (var label in labels)
            SetDetail(label, line, DetailTone.Warning);
        CountMissedReply(issueKey, command, line);
    }

    private void CountMissedReply(string issueKey, string command, string line)
    {
        int missed;
        lock (_missedRepliesLock)
        {
            missed = _missedReplies.GetValueOrDefault(issueKey) + 1;
            _missedReplies[issueKey] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("The Tesira gave {Reply} to {Command}, asking again at the next read ({Missed} of {MaxMissedReplies})",
                line, command, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(issueKey,
            $"The Tesira did not give a real reply to {command} {MaxMissedReplies} times in a row, the last was {line}",
            IssueSeverity.Minor);
    }

    private void ShowEmptyValue(string issueKey, string line, params string[] labels)
    {
        foreach (var label in labels)
            SetDetail(label, line, DetailTone.Normal);
        ReadAnswered(issueKey);
    }

    private void ReadAnswered(string issueKey)
    {
        lock (_missedRepliesLock)
            _missedReplies.Remove(issueKey);
        ResolveIssue(issueKey);
    }

    private void AllocateValueToBlock(string value, string line)
    {
        var current = _currentQuery;
        if (current == null)
        {
            DropValueWithNoCurrentQuery(value);
            return;
        }

        using (Trigger.Resume(current.TriggerId))
        using (PushProperties("AllocateValueToBlock"))
        {
            var currentPolledBlock = current.ArrayIndex;

            switch (current.BiampQuery)
            {
                case BiampQuery.Mute:
                    var theMute = FindMute(currentPolledBlock);
                    if (theMute != null)
                    {
                        theMute.MuteState = value == "true" ? MuteState.On : MuteState.Off;
                        CommunicationState = CommunicationState.Okay;
                    }
                    else
                        LogError("Error getting the Mute state, The polled block should be a mute, but it isn't. {blockKey}", current.DspCommand);
                    break;
                case BiampQuery.Level:
                    var level = FindGain(currentPolledBlock);
                    if (level != null)
                        level.SetVolumeFromDb(double.Parse(value));
                    else
                        LogError("Error getting the current level, the polled block should be a level, but it isn't. {blockKey}", current.DspCommand);
                    break;
                case BiampQuery.MaxGain:
                    var max = FindGain(currentPolledBlock);
                    if (max != null)
                        max.SetMaxGain(double.Parse(value));
                    else
                        LogError("Error getting the Max gain, The polled block should be a level, but it isn't. {blockKey}", current.DspCommand);
                    break;
                case BiampQuery.MinGain:
                    var min = FindGain(currentPolledBlock);
                    if (min != null)
                        min.SetMinGain(double.Parse(value));
                    else
                        LogError("Error getting the Min gain, The polled block should be a level, but it isn't. {blockKey}", current.DspCommand);
                    break;
                case BiampQuery.Firmware:
                    ShowFirmware(value, line);
                    break;
                case BiampQuery.Serial:
                    ShowSerial(value, line);
                    break;
                case BiampQuery.DeviceInfo:
                    ShowDeviceInfo(value, line);
                    break;
                case BiampQuery.NetworkStatus:
                    ShowNetworkStatus(value, line);
                    break;
                case BiampQuery.Aliases:
                    ShowMissingInstanceTags(value, line);
                    break;
            }

            ResolveIssue(UnansweredQueryIssueKey);
            _currentQuery = null;
        }
    }

    private void ShowFirmware(string value, string line)
    {
        if (value == EmptyTtpString)
        {
            ShowEmptyValue(FirmwareIssueKey, line, FirmwareLabel);
            return;
        }
        if (!IsNonEmptyTtpString(value))
        {
            CountGarbledReply(FirmwareIssueKey, FirmwareCommand, line, FirmwareLabel);
            return;
        }
        SetDetail(FirmwareLabel, value[1..^1]);
        ReadAnswered(FirmwareIssueKey);
        CheckTheFirmwareIsHealthy(value[1..^1]);
    }

    private void CheckTheFirmwareIsHealthy(string firmware)
    {
        if (!Version.TryParse(firmware, out var version))
            RaiseOngoingIssue(FirmwareHealthIssueKey, $"Firmware {firmware} is not a version number this driver can check");
        else if (version < MinimumV4Firmware)
            RaiseOngoingIssue(FirmwareHealthIssueKey, $"Firmware {firmware} is below {MinimumV4Firmware}, so it is not supported");
        else if (version.Major == MinimumV5Firmware.Major && version < MinimumV5Firmware)
            RaiseOngoingIssue(FirmwareHealthIssueKey,
                $"Firmware {firmware} is below {MinimumV5Firmware}, the minimum for version {MinimumV5Firmware.Major}", IssueSeverity.Minor);
        else
            ResolveIssue(FirmwareHealthIssueKey);
    }

    private void ShowSerial(string value, string line)
    {
        if (value == EmptyTtpString)
        {
            ShowEmptyValue(SerialIssueKey, line, SerialLabel);
            return;
        }
        if (!IsNonEmptyTtpString(value))
        {
            CountGarbledReply(SerialIssueKey, SerialCommand, line, SerialLabel);
            return;
        }
        SetDetail(SerialLabel, value[1..^1]);
        ReadAnswered(SerialIssueKey);
    }

    private static bool IsNonEmptyTtpString(string value) =>
        value.Length > EmptyTtpString.Length && value.StartsWith(TtpStringQuote) && value.EndsWith(TtpStringQuote);

    private void ShowDeviceInfo(string value, string line)
    {
        var info = ParseTtp(value);
        string? model = StringProperty(info, "deviceModel");
        string? revision = StringProperty(info, "deviceRevision");
        if (model == null || revision == null)
        {
            CountGarbledReply(DeviceInfoIssueKey, DeviceInfoCommand, line, ModelLabel, RevisionLabel);
            return;
        }
        SetDetail(ModelLabel, model);
        SetDetail(RevisionLabel, revision);
        ReadAnswered(DeviceInfoIssueKey);
    }

    private void ShowNetworkStatus(string value, string line)
    {
        var status = ParseTtp(value);
        string? hostname = StringProperty(status, "hostname");
        var interfaces = Property(status, "networkInterfaceStatusWithName", JsonValueKind.Array);
        var dns = Property(status, "dnsStatus", JsonValueKind.Object);
        if (hostname == null || interfaces == null || dns == null)
        {
            CountGarbledReply(NetworkStatusIssueKey, NetworkStatusCommand, line, HostnameLabel, DnsLabel);
            return;
        }
        SetDetail(HostnameLabel, hostname);
        foreach (var networkInterface in interfaces.Value.EnumerateArray())
            SetDetail(StringProperty(networkInterface, "interfaceId") ?? networkInterface.GetRawText(), InterfaceText(networkInterface));
        SetDetail(DnsLabel, DnsText(dns.Value));
        ReadAnswered(NetworkStatusIssueKey);
    }

    private static string InterfaceText(JsonElement networkInterface)
    {
        var status = Property(networkInterface, "networkInterfaceStatus", JsonValueKind.Object);
        string? link = StringProperty(status, "linkStatus");
        string? ip = StringProperty(status, "ip");
        string? netmask = StringProperty(status, "netmask");
        string? gateway = StringProperty(status, "gateway");
        string? source = StringProperty(status, "addressSource");
        string? mac = StringProperty(status, "macAddress");
        return link == null || ip == null || netmask == null || gateway == null || source == null || mac == null
            ? networkInterface.GetRawText()
            : $"{link}, {ip}/{netmask}, gateway {gateway}, {source}, MAC {mac}";
    }

    private static string DnsText(JsonElement dns)
    {
        string? primary = StringProperty(dns, "primaryDNSServer");
        string? secondary = StringProperty(dns, "secondaryDNSServer");
        string? domain = StringProperty(dns, "domainName");
        return primary == null || secondary == null || domain == null
            ? dns.GetRawText()
            : $"primary {primary}, secondary {secondary}, domain {domain}";
    }

    private void ShowMissingInstanceTags(string value, string line)
    {
        var aliases = ParseTtp(value);
        if (aliases is not { ValueKind: JsonValueKind.Array } aliasList
            || aliasList.EnumerateArray().Any(alias => alias.ValueKind != JsonValueKind.String))
        {
            CountGarbledReply(AliasesIssueKey, AliasesCommand, line, MissingControlsLabel);
            return;
        }
        ReadAnswered(AliasesIssueKey);

        var configured = aliasList.EnumerateArray().Select(alias => alias.GetString()!).ToHashSet();
        var registered = RegisteredInstanceTags();
        var missing = registered.Where(tag => !configured.Contains(tag)).ToList();
        foreach (var tag in registered.Except(missing))
            ResolveIssue($"{BadControlIssuePrefix}{tag}");
        foreach (var tag in missing)
            RaiseOngoingIssue($"{BadControlIssuePrefix}{tag}", $"The Tesira's configuration has no instance tag {tag}");

        if (missing.Count == 0)
            RemoveDetail(MissingControlsLabel);
        else
            SetDetail(MissingControlsLabel, string.Join(", ", missing), DetailTone.Warning);
    }

    private List<string> RegisteredInstanceTags()
    {
        lock (_controlsLock)
            return _gains.Values.Select(gain => gain.ControlName)
                .Concat(_mutes.Values.Select(mute => mute.ControlName))
                .Distinct()
                .Order()
                .ToList();
    }

    private void ShowFaults(string value, string line)
    {
        var entries = ParseTtp(value);
        if (entries is not { ValueKind: JsonValueKind.Array } faultList)
        {
            CountGarbledReply(FaultsIssueKey, FaultsCommand, line, ActiveFaultsLabel);
            return;
        }
        ReadAnswered(FaultsIssueKey);
        RemoveDetail(ActiveFaultsLabel);

        var faults = faultList.EnumerateArray()
            .SelectMany(ReadFaults)
            .GroupBy(fault => fault.IssueKey)
            .Select(same => new TesiraFault(same.Key, string.Join("; ", same.Select(fault => fault.Message)), same.Max(fault => fault.Severity)))
            .ToList();
        var activeKeys = faults.Select(fault => fault.IssueKey).ToHashSet();
        foreach (var cleared in GetOngoingIssues().Where(issue => issue.Key.StartsWith(FaultIssuePrefix) && !activeKeys.Contains(issue.Key)))
            ResolveIssue(cleared.Key);
        foreach (var fault in faults)
            RaiseOngoingIssue(fault.IssueKey, fault.Message, fault.Severity);
    }

    private static IEnumerable<TesiraFault> ReadFaults(JsonElement entry)
    {
        string? serial = StringProperty(entry, "serialNumber");
        string? indicator = StringProperty(entry, "id");
        string? indicatorName = StringProperty(entry, "name");
        var faults = Property(entry, "faults", JsonValueKind.Array);
        if (serial == null || indicator == null || indicatorName == null || faults == null)
            return [new TesiraFault($"{FaultIssuePrefix}{entry.GetRawText()}",
                $"The Tesira reported a fault entry this driver does not recognise: {entry.GetRawText()}", IssueSeverity.Minor)];

        var severity = IndicatorSeverity(indicator);
        return faults.Value.EnumerateArray().Select(fault => ReadFault(fault, serial, $"{indicatorName} ({indicator})", severity)).ToList();
    }

    private static TesiraFault ReadFault(JsonElement fault, string serial, string indicator, IssueSeverity severity)
    {
        string? id = StringProperty(fault, "id");
        string? name = StringProperty(fault, "name");
        return id == null || name == null
            ? new TesiraFault($"{FaultIssuePrefix}{serial}:{fault.GetRawText()}",
                $"{serial}, {indicator}, reported a fault this driver does not recognise: {fault.GetRawText()}", severity)
            : new TesiraFault($"{FaultIssuePrefix}{serial}:{id}", $"{name} ({id}) on {serial}, {indicator}", severity);
    }

    private static IssueSeverity IndicatorSeverity(string indicator) => indicator switch
    {
        MajorInDeviceIndicator or MajorInSystemIndicator => IssueSeverity.Major,
        _ => IssueSeverity.Minor
    };

    private static JsonElement? ParseTtp(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(TtpJson.ToJson(value));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement? element, string name, JsonValueKind kind) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == kind
            ? property
            : null;

    private static string? StringProperty(JsonElement? element, string name) =>
        Property(element, name, JsonValueKind.String)?.GetString();

    private record TesiraFault(string IssueKey, string Message, IssueSeverity Severity);

    private record KeepAlive(long SentAt, string? TriggerId)
    {
        public bool Missed { get; set; }
    }

    private record OutstandingCommand(string Command, string? TriggerId);

    private void DropValueWithNoCurrentQuery(string value)
    {
        var interrupted = TakeInterruptedQuery();
        using (Trigger.Resume(interrupted?.TriggerId))
        using (PushProperties("DropValueWithNoCurrentQuery"))
        {
            if (interrupted == null)
                LogVerbose("No current query, ignoring response");
            else
                LogVerbose("Dropping the reply {value} to the interrupted query {query}, it is asked again", value, interrupted.DspCommand);
        }
    }

    private BiampGain? FindGain(string arrayIndex)
    {
        lock (_controlsLock)
            return _gains.GetValueOrDefault(arrayIndex);
    }

    private BiampMute? FindMute(string arrayIndex)
    {
        lock (_controlsLock)
            return _mutes.GetValueOrDefault(arrayIndex);
    }

    private static string LevelKey(string controlName, int controlIndex) => $"AvCodersLevel-{controlName}-{controlIndex}";

    private static string MuteKey(string controlName, int controlIndex) => $"AvCodersMute-{controlName}-{controlIndex}";

    public void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName, int controlIndex)
    {
        using (PushProperties("AddControl"))
        {
            string arrayIndex = LevelKey(controlName, controlIndex);
            BiampGain? gain;
            lock (_controlsLock)
            {
                if (_gains.TryGetValue(arrayIndex, out gain))
                    gain.VolumeLevelHandlers += volumeLevelHandler;
                else
                    _gains.Add(arrayIndex, new BiampGain(volumeLevelHandler, controlName, controlIndex, _faderCurve));
            }

            if (gain != null)
            {
                volumeLevelHandler.Invoke(gain.Volume);
                LogVerbose("Adding a handler to the existing gain {gainName}", gain.ControlName);
            }
            else
            {
                lock (_moduleQueriesLock)
                {
                    _moduleQueries.Add(new Query(arrayIndex, BiampQuery.MaxGain, $"{controlName} get maxLevel {controlIndex}\n"));
                    _moduleQueries.Add(new Query(arrayIndex, BiampQuery.MinGain, $"{controlName} get minLevel {controlIndex}\n"));
                    _moduleQueries.Add(new Query(arrayIndex, BiampQuery.Level, $"{controlName} get level {controlIndex}\n"));
                }

                _pendingQueries.Enqueue(new Query(arrayIndex, BiampQuery.MaxGain, $"{controlName} get maxLevel {controlIndex}\n") { TriggerId = Trigger.Id });
                _pendingQueries.Enqueue(new Query(arrayIndex, BiampQuery.MinGain, $"{controlName} get minLevel {controlIndex}\n") { TriggerId = Trigger.Id });
                _pendingQueries.Enqueue(new Query(arrayIndex, BiampQuery.Level, $"{controlName} get level {controlIndex}\n") { TriggerId = Trigger.Id });

                var levelSubscription = $"{controlName} subscribe level {controlIndex} {arrayIndex}\n";
                lock (_deviceSubscriptionsLock)
                    _deviceSubscriptions.Add(levelSubscription);
                if (CommunicationClient.ConnectionState == ConnectionState.Connected)
                    SendSubscription(levelSubscription);
                LogVerbose("Created a new gain {gainName}", controlName);
            }
        }
    }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName)
    {
        using (PushProperties("AddControl"))
            LogVerbose("The array index was not specified for {controlName} level/gain, using the default of 1.", controlName);
        AddControl(volumeLevelHandler, controlName, 1);
    }

    public void AddControl(MuteStateHandler muteStateHandler, string muteName, int controlIndex)
    {
        using (PushProperties("AddControl"))
        {
            string arrayIndex = MuteKey(muteName, controlIndex);
            BiampMute? mute;
            lock (_controlsLock)
            {
                if (_mutes.TryGetValue(arrayIndex, out mute))
                    mute.MuteStateHandlers += muteStateHandler;
                else
                    _mutes.Add(arrayIndex, new BiampMute(muteStateHandler, muteName, controlIndex));
            }

            if (mute != null)
            {
                muteStateHandler.Invoke(mute.MuteState);
                LogVerbose("Adding a handler to the existing mute {muteName}", mute.ControlName);
            }
            else
            {
                var muteSubscription = $"{muteName} subscribe mute {controlIndex} {arrayIndex}\n";
                lock (_deviceSubscriptionsLock)
                    _deviceSubscriptions.Add(muteSubscription);
                if (CommunicationClient.ConnectionState == ConnectionState.Connected)
                    SendSubscription(muteSubscription);
                LogVerbose("Created a new mute {muteName}", muteName);
            }
        }
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        using (PushProperties("AddControl"))
            LogVerbose("The array index was not specified for {controlName} mute, using the default of 1.", muteName);
        AddControl(muteStateHandler, muteName, 1);
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        using (PushProperties("AddControl"))
            LogWarning("{Name} does not support string values, {ControlName} will never be reported", Name, controlName);
    }

    public void RecallPreset(int presetNumber)
    {
        using (PushProperties("RecallPreset"))
        {
            // DEVICE recallPreset 1001
            // Value must be between 1001 and 9999.
            if (presetNumber is > 1000 and < 10000)
            {
                SendNonPollCommand($"DEVICE recallPreset {presetNumber}\n");
                LogVerbose("Recalled preset {presetNumber}", presetNumber);
            }
            else
            {
                LogWarning("{presetNumber} is out of range", presetNumber);
            }
        }
    }

    public void RecallPreset(string presetName)
    {
        using (PushProperties("RecallPreset"))
        {
            // DEVICE recallPresetByName "EWIS_On"
            if (presetName.Length > 0)
            {
                SendNonPollCommand($"DEVICE recallPresetByName \"{presetName}\"\n");
                LogVerbose("Recalled preset \"{presetName}\"", presetName);
            }
        }
    }

    public void SetLevel(string controlName, int controlIndex, int percentage)
    {
        var gain = FindGain(LevelKey(controlName, controlIndex));
        if (gain == null)
        {
            using (PushProperties("SetLevel"))
                LogWarning("Unknown level {ControlName} at index {ControlIndex}, not setting it to {Percentage}", controlName, controlIndex, percentage);
            return;
        }
        SendNonPollCommand($"{controlName} set level {controlIndex} {gain.PercentageToDb(percentage)}\n");
    }

    public void LevelUp(string controlName, int index, int amount)
    {
        var gain = FindGain(LevelKey(controlName, index));
        if (gain == null)
        {
            using (PushProperties("LevelUp"))
                LogWarning("Unknown level {ControlName} at index {ControlIndex}, not raising it by {Amount}", controlName, index, amount);
            return;
        }
        SetLevel(controlName, index, gain.Volume + amount);
    }

    public void LevelDown(string controlName, int index, int amount)
    {
        var gain = FindGain(LevelKey(controlName, index));
        if (gain == null)
        {
            using (PushProperties("LevelDown"))
                LogWarning("Unknown level {ControlName} at index {ControlIndex}, not lowering it by {Amount}", controlName, index, amount);
            return;
        }
        SetLevel(controlName, index, gain.Volume - amount);
    }

    public void SetAudioMute(string controlName, int controlIndex, MuteState muteState)
    {
        SendNonPollCommand($"{controlName} set mute {controlIndex} {_muteStateDictionary[muteState]}\n");
    }

    public void ToggleAudioMute(string controlName, int controlIndex)
    {
        var mute = FindMute(MuteKey(controlName, controlIndex));
        if (mute == null)
        {
            using (PushProperties("ToggleAudioMute"))
                LogWarning("Unknown mute {ControlName} at index {ControlIndex}, not toggling it", controlName, controlIndex);
            return;
        }
        switch (mute.MuteState)
        {
            case MuteState.On:
                SetAudioMute(controlName, controlIndex, MuteState.Off);
                break;
            case MuteState.Off:
            default:
                SetAudioMute(controlName, controlIndex, MuteState.On);
                break;
        }
    }
    
    public override void SetValue(string controlName, string value)
    {
        using (PushProperties("SetValue"))
            LogWarning("{Name} does not support string values, not setting {ControlName} to {Value}", Name, controlName, value);
    }

    public void SetState(string controlName, int controlIndex, bool state)
    {
        SendNonPollCommand($"{controlName} set state {controlIndex} {state.ToString().ToLower()}\n");
    }

    public int GetLevel(string controlName, int controlIndex)
    {
        var gain = FindGain(LevelKey(controlName, controlIndex));
        if (gain == null)
        {
            using (PushProperties("GetLevel"))
                LogWarning("Unknown level {ControlName} at index {ControlIndex}, returning 0", controlName, controlIndex);
            return 0;
        }
        return gain.Volume;
    }

    public MuteState GetAudioMute(string controlName, int controlIndex)
    {
        var mute = FindMute(MuteKey(controlName, controlIndex));
        if (mute == null)
        {
            using (PushProperties("GetAudioMute"))
                LogWarning("Unknown mute {ControlName} at index {ControlIndex}, returning Unknown", controlName, controlIndex);
            return MuteState.Unknown;
        }
        return mute.MuteState;
    }
    
    
    public override void SetLevel(string controlName, int percentage) => SetLevel(controlName, 1, percentage);
    
    public override void LevelUp(string controlName, int amount = 1) => LevelUp(controlName, 1, amount);
    
    public override void LevelDown(string controlName, int amount = 1) => LevelDown(controlName, 1, amount);
    
    public override int GetLevel(string controlName) => GetLevel(controlName, 1);
    
    public override void SetAudioMute(string controlName, MuteState muteState) => SetAudioMute(controlName, 1, muteState);
    
    public override void ToggleAudioMute(string controlName) => ToggleAudioMute(controlName, 1);

    public override MuteState GetAudioMute(string controlName) => GetAudioMute(controlName, 1);

    public override string GetValue(string controlName)
    {
        using (PushProperties("GetValue"))
            LogWarning("{Name} does not support string values, returning an empty value for {ControlName}", Name, controlName);
        return "";
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

}