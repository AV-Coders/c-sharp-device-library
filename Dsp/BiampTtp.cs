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

public class BiampTtp : Dsp
{
    public static ushort DefaultPort = 22;
    public static string DefaultUser = "default";
    public static string DefaultPassword = String.Empty;
    private readonly Dictionary<string, BiampGain> _gains = new();
    private readonly Dictionary<string, BiampMute> _mutes = new();
    private readonly Dictionary<string, BiampInt> _strings = new();
    
    private readonly Dictionary<MuteState, string> _muteStateDictionary;
    private readonly IpComms _tcpClient;
    private Thread _pollThread;
    private readonly int _pollTime;
    private readonly Regex _subscriptionResponseParser;

    private readonly List<Query> _deviceQueries = new();
    private readonly List<string> _deviceSubscriptions = new();
    

    public BiampTtp(IpComms tcpClient, int pollTime = 50000)
    {
        _tcpClient = tcpClient;
        _pollTime = pollTime;
        _tcpClient.ResponseHandlers += HandleResponse;
        _tcpClient.ConnectionStateHandlers += HandleConnectionState;
        
        _muteStateDictionary = new Dictionary<MuteState, string>
        {
            { MuteState.On, "true" },
            { MuteState.Off, "false" }
        };
        
        
        string subscriptionResponsePattern = "\":\"(.+)\" \"value\":(.+)";
        _subscriptionResponseParser = new Regex(subscriptionResponsePattern);

        HandleConnectionState(tcpClient.GetConnectionState());
    }
    
    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState == ConnectionState.Connected)
        {
            LogHandlers?.Invoke($"Re-establising subscriptions, subscription count: {_deviceSubscriptions.Count}");
            _deviceSubscriptions.ForEach(subscriptionCommand => _tcpClient.Send(subscriptionCommand));
            _pollThread = new Thread(_ => { PollDspThreadFunction(); });
            _pollThread.Start();
        }
        else
        {
            CommunicationState = CommunicationState.Error;
        }
    }

    private void PollDspThreadFunction()
    {
        while (_tcpClient.GetConnectionState() == ConnectionState.Connected)
        {
            if(_deviceQueries.Count > 0)
                _tcpClient.Send(_deviceQueries[0].DspCommand);
            else
                _tcpClient.Send("DEVICE get version\n");
            Thread.Sleep(_pollTime);
        }
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
        // ! "publishToken":"AvCodersLevel-court_mics-1" "value":-6.000000
        if (!line.Contains("publishToken"))
            return;
        var match = _subscriptionResponseParser.Match(line);
        if (_gains.ContainsKey(match.Groups[1].Value))
        {
            _gains[match.Groups[1].Value].SetVolumeFromDb(Double.Parse(match.Groups[2].Value));
            _gains[match.Groups[1].Value].Report();
        }

        if (_mutes.ContainsKey(match.Groups[1].Value))
        {
            _mutes[match.Groups[1].Value].MuteState =  match.Groups[2].Value.Contains("true") ? MuteState.On : MuteState.Off;
            _mutes[match.Groups[1].Value].Report();
        }
    }

    private void AllocateValueToBlock(string value)
    {
        // +OK "value":12.000000
        if (_deviceQueries.Count == 0)
            return;
        var currentPolledBlock = _deviceQueries[0].ArrayIndex;
        
        if (_gains.ContainsKey(currentPolledBlock))
        {
            switch (_deviceQueries[0].BiampQuery)
            {
                case BiampQuery.Level:
                    _gains[currentPolledBlock].SetVolumeFromDb(Double.Parse(value));
                    _gains[currentPolledBlock].Report();
                    break;
                case BiampQuery.MaxGain:
                    _gains[currentPolledBlock].SetMaxGain(Double.Parse(value));
                    break;
                case BiampQuery.MinGain:
                    _gains[currentPolledBlock].SetMinGain(Double.Parse(value));
                    break;
            }
        }

        if (_mutes.ContainsKey(currentPolledBlock) && _deviceQueries[0].BiampQuery == BiampQuery.Mute)
        {
            _mutes[currentPolledBlock].MuteState = value == "true" ? MuteState.On : MuteState.Off;
            _mutes[currentPolledBlock].Report();
        }

        _deviceQueries.Remove(_deviceQueries[0]);
        if(_deviceQueries.Count > 0)
            _tcpClient.Send(_deviceQueries[0].DspCommand);
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

            _tcpClient.Send($"{controlName} subscribe level {controlIndex} {arrayIndex}\n");
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

            _tcpClient.Send($"{muteName} subscribe mute {controlIndex} {arrayIndex}\n");
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
            _tcpClient.Send($"DEVICE recallPreset {presetNumber}");
    }

    public void SetLevel(string controlName, int controlIndex, int percentage)
    {
        var index = $"AvCodersLevel-{controlName}-{controlIndex}";
        _tcpClient.Send($"{controlName} set level {controlIndex} {_gains[index].PercentageToDb(percentage)}\n");
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
        _tcpClient.Send($"{controlName} set mute {controlIndex} {_muteStateDictionary[muteState]}\n");
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