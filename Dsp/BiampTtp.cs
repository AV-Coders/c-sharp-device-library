using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Dsp;

public enum BiampQuery
{
    Unknown,
    Level,
    MinGain,
    MaxGain,
    Mute
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
    private const string UnansweredQueryIssueKey = "unanswered-query";
    private const string ErrorReplyIssueKey = "error-reply";
    private const string ErrorReplyPrefix = "-";
    private const string PlainReplyPrefix = "+OK";
    private static readonly TimeSpan InterruptedReplyTimeout = TimeSpan.FromSeconds(1);
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
    private bool _lastRequestWasForTheVersion;
    private bool _clientHasReconnectedSinceLastPollLoop;
    private int _initialising;
    private int _outstandingCommands;
    private readonly object _outstandingCommandsLock = new();
    private readonly FaderCurve _faderCurve;

    public BiampTtp(CommunicationClient commsClient, string name = "Biamp", int pollIntervalInMilliseconds = 200, FaderCurve faderCurve = FaderCurve.Linear) : base(name, commsClient, pollIntervalInMilliseconds)
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
                    _outstandingCommands = 0;
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
                _interruptedAt = Stopwatch.GetTimestamp();
            }
            _pendingQueries.Enqueue(interrupted);
        }
        _currentQuery = null;
        lock (_outstandingCommandsLock)
            _outstandingCommands++;
        CommunicationClient.Send(command);
    }

    private void SendSubscription(string subscription)
    {
        lock (_outstandingCommandsLock)
            _outstandingCommands++;
        CommunicationClient.Send(subscription);
    }

    private bool TakeOutstandingCommand()
    {
        lock (_outstandingCommandsLock)
        {
            if (_outstandingCommands == 0)
                return false;
            _outstandingCommands--;
            return true;
        }
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
            if (Stopwatch.GetElapsedTime(_interruptedAt) < InterruptedReplyTimeout)
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
                await Task.Delay(TimeSpan.FromSeconds(10), token);
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
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                _currentQuery = null;
                return;
            }

            if (WaitingForTheInterruptedReply())
                return;

            if (_pendingQueries.TryDequeue(out var query))
            {
                using (Trigger.Resume(query.TriggerId))
                using (PushProperties("Poll"))
                {
                    LogVerbose("Polling for {query}", query.DspCommand);
                    _currentQuery = query;
                    CommunicationClient.Send(query.DspCommand);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }
            else
            {
                LogVerbose("There are no pending queries, getting the version");
                await GetTheVersion(token);
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
                    await Task.Delay(TimeSpan.FromMilliseconds(100), token);
                }

                await Task.Delay(TimeSpan.FromSeconds(4), token);

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

    private async Task GetTheVersion(CancellationToken token)
    {
        CommunicationClient.Send("DEVICE get version\n");
        _lastRequestWasForTheVersion = true;
        await Task.Delay(TimeSpan.FromSeconds(10), token);
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (string received in response.Split('\n'))
            {
                string line = received.TrimEnd('\r');
                if (line.StartsWith("+OK \"value\":"))
                {
                    if (_lastRequestWasForTheVersion)
                    {
                        _lastRequestWasForTheVersion = false;
                        continue;
                    }

                    AllocateValueToBlock(line.Split(':')[1]);
                }
                else if (line.StartsWith("!"))
                {
                    ProcessChangeNotification(line);
                }
                else if (line.StartsWith(PlainReplyPrefix))
                {
                    TakeOutstandingCommand();
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

    private void HandleErrorReply(string line)
    {
        if (TakeOutstandingCommand())
        {
            using (PushProperties("HandleErrorReply"))
                RaiseMomentaryIssue($"The Tesira returned an error to a command: {line}", ErrorReplyIssueKey);
            return;
        }
        var current = _currentQuery ?? TakeInterruptedQuery();
        _currentQuery = null;
        using (Trigger.Resume(current?.TriggerId))
        using (PushProperties("HandleErrorReply"))
        {
            ResolveIssue(UnansweredQueryIssueKey);
            RaiseMomentaryIssue(current == null
                    ? $"The Tesira returned an error: {line}"
                    : $"The Tesira returned an error to {current.DspCommand.Trim()}: {line}",
                ErrorReplyIssueKey);
        }
    }

    private void AllocateValueToBlock(string value)
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
            }

            ResolveIssue(UnansweredQueryIssueKey);
            _currentQuery = null;
        }
    }

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