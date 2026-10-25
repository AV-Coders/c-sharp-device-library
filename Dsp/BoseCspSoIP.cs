using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Dsp;
public class BoseGain : Fader
{
    public readonly string ControlName;

    public BoseGain(VolumeLevelHandler volumeLevelHandler, string controlName, double minGain, double maxGain) : base(volumeLevelHandler)
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

public class BoseMute(MuteStateHandler muteStateHandler, string controlName) : Mute(muteStateHandler)
{
    public readonly string ControlName = controlName;
}

public class BoseSelect(StringValueHandler stringValueHandler, string controlName) : StringValue(stringValueHandler)
{
    public readonly string ControlName = controlName;
}

public record BoseAudioBlockInfo(string Name, string ControlName);

public class BoseCspSoIPVolumeControl : VolumeControl
{
    private readonly string _controlName;
    private readonly BoseCspSoIP _dsp;

    public BoseCspSoIPVolumeControl(BoseAudioBlockInfo audioBlockInfo, VolumeType type, BoseCspSoIP dsp) : base(audioBlockInfo.Name, type)
    {
        _dsp = dsp;
        _controlName = audioBlockInfo.ControlName;
        _dsp.AddControl(volumeLevel => Volume = volumeLevel, _controlName);
        _dsp.AddControl(muteState => MuteState = muteState, _controlName);
    }
    
    public override void LevelUp(int amount) => _dsp.LevelUp(_controlName, amount);

    public override void LevelDown(int amount) => _dsp.LevelDown(_controlName, amount);

    public override void SetLevel(int percentage) => _dsp.SetLevel(_controlName, percentage);

    public override void ToggleAudioMute() => _dsp.ToggleAudioMute(_controlName);
    
    public override void SetAudioMute(MuteState state) => _dsp.SetAudioMute(_controlName, state);
}

public class BoseCspSoIP : Dsp
{
    public static readonly ushort DefaultPort = 10055;
    private const double DefaultMinGain = -60.5;
    private const double DefaultMaxGain = 12.0;
    private readonly Dictionary<string, BoseGain> _gains = new();
    private readonly Dictionary<string, BoseMute> _mutes = new();
    private readonly Dictionary<string, BoseSelect> _selects = new();
    private readonly object _controlsLock = new();
    private readonly Regex _responseParser;
    
    public BoseCspSoIP(TcpClient tcpClient, string name = "Bose", int pollIntervalInMilliseconds = 50000) : base(name, tcpClient, pollIntervalInMilliseconds)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        
        string responsePattern = "GA\\\"([^\\\"]+)\\\"\\>(\\d+)=([a-zA-Z0-9.-]+)";
        _responseParser = new(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(250));
        
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        using (PushProperties("HandleConnectionState"))
        {
            AddEvent(EventType.Connection, connectionState.ToString());
            if (connectionState == ConnectionState.Connected)
                _ = Task.Run(QueryEveryControlAfterConnecting);
            else
                CommunicationState = CommunicationClient.DesiredConnectionState == ConnectionState.Disconnected
                    ? CommunicationState.NotAttempted
                    : CommunicationState.Unknown;
        }
    }

