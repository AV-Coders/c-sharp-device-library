using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Dsp;

public record QscAudioBlockInfo(string Name, string LevelInstanceTag, string MuteInstanceTag);

public class QscGain : Fader
{
    public QscGain(VolumeLevelHandler volumeLevelHandler) : base(volumeLevelHandler, false)
    {
    }
}

public class QscMute : Mute
{
    public QscMute(MuteStateHandler muteStateHandler) : base(muteStateHandler)
    {
    }
}

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
        _dsp.AddControl(volumeLevel => VolumeLevelHandlers?.Invoke(volumeLevel), _levelNamedControl);
        _dsp.AddControl(muteState => MuteStateHandlers?.Invoke(muteState), _muteNamedControl);
    }
    
    public override void LevelUp(int amount) => _dsp.LevelUp(_levelNamedControl, amount);

    public override void LevelDown(int amount) => _dsp.LevelDown(_levelNamedControl, amount);

    public override void SetLevel(int percentage) => _dsp.SetLevel(_levelNamedControl, percentage);

    public override void ToggleAudioMute() => _dsp.ToggleAudioMute(_muteNamedControl);
    
    public override void SetAudioMute(MuteState state) => _dsp.SetAudioMute(_muteNamedControl, state);
}

public class QsysEcp : Dsp
{
    public static readonly ushort DefaultPort = 1702;
    private Dictionary<string, QscGain> _gains = new Dictionary<string, QscGain>();
    private Dictionary<string, QscMute> _mutes = new Dictionary<string, QscMute>();
    private Dictionary<string, QscInt> _strings = new Dictionary<string, QscInt>();
    private readonly Regex _responseParser;

    // There is a limit of 4 change groups.
    private const int ChangeGroupGains = 1;
    private const int ChangeGroupMutes = 2;
    private const int ChangeGroupStrings = 3;

    private readonly Dictionary<MuteState, string> _muteStateDictionary;
    private readonly TcpClient _tcpClient;

    public QsysEcp(TcpClient tcpClient, int pollTimeInMs = 50000) : base(pollTimeInMs)
    {
        _tcpClient = tcpClient;
        _tcpClient.SetPort(DefaultPort);
        _tcpClient.ResponseHandlers += HandleResponse;
        _tcpClient.ConnectionStateHandlers += HandleConnectionState;

        string responsePattern = "cv\\s\"([^\"]+)\"\\s\"([^\"]+)\"\\s(-?\\d+(\\.\\d+)?)\\s(-?\\d+(\\.\\d+)?)";
        _responseParser = new Regex(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(30));

        _muteStateDictionary = new Dictionary<MuteState, string>
        {
            { MuteState.On, "muted" },
            { MuteState.Off, "unmuted" }
        };

        HandleConnectionState(tcpClient.GetConnectionState());
    }

    private void HandleResponse(string response)
    {
        var lines = response.Split('\n');
        foreach (string line in lines)
        {
            if (line.StartsWith("cv"))
            {
                ProcessValueChange(line);
                CommunicationState = CommunicationState.Okay;
            }
            else if (response.StartsWith("sr"))
                CommunicationState = CommunicationState.Okay;
            else if (response.Contains("bad_id"))
            {
                CommunicationState = CommunicationState.Error;
                Error($"Invalid named control found: {response}");
            }
        }
        
    }

