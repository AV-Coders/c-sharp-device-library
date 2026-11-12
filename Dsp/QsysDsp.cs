using AVCoders.Core;

namespace AVCoders.Dsp;

public abstract class QsysDsp : Dsp
{
    public const int MaxUnansweredStatusRequests = 3;
    public const string BadControlIssuePrefix = "bad-control:";
    public const string UnansweredStatusIssueKey = "unanswered-status";
    public const string CoreStandbyIssueKey = "core-standby";
    public const string LoginIssueKey = "login";

    private protected const int EscalateAfterRepeats = 3;
    private protected const string LevelControlType = "level";
    private protected const string MuteControlType = "mute";
    private protected const string StringControlType = "string";
    private const string MissingControlsLabel = "Missing Controls";
    private const string UnansweredStatusMessage = "The Core did not answer the status request";

    private protected enum ControlKind
    {
        Level,
        Mute,
        Text
    }

    private protected readonly Dictionary<string, QscGain> Gains = new();
    private protected readonly Dictionary<string, QscMute> Mutes = new();
    private protected readonly Dictionary<string, QscInt> Strings = new();
    private protected readonly object ControlsLock = new();
    private readonly HashSet<string> _missingControls = new();
    private readonly TcpClient _tcpClient;
    private int _unansweredStatusRequests;

    private protected QsysDsp(string name, TcpClient tcpClient, int pollIntervalInMilliseconds, TimeProvider? timeProvider)
        : base(name, tcpClient, pollIntervalInMilliseconds, timeProvider)
    {
        _tcpClient = tcpClient;
    }

    private protected abstract bool AcceptsControlName(string controlName, string controlType);

    private protected abstract void ControlAdded(string controlName, ControlKind kind, bool isNew);

    private protected abstract void SendStatusRequest();

