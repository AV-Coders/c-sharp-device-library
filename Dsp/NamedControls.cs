using AVCoders.Core;

namespace AVCoders.Dsp;

internal sealed class NamedControls(
    Func<string, IDisposable> scope,
    Action<string, object?[]> warn,
    Action<string, string> raiseIssue,
    Action<string> resolveIssue,
    Action<string, string, DetailTone> setDetail,
    Action<string> removeDetail)
{
    private readonly Dictionary<string, Fader> _levels = new();
    private readonly Dictionary<string, Mute> _mutes = new();
    private readonly Dictionary<string, StringValue> _strings = new();
    private readonly HashSet<string> _missing = new();
    private readonly object _lock = new();

    public bool AddLevel(string name, VolumeLevelHandler handler, Func<VolumeLevelHandler, Fader> create)
    {
        Fader? level;
        lock (_lock)
        {
            if (_levels.TryGetValue(name, out level))
                level.VolumeLevelHandlers += handler;
            else
                _levels.Add(name, create(handler));
        }
        if (level == null)
            return true;
        handler.Invoke(level.Volume);
        return false;
    }

    public bool AddMute(string name, MuteStateHandler handler, Func<MuteStateHandler, Mute> create)
    {
        Mute? mute;
        lock (_lock)
        {
            if (_mutes.TryGetValue(name, out mute))
                mute.MuteStateHandlers += handler;
            else
                _mutes.Add(name, create(handler));
        }
        if (mute == null)
            return true;
        handler.Invoke(mute.MuteState);
        return false;
    }

    public bool AddString(string name, StringValueHandler handler, Func<StringValueHandler, StringValue> create)
    {
        StringValue? value;
        lock (_lock)
        {
            if (_strings.TryGetValue(name, out value))
                value.StringValueHandlers += handler;
            else
                _strings.Add(name, create(handler));
        }
        if (value == null)
            return true;
        handler.Invoke(value.Value);
        return false;
    }

    public Fader? FindLevel(string name)
    {
        lock (_lock)
            return _levels.GetValueOrDefault(name);
    }

    public Mute? FindMute(string name)
    {
        lock (_lock)
            return _mutes.GetValueOrDefault(name);
    }

    public StringValue? FindString(string name)
    {
        lock (_lock)
            return _strings.GetValueOrDefault(name);
    }

    public List<string> LevelNames()
    {
        lock (_lock)
            return _levels.Keys.ToList();
    }

    public List<string> MuteNames()
    {
        lock (_lock)
            return _mutes.Keys.ToList();
    }

    public List<string> StringNames()
    {
        lock (_lock)
            return _strings.Keys.ToList();
    }

    public List<string> Names()
    {
        lock (_lock)
            return [.._levels.Keys, .._mutes.Keys, .._strings.Keys];
    }

    public void LevelUp(string name, int amount, Action<string, int> setLevel)
    {
        var level = FindLevel(name);
        if (level == null)
        {
            using (scope("LevelUp"))
                warn("Unknown level {ControlName}, not raising it by {Amount}", [name, amount]);
            return;
        }
        setLevel(name, level.Volume + amount);
    }

    public void LevelDown(string name, int amount, Action<string, int> setLevel)
    {
        var level = FindLevel(name);
        if (level == null)
        {
            using (scope("LevelDown"))
                warn("Unknown level {ControlName}, not lowering it by {Amount}", [name, amount]);
            return;
        }
        setLevel(name, level.Volume - amount);
    }

    public int GetLevel(string name)
    {
        var level = FindLevel(name);
        if (level == null)
        {
            using (scope("GetLevel"))
                warn("Unknown level {ControlName}, returning 0", [name]);
            return 0;
        }
        return level.Volume;
    }

    public void ToggleMute(string name, Action<string, MuteState> setMute)
    {
        var mute = FindMute(name);
        if (mute == null)
        {
            using (scope("ToggleAudioMute"))
                warn("Unknown mute {ControlName}, not toggling it", [name]);
            return;
        }
        setMute(name, mute.MuteState == MuteState.On ? MuteState.Off : MuteState.On);
    }

    public MuteState GetMute(string name)
    {
        var mute = FindMute(name);
        if (mute == null)
        {
            using (scope("GetAudioMute"))
                warn("Unknown mute {ControlName}, returning Unknown", [name]);
            return MuteState.Unknown;
        }
        return mute.MuteState;
    }

    public string GetString(string name)
    {
        var value = FindString(name);
        if (value == null)
        {
            using (scope("GetValue"))
                warn("Unknown string {ControlName}, returning an empty value", [name]);
            return "";
        }
        return value.Value;
    }

    public void ShowBadControl(string name, string issueKey, string message)
    {
        raiseIssue(issueKey, message);
        bool added;
        lock (_lock)
            added = _missing.Add(name);
        if (added)
            ShowMissingControls();
    }

    public void ControlReported(string name)
    {
        resolveIssue($"{Dsp.BadControlIssuePrefix}{name}");
        bool wasMissing;
        lock (_lock)
            wasMissing = _missing.Remove(name);
        if (wasMissing)
            ShowMissingControls();
    }

    private void ShowMissingControls()
    {
        string missing;
        lock (_lock)
            missing = string.Join(", ", _missing.Order().Select(ShownName));
        if (missing.Length == 0)
            removeDetail(Dsp.MissingControlsLabel);
        else
            setDetail(Dsp.MissingControlsLabel, missing, DetailTone.Warning);
    }

    private static string ShownName(string name) => string.IsNullOrWhiteSpace(name) ? $"\"{name}\"" : name;
}