    private async Task QueryEveryControlAfterConnecting()
    {
        try
        {
            await Poll(CancellationToken.None);
        }
        catch (Exception e)
        {
            LogException(e, "Querying the controls after connecting failed");
        }
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (var line in response.Split('\r'))
                ProcessLine(line);
        }
    }

    private void ProcessLine(string line)
    {
        var match = _responseParser.Match(line);
        if (!match.Success)
            return;
        CommunicationState = CommunicationState.Okay;

        var controlName = match.Groups[1].Value;
        var attribute = match.Groups[2].Value;
        var value = match.Groups[3].Value;

        // The device echoes the full control name ("Foo Gain", "Foo Selector") but the
        // dictionaries are keyed by the base name consumers registered with.
        if (TryStripSuffix(controlName, " Gain", out var gainKey))
            ProcessGainResponse(gainKey, attribute, value);
        else if (TryStripSuffix(controlName, " Selector", out var selectKey)
                 && attribute == "1" && FindControl(_selects, selectKey) is { } select)
            select.Value = value;
    }

    private void ProcessGainResponse(string gainKey, string attribute, string value)
    {
        if (attribute == "1" && FindControl(_gains, gainKey) is { } gain)
            gain.SetVolumeFromDb(double.Parse(value));
        else if (attribute == "2" && FindControl(_mutes, gainKey) is { } mute)
            mute.MuteState = value switch
            {
                "O" => MuteState.On,
                "F" => MuteState.Off,
                _ => MuteState.Unknown
            };
    }

    private static bool TryStripSuffix(string name, string suffix, out string baseName)
    {
        if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
        {
            baseName = name[..^suffix.Length];
            return true;
        }
        baseName = string.Empty;
        return false;
    }

    protected override async Task Poll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        foreach (string key in ControlNames(_gains))
        {
            await Task.Delay(30, token);
            CommunicationClient.Send($"GA\"{key} Gain\">1\r");
        }
        foreach (string key in ControlNames(_mutes))
        {
            await Task.Delay(30, token);
            CommunicationClient.Send($"GA\"{key} Gain\">2\r");
        }

        foreach (string key in ControlNames(_selects))
        {
            await Task.Delay(30, token);
            CommunicationClient.Send($"GA\"{key} Selector\">1\r");
        }
    }

    private List<string> ControlNames<T>(Dictionary<string, T> controls)
    {
        lock (_controlsLock)
            return controls.Keys.ToList();
    }

    private T? FindControl<T>(Dictionary<string, T> controls, string controlName) where T : class
    {
        lock (_controlsLock)
            return controls.GetValueOrDefault(controlName);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName)
    {
        BoseGain? gain;
        lock (_controlsLock)
        {
            if (_gains.TryGetValue(controlName, out gain))
                gain.VolumeLevelHandlers += volumeLevelHandler;
            else
                _gains.Add(controlName, new BoseGain(volumeLevelHandler, controlName, DefaultMinGain, DefaultMaxGain));
        }
        if (gain != null)
        {
            volumeLevelHandler.Invoke(gain.Volume);
            return;
        }
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            CommunicationClient.Send($"GA\"{controlName} Gain\">1\r");
    }

    public void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName, double minGain, double maxGain)
    {
        BoseGain? gain;
        lock (_controlsLock)
        {
            if (_gains.TryGetValue(controlName, out gain))
                gain.VolumeLevelHandlers += volumeLevelHandler;
            else
                _gains.Add(controlName, new BoseGain(volumeLevelHandler, controlName, minGain, maxGain));
        }
        if (gain != null)
        {
            gain.SetMinGain(minGain);
            gain.SetMaxGain(maxGain);
            volumeLevelHandler.Invoke(gain.Volume);
            return;
        }
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            CommunicationClient.Send($"GA\"{controlName} Gain\">1\r");
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string muteName)
    {
        BoseMute? mute;
        lock (_controlsLock)
        {
            if (_mutes.TryGetValue(muteName, out mute))
                mute.MuteStateHandlers += muteStateHandler;
            else
                _mutes.Add(muteName, new BoseMute(muteStateHandler, muteName));
        }
        if (mute != null)
        {
            muteStateHandler.Invoke(mute.MuteState);
            return;
        }
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            CommunicationClient.Send($"GA\"{muteName} Gain\">2\r");
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        BoseSelect? select;
        lock (_controlsLock)
        {
            if (_selects.TryGetValue(controlName, out select))
                select.StringValueHandlers += stringValueHandler;
            else
                _selects.Add(controlName, new BoseSelect(stringValueHandler, controlName));
        }
        if (select != null)
        {
            stringValueHandler.Invoke(select.Value);
            return;
        }
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            CommunicationClient.Send($"GA\"{controlName} Selector\">1\r");
    }

    public override void SetLevel(string controlName, int percentage)
    {
        var gain = FindControl(_gains, controlName);
        if (gain == null)
        {
            using (PushProperties("SetLevel"))
                LogWarning("Unknown level {ControlName}, not setting it to {Percentage}", controlName, percentage);
            return;
        }
        CommunicationClient.Send($"SA\"{controlName} Gain\">1={gain.PercentageToDb(percentage)}\r");
    }

    public override void LevelUp(string controlName, int amount = 1) 
        => CommunicationClient.Send($"SA\"{controlName} Gain\">3={amount}\r");

    public override void LevelDown(string controlName, int amount = 1) 
        => CommunicationClient.Send($"SA\"{controlName} Gain\">3={-amount}\r");

    public override void SetAudioMute(string controlName, MuteState muteState)
    {
        string muteCommand = muteState switch
        {
            MuteState.On => "O",
            _ => "F",
        };
        CommunicationClient.Send($"SA\"{controlName} Gain\">2={muteCommand}\r");
    }

    public override void ToggleAudioMute(string controlName)
        => CommunicationClient.Send($"SA\"{controlName} Gain\">2=T\r");

    public override void SetValue(string controlName, string value)
        => CommunicationClient.Send($"SA\"{controlName} Selector\">1={value}\r");

    public override int GetLevel(string controlName)
    {
        var gain = FindControl(_gains, controlName);
        if (gain == null)
        {
            using (PushProperties("GetLevel"))
                LogWarning("Unknown level {ControlName}, returning 0", controlName);
            return 0;
        }
        return gain.Volume;
    }

    public override MuteState GetAudioMute(string controlName)
    {
        var mute = FindControl(_mutes, controlName);
        if (mute == null)
        {
            using (PushProperties("GetAudioMute"))
                LogWarning("Unknown mute {ControlName}, returning Unknown", controlName);
            return MuteState.Unknown;
        }
        return mute.MuteState;
    }

    public override string GetValue(string controlName)
    {
        var select = FindControl(_selects, controlName);
        if (select == null)
        {
            using (PushProperties("GetValue"))
                LogWarning("Unknown selector {ControlName}, returning an empty value", controlName);
            return string.Empty;
        }
        return select.Value;
    }

    public override Task Reinitialise(CancellationToken token = default)
    {
        using (PushProperties("Reinitialise"))
            LogVerbose("This module doesn't pull data from the DSP");
        return Task.CompletedTask;
    }
}