    private protected virtual string UnansweredStatusDetail() => "";

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName)
    {
        if (!AcceptsControlName(controlName, LevelControlType))
            return;

        QscGain? gain;
        lock (ControlsLock)
        {
            if (Gains.TryGetValue(controlName, out gain))
                gain.VolumeLevelHandlers += volumeLevelHandler;
            else
                Gains.Add(controlName, new QscGain(volumeLevelHandler));
        }
        if (gain != null)
            volumeLevelHandler.Invoke(gain.Volume);
        ControlAdded(controlName, ControlKind.Level, gain == null);
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string controlName)
    {
        if (!AcceptsControlName(controlName, MuteControlType))
            return;

        QscMute? mute;
        lock (ControlsLock)
        {
            if (Mutes.TryGetValue(controlName, out mute))
                mute.MuteStateHandlers += muteStateHandler;
            else
                Mutes.Add(controlName, new QscMute(muteStateHandler));
        }
        if (mute != null)
            muteStateHandler.Invoke(mute.MuteState);
        ControlAdded(controlName, ControlKind.Mute, mute == null);
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (!AcceptsControlName(controlName, StringControlType))
            return;

        QscInt? block;
        lock (ControlsLock)
        {
            if (Strings.TryGetValue(controlName, out block))
                block.StringValueHandlers += stringValueHandler;
            else
                Strings.Add(controlName, new QscInt(stringValueHandler));
        }
        if (block != null)
            stringValueHandler.Invoke(block.Value);
        ControlAdded(controlName, ControlKind.Text, block == null);
    }

    public override void LevelUp(string controlName, int amount = 1)
    {
        var gain = FindControl(Gains, controlName);
        if (gain == null)
        {
            using (PushProperties("LevelUp"))
                LogWarning("Unknown level {ControlName}, not raising it by {Amount}", controlName, amount);
            return;
        }
        SetLevel(controlName, gain.Volume + amount);
    }

    public override void LevelDown(string controlName, int amount = 1)
    {
        var gain = FindControl(Gains, controlName);
        if (gain == null)
        {
            using (PushProperties("LevelDown"))
                LogWarning("Unknown level {ControlName}, not lowering it by {Amount}", controlName, amount);
            return;
        }
        SetLevel(controlName, gain.Volume - amount);
    }

    public override int GetLevel(string gainName)
    {
        var gain = FindControl(Gains, gainName);
        if (gain == null)
        {
            using (PushProperties("GetLevel"))
                LogWarning("Unknown level {ControlName}, returning 0", gainName);
            return 0;
        }
        return gain.Volume;
    }

    public override void ToggleAudioMute(string muteName)
    {
        var mute = FindControl(Mutes, muteName);
        if (mute == null)
        {
            using (PushProperties("ToggleAudioMute"))
                LogWarning("Unknown mute {ControlName}, not toggling it", muteName);
            return;
        }
        switch (mute.MuteState)
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
        var mute = FindControl(Mutes, muteName);
        if (mute == null)
        {
            using (PushProperties("GetAudioMute"))
                LogWarning("Unknown mute {ControlName}, returning Unknown", muteName);
            return MuteState.Unknown;
        }
        return mute.MuteState;
    }

    public override string GetValue(string intName)
    {
        var block = FindControl(Strings, intName);
        if (block == null)
        {
            using (PushProperties("GetValue"))
                LogWarning("Unknown string {ControlName}, returning an empty value", intName);
            return "";
        }
        return block.Value;
    }

    private protected T? FindControl<T>(Dictionary<string, T> controls, string controlName) where T : class
    {
        lock (ControlsLock)
            return controls.GetValueOrDefault(controlName);
    }

    private protected void ShowBadControl(string controlName, string issueKey, string message)
    {
        RaiseOngoingIssue(issueKey, message);
        bool added;
        lock (ControlsLock)
            added = _missingControls.Add(controlName);
        if (added)
            ShowMissingControls();
    }

    private protected void ControlReported(string controlName)
    {
        ResolveIssue($"{BadControlIssuePrefix}{controlName}");
        bool wasMissing;
        lock (ControlsLock)
            wasMissing = _missingControls.Remove(controlName);
        if (wasMissing)
            ShowMissingControls();
    }

    private void ShowMissingControls()
    {
        string missingControls;
        lock (ControlsLock)
            missingControls = string.Join(", ", _missingControls.Order().Select(ShownControlName));
        if (missingControls.Length == 0)
            RemoveDetail(MissingControlsLabel);
        else
            SetDetail(MissingControlsLabel, missingControls, DetailTone.Warning);
    }

    private static string ShownControlName(string controlName) =>
        string.IsNullOrWhiteSpace(controlName) ? $"\"{controlName}\"" : controlName;

    private protected void RequestStatus()
    {
        int unanswered = _unansweredStatusRequests;
        if (unanswered > 0)
            RaiseMomentaryIssue(UnansweredStatusMessage + UnansweredStatusDetail(), UnansweredStatusIssueKey,
                IssueSeverity.Minor, EscalateAfterRepeats);

        if (unanswered >= MaxUnansweredStatusRequests)
        {
            ReconnectAfterUnansweredStatus();
            return;
        }

        Interlocked.Increment(ref _unansweredStatusRequests);
        SendStatusRequest();
    }

    private void ReconnectAfterUnansweredStatus()
    {
        using (PushProperties("ReconnectAfterUnansweredStatus"))
        {
            LogWarning("The Core has not answered {Count} status requests, reconnecting", MaxUnansweredStatusRequests);
            CommunicationState = CommunicationState.Error;
            Interlocked.Exchange(ref _unansweredStatusRequests, 0);
            _tcpClient.Reconnect();
        }
    }

    private protected void StatusAnswered()
    {
        Interlocked.Exchange(ref _unansweredStatusRequests, 0);
        ResolveIssue(UnansweredStatusIssueKey);
    }

    private protected void ResetUnansweredStatus() => Interlocked.Exchange(ref _unansweredStatusRequests, 0);

    private protected void ShowNotConnected() =>
        CommunicationState = CommunicationClient.DesiredConnectionState == ConnectionState.Disconnected
            ? CommunicationState.NotAttempted
            : CommunicationState.Unknown;
}
