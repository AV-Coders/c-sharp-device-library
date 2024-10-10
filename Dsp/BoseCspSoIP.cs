using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Dsp;
public class BoseGain : Fader
{
    public readonly string ControlName;

    public BoseGain(VolumeLevelHandler volumeLevelHandler, string controlName, double minGain, double maxGain) : base(volumeLevelHandler, false)
    {
        ControlName = controlName;
        SetMinGain(minGain);
        SetMaxGain(maxGain);
    }
    
    public new double PercentageToDb(int percentage)
    {
        if (percentage >= 100)
            return MaxGain;
        if (percentage <= 0)
            return MinGain;

        double db = MinGain + (Step * percentage);
        double remainder = db % 1;
        return remainder switch
        {
            < -0.5  => db -0.5 - remainder,
            < 0     => db - remainder,
            0       => db,
            < 0.5   => db - remainder,
            _       => db + 0.5 - remainder
        };
    }
}

public class BoseMute : Mute
{
    public readonly string ControlName;
    public BoseMute(MuteStateHandler muteStateHandler, string controlName) : base(muteStateHandler)
    {
        ControlName = controlName;
    }
}

public class BoseSelect : StringValue
{
    public readonly string ControlName;

    public BoseSelect(StringValueHandler stringValueHandler, string controlName) : base(stringValueHandler)
    {
        ControlName = controlName;
    }
}

public class BoseCspSoIP : Dsp
{
    public static readonly ushort DefaultPort = 10055;
    private readonly Dictionary<string, BoseGain> _gains = new();
    private readonly Dictionary<string, BoseMute> _mutes = new();
    private readonly Dictionary<string, BoseSelect> _selects = new();
    private readonly TcpClient _tcpClient;
    private readonly Regex _responseParser;
    
    public BoseCspSoIP(TcpClient tcpClient, int pollTime = 50000) : base(pollTime)
    {
        _tcpClient = tcpClient;
        _tcpClient.ResponseHandlers += HandleResponse;
        
        string responsePattern = "GA\\\"([^\\\"]+)\\\"\\>(\\d+)=([a-zA-Z0-9.-]+)";
        _responseParser = new(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
        
    }

    private void HandleResponse(string response)
    {
        var lines = response.Split('\r').ToList();
        lines.ForEach(line =>
        {
            _ = line.TrimEnd('\r');
            var match = _responseParser.Match(line);
            
            if (match.Groups[2].Value == "1" && _gains.ContainsKey(match.Groups[1].Value))
                _gains[match.Groups[1].Value].SetVolumeFromDb(Double.Parse(match.Groups[3].Value));

            if (match.Groups[2].Value == "2" && _mutes.ContainsKey(match.Groups[1].Value))
                _mutes[match.Groups[1].Value].MuteState = match.Groups[3].Value switch
                {
                    "O" => MuteState.On,
                    "F" => MuteState.Off,
                    _ => MuteState.Unknown
                };
        });
    }

    protected override async Task Poll(CancellationToken token)
    {
        if (_tcpClient.GetConnectionState() != ConnectionState.Connected)
            return;
        
        foreach (string key in _gains.Keys)
        {
            await Task.Delay(30, token);
            _tcpClient.Send($"GA\"{key}\">1\r");
            
        }
        foreach (string key in _mutes.Keys)
        {
            await Task.Delay(30, token);
            _tcpClient.Send($"GA\"{key}>2\r");
        }

        foreach (string key in _selects.Keys)
        {
            await Task.Delay(30, token);
            _tcpClient.Send($"GA\"{key}>1\r");
        }
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName) 
        => AddControl(volumeLevelHandler, controlName, -60.5, 12.0);

    public void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName, double minGain, double maxGain)
    {if (_gains.TryGetValue(controlName, out var gain))
        {
            gain.VolumeLevelHandlers += volumeLevelHandler;
            volumeLevelHandler.Invoke(gain.Volume);
        }
        else
        {
            _gains.Add(controlName, new BoseGain(volumeLevelHandler, controlName, minGain, maxGain));
            // GA"Fitness Gain">1\r 
            _tcpClient.Send($"GA\"{controlName} Gain\">1\r");
        }
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        if (_mutes.TryGetValue(muteName, out var mute))
        {
            mute.MuteStateHandlers += muteStateHandler;
            muteStateHandler.Invoke(mute.MuteState);
        }
        else
        {
            _mutes.Add(muteName, new BoseMute(muteStateHandler, muteName));

            _tcpClient.Send($"GA\"{muteName} Gain\">2\r");
        }
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (_selects.TryGetValue(controlName, out var select))
        {
            select.StringValueHandlers += stringValueHandler;
            stringValueHandler.Invoke(select.Value);
        }
        else
        {
            _selects.Add(controlName, new BoseSelect(stringValueHandler, controlName));
            _tcpClient.Send($"GA\"{controlName} Selector\">1\r");
        }
    }

    public override void SetLevel(string controlName, int percentage)
    {
        _tcpClient.Send($"SA\"{controlName} Gain\">1={_gains[controlName].PercentageToDb(percentage)}\r");
    }

    public override void LevelUp(string controlName, int amount = 1) 
        => _tcpClient.Send($"SA\"{controlName} Gain\">3={amount}\r");

    public override void LevelDown(string controlName, int amount = 1) 
        => _tcpClient.Send($"SA\"{controlName} Gain\">3=-{amount}\r");

    public override void SetAudioMute(string controlName, MuteState muteState)
    {
        string muteCommand = muteState switch
        {
            MuteState.On => "O",
            _ => "F",
        };
        _tcpClient.Send($"SA\"{controlName} Gain\">2={muteCommand}\r");
    }

    public override void ToggleAudioMute(string controlName)
        => _tcpClient.Send($"SA\"{controlName} Gain\">2=T\r");

    public override void SetValue(string controlName, string value)
        => _tcpClient.Send($"SA\"{controlName} Selector\">1={value}\r");

    public override int GetLevel(string controlName)
        => !_gains.TryGetValue(controlName, out var gain) ? 0 : gain.Volume;

    public override MuteState GetAudioMute(string controlName)
        => !_mutes.TryGetValue(controlName, out var mute) ? MuteState.Unknown : mute.MuteState;

    public override string GetValue(string controlName)
        => !_selects.TryGetValue(controlName, out var select) ? String.Empty : select.Value;
}