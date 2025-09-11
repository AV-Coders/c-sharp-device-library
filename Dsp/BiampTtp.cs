using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AVCoders.Core;
using Serilog;

namespace AVCoders.Dsp;

public enum BiampQuery
{
    Unknown,
    Level,
    MinGain,
    MaxGain,
    Mute
}

public record Query(string ArrayIndex, BiampQuery BiampQuery, string DspCommand);

public record BiampAudioBlockInfo(string Name, string InstanceTag, int BlockIndex);

public class BiampGain(VolumeLevelHandler volumeLevelHandler, string controlName, int controlIndex)
    : Fader(volumeLevelHandler, false)
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
    
    public override void SetAudioMute(MuteState state) => _dsp.SetAudioMute(_instanceTag, state);
}

public class BiampTtp : Dsp
{
    public static readonly ushort DefaultPort = 22;
    public static readonly string DefaultUser = "default";
    public static readonly string DefaultPassword = string.Empty;
    private readonly Dictionary<string, BiampGain> _gains = new();
    private readonly Dictionary<string, BiampMute> _mutes = new();
    private readonly Dictionary<string, BiampInt> _strings = new();
    
    private readonly Dictionary<MuteState, string> _muteStateDictionary;
    private readonly Regex _subscriptionResponseParser;

    private readonly List<Query> _moduleQueries = [];
    private readonly ConcurrentQueue<Query> _pendingQueries = new();
    private Query? _currentQuery = null;
    private readonly List<string> _deviceSubscriptions = [];
    private int _loopsSinceLastFetch = 0;
    private int _loopsSinceLastRequest = 0;
    private bool _lastRequestWasForTheVersion;


    public BiampTtp(CommunicationClient commsClient, string name = "Biamp", int pollIntervalInSeconds = 1) : base(name, commsClient, pollIntervalInSeconds)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        
        _muteStateDictionary = new Dictionary<MuteState, string>
        {
            { MuteState.On, "true" },
            { MuteState.Off, "false" }
        };
        
