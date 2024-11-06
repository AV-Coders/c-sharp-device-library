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

public record Query(string ArrayIndex, BiampQuery BiampQuery, string DspCommand);

public record BiampAudioBlockInfo(string Name, string InstanceTag, int BlockIndex);

public class BiampGain : Fader
{
    public readonly int ControlIndex;
    public readonly string ControlName;
    public bool MinSet = false;
    public bool MaxSet = false;
    public BiampGain(VolumeLevelHandler volumeLevelHandler, string controlName, int controlIndex) : base(volumeLevelHandler, false)
    {
        ControlName = controlName;
        ControlIndex = controlIndex;
    }
}

public class BiampMute : Mute
{
    public readonly int ControlIndex;
    public readonly string ControlName;
    public BiampMute(MuteStateHandler muteStateHandler, string controlName, int controlIndex) : base(muteStateHandler)
    {
        ControlName = controlName;
        ControlIndex = controlIndex;
    }
}

public class BiampInt : StringValue
{
    public BiampInt(StringValueHandler stringValueHandler) : base(stringValueHandler)
    {
    }
}

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
        
        _dsp.AddControl(volumeLevel => VolumeLevelHandlers?.Invoke(volumeLevel), _instanceTag, _index);
        _dsp.AddControl(muteState => MuteStateHandlers?.Invoke(muteState), _instanceTag, _index);
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
    public static readonly string DefaultPassword = String.Empty;
    private readonly Dictionary<string, BiampGain> _gains = new();
    private readonly Dictionary<string, BiampMute> _mutes = new();
    private readonly Dictionary<string, BiampInt> _strings = new();
    
    private readonly Dictionary<MuteState, string> _muteStateDictionary;
    private readonly CommunicationClient _commsClient;
    private readonly Regex _subscriptionResponseParser;

    private readonly List<Query> _deviceQueries = new();
    private readonly List<string> _deviceSubscriptions = new();
    

    public BiampTtp(CommunicationClient commsClient, int pollTimeInMs = 29000) : base(pollTimeInMs)
    {
        _commsClient = commsClient;
        _commsClient.ResponseHandlers += HandleResponse;
        _commsClient.ConnectionStateHandlers += HandleConnectionState;
        
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
        {
            Log($"Re-establishing subscriptions in 5 seconds, subscription count: {_deviceSubscriptions.Count}");
            Thread.Sleep(TimeSpan.FromSeconds(5));
            _deviceSubscriptions.ForEach(subscriptionCommand =>
            {
                _commsClient.Send(subscriptionCommand);
                Log($"Sending: {subscriptionCommand}");
            });
            Thread.Sleep(TimeSpan.FromSeconds(1));
            PollWorker.Restart();
            
        }
        else
        {
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    protected override Task Poll(CancellationToken token)
    {
        if (_commsClient.GetConnectionState() != ConnectionState.Connected)
        {
            Log("IP Comms disconnected, not polling");
            return Task.CompletedTask;
        }
        Log("Device Connected - Polling");
        _commsClient.Send(_deviceQueries.Count > 0 ? 
            _deviceQueries[0].DspCommand :
            "DEVICE get version\n");

        return Task.CompletedTask;
    }

    private void HandleResponse(string response)
    {
        var lines = response.Split('\n');
        foreach (string line in lines)
        {
            if (line.StartsWith("+OK \"value\":"))
            {
                AllocateValueToBlock(line.Split(':')[1]);
            }
            else if (line.StartsWith("!"))
            {   
                ProcessChangeNotification(line);
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
            _gains[match.Groups[1].Value].SetVolumeFromDb(Double.Parse(match.Groups[2].Value));
            UpdateCommunicationState(CommunicationState.Okay);
        }

        if (_mutes.ContainsKey(match.Groups[1].Value))
        {
            _mutes[match.Groups[1].Value].MuteState =  match.Groups[2].Value.Contains("true") ? MuteState.On : MuteState.Off;
            UpdateCommunicationState(CommunicationState.Okay);
        }
    }

    private void AllocateValueToBlock(string value)
    {
        if (_deviceQueries.Count == 0)
            return;
        var currentPolledBlock = _deviceQueries[0].ArrayIndex;
        
        if (_gains.ContainsKey(currentPolledBlock))
        {
            switch (_deviceQueries[0].BiampQuery)
            {
                case BiampQuery.Level:
                    _gains[currentPolledBlock].SetVolumeFromDb(Double.Parse(value));
                    break;
                case BiampQuery.MaxGain:
                    _gains[currentPolledBlock].SetMaxGain(Double.Parse(value));
                    break;
                case BiampQuery.MinGain:
                    _gains[currentPolledBlock].SetMinGain(Double.Parse(value));
                    break;
            }
            
            UpdateCommunicationState(CommunicationState.Okay);
        }

        if (_mutes.ContainsKey(currentPolledBlock) && _deviceQueries[0].BiampQuery == BiampQuery.Mute)
        {
            _mutes[currentPolledBlock].MuteState = value == "true" ? MuteState.On : MuteState.Off;
            _mutes[currentPolledBlock].Report();
            
            UpdateCommunicationState(CommunicationState.Okay);
        }

        _deviceQueries.Remove(_deviceQueries[0]);
        if(_deviceQueries.Count > 0)
            _commsClient.Send(_deviceQueries[0].DspCommand);
    }

    public void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName, int controlIndex)
    {
        string arrayIndex = $"AvCodersLevel-{controlName}-{controlIndex}";
        if (_gains.TryGetValue(arrayIndex, out var gain))
        {
            gain.VolumeLevelHandlers += volumeLevelHandler;
            volumeLevelHandler.Invoke(gain.Volume);
        }
        else
        {
            _gains.Add(arrayIndex, new BiampGain(volumeLevelHandler, controlName, controlIndex));
            _deviceQueries.Add(new Query(arrayIndex, BiampQuery.MaxGain, $"{controlName} get maxLevel {controlIndex}\n"));
            _deviceQueries.Add(new Query(arrayIndex, BiampQuery.MinGain, $"{controlName} get minLevel {controlIndex}\n"));
            _deviceQueries.Add(new Query(arrayIndex, BiampQuery.Level, $"{controlName} get level {controlIndex}\n"));

            _commsClient.Send($"{controlName} subscribe level {controlIndex} {arrayIndex}\n");
            _deviceSubscriptions.Add($"{controlName} subscribe level {controlIndex} {arrayIndex}\n");
        }
    }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName)
    {
        AddControl(volumeLevelHandler, controlName, 1);
    }

    public void AddControl(MuteStateHandler muteStateHandler, string muteName, int controlIndex)
    {
        string arrayIndex = $"AvCodersMute-{muteName}-{controlIndex}";
        if (_mutes.TryGetValue(arrayIndex, out var mute))
        {
            mute.MuteStateHandlers += muteStateHandler;
            muteStateHandler.Invoke(mute.MuteState);
        }
        else
        {
            _mutes.Add(arrayIndex, new BiampMute(muteStateHandler, muteName, controlIndex));

            _commsClient.Send($"{muteName} subscribe mute {controlIndex} {arrayIndex}\n");
            _deviceSubscriptions.Add($"{muteName} subscribe mute {controlIndex} {arrayIndex}\n");
        }
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        AddControl(muteStateHandler, muteName, 1);
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (_strings.TryGetValue(controlName, out var s))
            s.StringValueHandlers += stringValueHandler;
        else
        {
            _strings.Add(controlName, new BiampInt(stringValueHandler));
        }
    }

    public void RecallPreset(int presetNumber)
    {
        // DEVICE recallPreset 1001
        // Value must be between 1001 and 9999.
        if(presetNumber > 1000 && presetNumber < 10000)
            _commsClient.Send($"DEVICE recallPreset {presetNumber}\n");
    }

    public void SetLevel(string controlName, int controlIndex, int percentage)
    {
        var index = $"AvCodersLevel-{controlName}-{controlIndex}";
        _commsClient.Send($"{controlName} set level {controlIndex} {_gains[index].PercentageToDb(percentage)}\n");
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
        _commsClient.Send($"{controlName} set mute {controlIndex} {_muteStateDictionary[muteState]}\n");
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
        throw new NotImplementedException();
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