    private void ProcessValueChange(string response)
    {
        if (response.Length < 2)
            return;

        try
        {
            var matches = _responseParser.Matches(response);

            var controlName = matches[0].Groups[1].Value;
            if (_gains.ContainsKey(controlName)) // Eg:cv "Zone 1 BGM Gain" "-6.40dB" -6.4 0.989744
                _gains[controlName].SetVolumeFromPercentage(double.Parse(matches[0].Groups[5].Value) * 100);

            if (_mutes.ContainsKey(controlName)) // Eg:cv "Zone 1 BGM Mute" "unmuted" 1 1
                _mutes[controlName].MuteState = matches[0].Groups[2].Value.Contains("unmuted") ? MuteState.Off : MuteState.On;

            if (_strings.ContainsKey(controlName)) // Eg:cv "Zone 1 BGM Select" "5 sfasdfa" 5 0.571429
                _strings[controlName].Value = matches[0].Groups[2].Value;
        }
        catch (Exception e)
        {
            LogHandlers?.Invoke($"QsysEcp: {e}\n {e.StackTrace}", EventLevel.Error);
        }
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState == ConnectionState.Connected)
        {
            new Thread(_ =>
            {
                _tcpClient.Send($"cgc {ChangeGroupGains}\n");
                _tcpClient.Send($"cgc {ChangeGroupMutes}\n");
                _tcpClient.Send($"cgc {ChangeGroupStrings}\n");
                Thread.Sleep(500);

                AddControlsToChangeGroup(ChangeGroupGains, _gains.Keys.ToList());
                Thread.Sleep(500);
                AddControlsToChangeGroup(ChangeGroupMutes, _mutes.Keys.ToList());
                Thread.Sleep(500);
                AddControlsToChangeGroup(ChangeGroupStrings, _strings.Keys.ToList());
                Thread.Sleep(500);

                ScheduleChangeGroupPoll(ChangeGroupGains);
                ScheduleChangeGroupPoll(ChangeGroupMutes);
                ScheduleChangeGroupPoll(ChangeGroupStrings);
            }).Start();


            GetAllControlStates();
        }
        else
        {
            CommunicationState = CommunicationState.Error;
        }
    }

    private void ScheduleChangeGroupPoll(int changeGroupId)
    {
        // The device only reports on a change
        _tcpClient.Send($"cgsna {changeGroupId} 100\n");
    }

    private void AddControlsToChangeGroup(int groupId, List<String> controlNames)
    {
        controlNames.ForEach(controlName => _tcpClient.Send($"cga {groupId} \"{controlName}\"\n"));
    }

    protected override Task Poll(CancellationToken token)
    {
        if(_tcpClient.GetConnectionState() == ConnectionState.Connected)
            _tcpClient.Send("sg\n"); 
        return Task.CompletedTask;
    }

    public void GetAllControlStates()
    {
        new Thread(_ =>
        {
            foreach (string key in _gains.Keys)
            {
                GetControl(key);
                Thread.Sleep(100);
            }

            foreach (string key in _mutes.Keys)
            {
                GetControl(key);
                Thread.Sleep(100);
            }

            foreach (string key in _strings.Keys)
            {
                GetControl(key);
                Thread.Sleep(100);
            }
        }).Start();
    }

    private void GetControl(string controlName)
    {
        _tcpClient.Send($"cg \"{controlName}\"\n");
    }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string levelName)
    {
        if (_gains.TryGetValue(levelName, out var gain))
            gain.VolumeLevelHandlers += volumeLevelHandler;
        else
        {
            _gains.Add(levelName, new QscGain(volumeLevelHandler));
            AddControlsToChangeGroup(ChangeGroupGains, new List<string> { levelName });
        }

        GetControl(levelName);
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        if (_mutes.TryGetValue(muteName, out var mute))
            mute.MuteStateHandlers += muteStateHandler;
        else
        {
            _mutes.Add(muteName, new QscMute(muteStateHandler));
            AddControlsToChangeGroup(ChangeGroupMutes, new List<string> { muteName });
        }

        GetControl(muteName);
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (_strings.TryGetValue(controlName, out var block))
            block.StringValueHandlers += stringValueHandler;
        else
        {
            _strings.Add(controlName, new QscInt(stringValueHandler));
            AddControlsToChangeGroup(ChangeGroupStrings, new List<string> { controlName });
        }

        GetControl(controlName);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override void SetLevel(string gainName, int percentage)
    {
        _tcpClient.Send($"csp \"{gainName}\" {Math.Round((double)percentage / 100, 2)}\n");
    }

    public override void LevelUp(string controlName, int amount = 1)
    {
        SetLevel(controlName, _gains[controlName].Volume + amount);
    }

    public override void LevelDown(string controlName, int amount = 1)
    {
        SetLevel(controlName, _gains[controlName].Volume - amount);
    }

    public override int GetLevel(string gainName)
    {
        if (!_gains.ContainsKey(gainName))
            return 0;
        return _gains[gainName].Volume;
    }

    public override void SetAudioMute(string muteName, MuteState state)
    {
        _tcpClient.Send($"css \"{muteName}\" {_muteStateDictionary[state]}\n");
    }

    public override void ToggleAudioMute(string muteName)
    {
        switch (_mutes[muteName].MuteState)
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
        if (!_mutes.ContainsKey(muteName))
            return MuteState.Unknown;
        return _mutes[muteName].MuteState;
    }

    public override void SetValue(string stringName, string value)
    {
        _tcpClient.Send($"css \"{stringName}\" {value}\n");
    }

    public override string GetValue(string intName)
    {
        if (!_strings.ContainsKey(intName))
            return "";
        return _strings[intName].Value;
    }

    public override void Reinitialise() => GetAllControlStates();

    public void RecallPreset(string controlName)
    {
            _tcpClient.Send($"csv \"{controlName}\" 1 \n");
    }
}