        string subscriptionResponsePattern = "\":\"(.+)\" \"value\":(.+)";
        _subscriptionResponseParser = new Regex(subscriptionResponsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));

        HandleConnectionState(commsClient.GetConnectionState());
    }
    
    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState == ConnectionState.Connected)
            Resubscribe();
        else
            CommunicationState = CommunicationState.Error;
    }

    private void SendNonPollCommand(string command)
    {
        _currentQuery = null;
        CommunicationClient.Send(command);
    }

    private void Resubscribe()
    {
        using (PushProperties("Resubscribe"))
        {
            _currentQuery = null;
            PollWorker.Stop();
            Log.Verbose("Re-establishing subscriptions in 5 seconds, subscription count: {DeviceSubscriptionsCount}", _deviceSubscriptions.Count);
            Thread.Sleep(TimeSpan.FromSeconds(5));
            _deviceSubscriptions.ForEach(subscriptionCommand =>
            {
                CommunicationClient.Send(subscriptionCommand);
            });
            Thread.Sleep(TimeSpan.FromSeconds(1));
            PollWorker.Restart();
        }
    }

    protected override async Task Poll(CancellationToken token)
    {
        using (PushProperties("Poll"))
        {
            if (CommunicationClient.GetConnectionState() != ConnectionState.Connected)
            {
                Log.Verbose("IP Comms disconnected, not polling");
                return;
            }

            if (_currentQuery != null)
            {
                _loopsSinceLastRequest++;
                if (_loopsSinceLastRequest > 10)
                {
                    Log.Error("Unable to get a response for the last query {query}", _currentQuery.DspCommand);
                    _currentQuery = null;
                    _loopsSinceLastRequest = 0;
                }
                return;
            }

            if (_pendingQueries.TryDequeue(out var query))
            {
                _currentQuery = query;
                CommunicationClient.Send(_currentQuery.DspCommand);
            }
            else
            {
                CommunicationClient.Send("DEVICE get version\n");
                _lastRequestWasForTheVersion = true;
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                _loopsSinceLastFetch++;
                if (_loopsSinceLastFetch > 60)
                {
                    Reinitialise();
                    _loopsSinceLastFetch = 0;
                }
            }
        }
    }

    public override void Reinitialise()
    {
        using (PushProperties("Reinitialise"))
        {
            Log.Verbose("Reinitialising Biamp TTP");
            _moduleQueries.ForEach(x => _pendingQueries.Enqueue(x));
        }
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            var lines = response.Split('\n');
            foreach (string line in lines)
            {
                if (line.StartsWith("+OK \"value\":"))
                {
                    if (_lastRequestWasForTheVersion)
                    {
                        _lastRequestWasForTheVersion = false;
                        return;
                    }

                    AllocateValueToBlock(line.Split(':')[1]);
                }
                else if (line.StartsWith("!"))
                {
                    ProcessChangeNotification(line);
                }
                else if (line.StartsWith("Welcome to the Tesira Text Protocol Server"))
                {
                    Thread.Sleep(5000);
                    Resubscribe();
                }
            }
        }
    }

    private void ProcessChangeNotification(string line)
    {
        if (!line.Contains("publishToken"))
            return;
        var match = _subscriptionResponseParser.Match(line);
        if (_gains.ContainsKey(match.Groups[1].Value))
        {
            _gains[match.Groups[1].Value].SetVolumeFromDb(double.Parse(match.Groups[2].Value));
            CommunicationState = CommunicationState.Okay;
        }

        if (_mutes.ContainsKey(match.Groups[1].Value))
        {
            _mutes[match.Groups[1].Value].MuteState =  match.Groups[2].Value.Contains("true") ? MuteState.On : MuteState.Off;
            CommunicationState = CommunicationState.Okay;
        }
    }

    private void AllocateValueToBlock(string value)
    {
        using (PushProperties("AllocateValueToBlock"))
        {
            if (_currentQuery == null)
            {
                Log.Verbose("No current query, ignoring response");
                return;
            }

            var currentPolledBlock = _currentQuery.ArrayIndex;

            switch (_currentQuery.BiampQuery)
            {
                case BiampQuery.Mute:
                    if (_mutes.TryGetValue(currentPolledBlock, out var theMute))
                    {
                        theMute.MuteState = value == "true" ? MuteState.On : MuteState.Off;
                        CommunicationState = CommunicationState.Okay;
                    }
                    else
                        Log.Error("Error getting the Mute state, The polled block should be a mute, but it isn't. {blockKey}", _currentQuery.DspCommand);
                    break;
                case BiampQuery.Level:
                    if (_gains.TryGetValue(currentPolledBlock, out var level))
                        level.SetVolumeFromDb(double.Parse(value));
                    else
                        Log.Error("Error getting the current level, the polled block should be a level, but it isn't. {blockKey}", _currentQuery.DspCommand);
                    break;
                case BiampQuery.MaxGain:
                    if (_gains.TryGetValue(currentPolledBlock, out var max))
                        max.SetMaxGain(double.Parse(value));
                    else
                        Log.Error("Error getting the Max gain, The polled block should be a level, but it isn't. {blockKey}", _currentQuery.DspCommand);
                    break;
                case BiampQuery.MinGain:
                    if (_gains.TryGetValue(currentPolledBlock, out var min))
                        min.SetMinGain(double.Parse(value));
                    else
                        Log.Error("Error getting the Min gain, The polled block should be a level, but it isn't. {blockKey}", _currentQuery.DspCommand);
                    break;
            }

            _currentQuery = null;
        }
    }

    public void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName, int controlIndex)
    {
        string arrayIndex = $"AvCodersLevel-{controlName}-{controlIndex}";
        if (_gains.TryGetValue(arrayIndex, out var gain))
        {
            gain.VolumeLevelHandlers += volumeLevelHandler;
            volumeLevelHandler.Invoke(gain.Volume);
            Log.Verbose("Adding a handler to the existing gain {gainName}", gain.ControlName);
        }
        else
        {
            _gains.Add(arrayIndex, new BiampGain(volumeLevelHandler, controlName, controlIndex));
            _moduleQueries.Add(new Query(arrayIndex, BiampQuery.MaxGain, $"{controlName} get maxLevel {controlIndex}\n"));
            _moduleQueries.Add(new Query(arrayIndex, BiampQuery.MinGain, $"{controlName} get minLevel {controlIndex}\n"));
            _moduleQueries.Add(new Query(arrayIndex, BiampQuery.Level, $"{controlName} get level {controlIndex}\n"));
            _pendingQueries.Enqueue(new Query(arrayIndex, BiampQuery.MaxGain, $"{controlName} get maxLevel {controlIndex}\n"));
            _pendingQueries.Enqueue(new Query(arrayIndex, BiampQuery.MinGain, $"{controlName} get minLevel {controlIndex}\n"));
            _pendingQueries.Enqueue(new Query(arrayIndex, BiampQuery.Level, $"{controlName} get level {controlIndex}\n"));

            CommunicationClient.Send($"{controlName} subscribe level {controlIndex} {arrayIndex}\n");
            _deviceSubscriptions.Add($"{controlName} subscribe level {controlIndex} {arrayIndex}\n");
            Log.Verbose("Created a new gain {gainName}", controlName);
        }
    }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName)
    {
        Log.Verbose("The array index was not specified for {controlName} level/gain, using the default of 1.", controlName);
        AddControl(volumeLevelHandler, controlName, 1);
    }

    public void AddControl(MuteStateHandler muteStateHandler, string muteName, int controlIndex)
    {
        string arrayIndex = $"AvCodersMute-{muteName}-{controlIndex}";
        if (_mutes.TryGetValue(arrayIndex, out var mute))
        {
            mute.MuteStateHandlers += muteStateHandler;
            muteStateHandler.Invoke(mute.MuteState);
            Log.Verbose("Adding a handler to the existing mute {muteName}", mute.ControlName);
        }
        else
        {
            _mutes.Add(arrayIndex, new BiampMute(muteStateHandler, muteName, controlIndex));

            CommunicationClient.Send($"{muteName} subscribe mute {controlIndex} {arrayIndex}\n");
            _deviceSubscriptions.Add($"{muteName} subscribe mute {controlIndex} {arrayIndex}\n");
            Log.Verbose("Created a new mute {muteName}", muteName);
        }
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        Log.Verbose("The array index was not specified for {controlName} mute, using the default of 1.", muteName);       
        AddControl(muteStateHandler, muteName, 1);
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (_strings.TryGetValue(controlName, out var s))
        {
            s.StringValueHandlers += stringValueHandler;
            Log.Verbose("Adding a handler to the existing string/value {controlName}", controlName);
        }
        else
        {
            _strings.Add(controlName, new BiampInt(stringValueHandler));
            Log.Verbose("Created a new string/value handler {controlName}", controlName);
        }
    }

    public void RecallPreset(int presetNumber)
    {
        // DEVICE recallPreset 1001
        // Value must be between 1001 and 9999.
        if (presetNumber is > 1000 and < 10000)
        {
            CommunicationClient.Send($"DEVICE recallPreset {presetNumber}\n");
            Log.Verbose("Recalled preset {presetNumber}", presetNumber);
        }
        else
        {
            Log.Error("{presetNumber} is out of range", presetNumber);
        }
    }

    public void RecallPreset(string presetName)
    {
        // DEVICE recallPresetByName "EWIS_On"
        if (presetName.Length > 0)
        {
            SendNonPollCommand($"DEVICE recallPresetByName \"{presetName}\"\n");
            Log.Verbose("Recalled preset \"{presetName}\"", presetName);
        }
    }

    public void SetLevel(string controlName, int controlIndex, int percentage)
    {
        var index = $"AvCodersLevel-{controlName}-{controlIndex}";
        SendNonPollCommand($"{controlName} set level {controlIndex} {_gains[index].PercentageToDb(percentage)}\n");
    }

    public void LevelUp(string controlName, int index, int amount)
    {
        SetLevel(controlName, index, _gains[$"AvCodersLevel-{controlName}-{index}"].Volume + amount);
    }

    public void LevelDown(string controlName, int index, int amount)
    {
        SetLevel(controlName, index, _gains[$"AvCodersLevel-{controlName}-{index}"].Volume - amount);
    }

    public void SetAudioMute(string controlName, int controlIndex, MuteState muteState)
    {
        SendNonPollCommand($"{controlName} set mute {controlIndex} {_muteStateDictionary[muteState]}\n");
    }

    public void ToggleAudioMute(string controlName, int controlIndex)
    {
        switch (_mutes[$"AvCodersMute-{controlName}-{controlIndex}"].MuteState)
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
    }

    public void SetState(string controlName, int controlIndex, bool state)
    {
        SendNonPollCommand($"{controlName} set state {controlIndex} {state.ToString().ToLower()}\n");
    }

    public int GetLevel(string controlName, int controlIndex)
    {
        if (!_gains.ContainsKey($"AvCodersLevel-{controlName}-{controlIndex}"))
            return 0;
        return _gains[$"AvCodersLevel-{controlName}-{controlIndex}"].Volume;
    }

    public MuteState GetAudioMute(string controlName, int controlIndex)
    {
        if (!_mutes.ContainsKey($"AvCodersMute-{controlName}-{controlIndex}"))
            return MuteState.Unknown;
        return _mutes[$"AvCodersMute-{controlName}-{controlIndex}"].MuteState;
    }
    
    
    public override void SetLevel(string controlName, int percentage) => SetLevel(controlName, 1, percentage);
    
    public override void LevelUp(string controlName, int amount = 1) => LevelUp(controlName, 1, amount);
    
    public override void LevelDown(string controlName, int amount = 1) => LevelDown(controlName, 1, amount);
    
    public override int GetLevel(string controlName) => GetLevel(controlName, 1);
    
    public override void SetAudioMute(string controlName, MuteState muteState) => SetAudioMute(controlName, 1, muteState);
    
    public override void ToggleAudioMute(string controlName) => ToggleAudioMute(controlName, 1);

    public override MuteState GetAudioMute(string controlName) => GetAudioMute(controlName, 1);

    public override string GetValue(string controlName) => "";
    
    public override void PowerOn() { }

    public override void PowerOff() { }

}