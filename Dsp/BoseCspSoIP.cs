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
    private const int MaxAwaitingReplies = 100;
    private const char Ack = '\u0006';
    private const char Nak = '\u0015';
    private const string AckGroup = "ack";
    private const string NakGroup = "nak";
    private const string NakCodeGroup = "code";
    private const string ModuleGroup = "module";
    private const string IndexGroup = "index";
    private const string ValueGroup = "value";
    private const string InvalidModuleName = "01";
    private const string IllegalIndex = "02";
    private const string OutOfRange = "03";
    private const string UnknownError = "99";
    private const string RejectedIssuePrefix = "nak:";
    private const string UntrackedNakIssueKey = "untracked-nak";
    private static readonly TimeSpan QueryGap = TimeSpan.FromMilliseconds(30);
    private static readonly Regex ReplyParser = new(
        $"(?<{AckGroup}>{Ack})|(?<{NakGroup}>{Nak} ?(?<{NakCodeGroup}>\\d*))|GA\"(?<{ModuleGroup}>[^\"]+)\">(?<{IndexGroup}>\\d+)=(?<{ValueGroup}>[a-zA-Z0-9.-]+)",
        RegexOptions.None, TimeSpan.FromMilliseconds(250));
    private readonly Dictionary<string, BoseGain> _gains = new();
    private readonly Dictionary<string, BoseMute> _mutes = new();
    private readonly Dictionary<string, BoseSelect> _selects = new();
    private readonly object _controlsLock = new();
    private readonly List<SentCommand> _awaitingReply = [];
    private readonly object _awaitingReplyLock = new();

    private record SentCommand(string Module, string Command);

    public BoseCspSoIP(TcpClient tcpClient, string name = "Bose", int pollIntervalInMilliseconds = 50000)
        : this(tcpClient, name, pollIntervalInMilliseconds, null)
    {
    }

    internal BoseCspSoIP(TcpClient tcpClient, string name, int pollIntervalInMilliseconds, TimeProvider? timeProvider)
        : base(name, tcpClient, pollIntervalInMilliseconds, timeProvider)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        using (PushProperties("HandleConnectionState"))
        {
            AddEvent(EventType.Connection, connectionState.ToString());
            if (connectionState == ConnectionState.Connected)
            {
                _ = Task.Run(QueryEveryControlAfterConnecting);
                return;
            }
            lock (_awaitingReplyLock)
                _awaitingReply.Clear();
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
            foreach (Match reply in ReplyParser.Matches(response))
                ProcessReply(reply);
        }
    }

    private void ProcessReply(Match reply)
    {
        CommunicationState = CommunicationState.Okay;
        if (reply.Groups[AckGroup].Success)
            HandleAck();
        else if (reply.Groups[NakGroup].Success)
            HandleNak(reply.Groups[NakCodeGroup].Value);
        else
            HandleQueryReply(reply.Groups[ModuleGroup].Value, reply.Groups[IndexGroup].Value, reply.Groups[ValueGroup].Value);
    }

    private void HandleAck()
    {
        if (TakeNextCommand() is { } command)
            ResolveIssue($"{RejectedIssuePrefix}{command.Module}");
    }

    private void HandleNak(string code)
    {
        using (PushProperties("HandleNak"))
        {
            if (TakeNextCommand() is not { } command)
            {
                RaiseMomentaryIssue($"The CSP answered NAK {code} ({NakMeaning(code)}) when no command was awaiting a reply",
                    UntrackedNakIssueKey);
                return;
            }
            RaiseOngoingIssue($"{RejectedIssuePrefix}{command.Module}",
                $"The CSP rejected {command.Command.TrimEnd('\r')} with NAK {code}: {NakMeaning(code)}", NakSeverity(code));
        }
    }

    private static string NakMeaning(string code) => code switch
    {
        InvalidModuleName => "no module has that name, or the name is a duplicate",
        IllegalIndex => "the index is not valid for that module",
        OutOfRange => "the value is out of range",
        UnknownError => "unknown error",
        _ => "a code the CSP serial control guide does not list"
    };

    private static IssueSeverity NakSeverity(string code) => code switch
    {
        InvalidModuleName or IllegalIndex => IssueSeverity.Major,
        _ => IssueSeverity.Minor
    };

    private void HandleQueryReply(string module, string attribute, string value)
    {
        TakeQueryCommand($"GA\"{module}\">{attribute}\r");
        ResolveIssue($"{RejectedIssuePrefix}{module}");

        // The device echoes the full control name ("Foo Gain", "Foo Selector") but the
        // dictionaries are keyed by the base name consumers registered with.
        if (TryStripSuffix(module, " Gain", out var gainKey))
            ProcessGainResponse(gainKey, attribute, value);
        else if (TryStripSuffix(module, " Selector", out var selectKey)
                 && attribute == "1" && FindControl(_selects, selectKey) is { } select)
            select.Value = value;
    }

    private void QueryParameter(string module, int index) => Send(module, $"GA\"{module}\">{index}\r");

    private void SetParameter(string module, int index, string value) => Send(module, $"SA\"{module}\">{index}={value}\r");

    private void Send(string module, string command)
    {
        lock (_awaitingReplyLock)
        {
            _awaitingReply.Add(new SentCommand(module, command));
            if (_awaitingReply.Count > MaxAwaitingReplies)
                _awaitingReply.RemoveAt(0);
            CommunicationClient.Send(command);
        }
    }

    private SentCommand? TakeNextCommand()
    {
        lock (_awaitingReplyLock)
        {
            if (_awaitingReply.Count == 0)
                return null;
            var command = _awaitingReply[0];
            _awaitingReply.RemoveAt(0);
            return command;
        }
    }

    private void TakeQueryCommand(string command)
    {
        List<SentCommand> unanswered;
        lock (_awaitingReplyLock)
        {
            var index = _awaitingReply.FindIndex(sent => sent.Command == command);
            if (index < 0)
                return;
            unanswered = _awaitingReply.GetRange(0, index);
            _awaitingReply.RemoveRange(0, index + 1);
        }
        if (unanswered.Count > 0)
            using (PushProperties("TakeQueryCommand"))
                LogWarning("The CSP did not answer {Commands}", string.Join(", ", unanswered.Select(sent => sent.Command.TrimEnd('\r'))));
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
            await Task.Delay(QueryGap, TimeProvider, token);
            QueryParameter($"{key} Gain", 1);
        }
        foreach (string key in ControlNames(_mutes))
        {
            await Task.Delay(QueryGap, TimeProvider, token);
            QueryParameter($"{key} Gain", 2);
        }

        foreach (string key in ControlNames(_selects))
        {
            await Task.Delay(QueryGap, TimeProvider, token);
            QueryParameter($"{key} Selector", 1);
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
            QueryParameter($"{controlName} Gain", 1);
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
            QueryParameter($"{controlName} Gain", 1);
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
            QueryParameter($"{muteName} Gain", 2);
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
            QueryParameter($"{controlName} Selector", 1);
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
        SetParameter($"{controlName} Gain", 1, $"{gain.PercentageToDb(percentage)}");
    }

    public override void LevelUp(string controlName, int amount = 1)
        => SetParameter($"{controlName} Gain", 3, $"{amount}");

    public override void LevelDown(string controlName, int amount = 1)
        => SetParameter($"{controlName} Gain", 3, $"{-amount}");

    public override void SetAudioMute(string controlName, MuteState muteState)
    {
        string muteCommand = muteState switch
        {
            MuteState.On => "O",
            _ => "F",
        };
        SetParameter($"{controlName} Gain", 2, muteCommand);
    }

    public override void ToggleAudioMute(string controlName)
        => SetParameter($"{controlName} Gain", 2, "T");

    public override void SetValue(string controlName, string value)
        => SetParameter($"{controlName} Selector", 1, value);

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