using AVCoders.Core;

namespace AVCoders.Dsp;

public abstract class QsysDsp : Dsp
{
    public const int MaxUnansweredStatusRequests = 3;
    public const string UnansweredStatusIssueKey = "unanswered-status";
    public const string CoreStandbyIssueKey = "core-standby";
    public const string LoginIssueKey = "login";

    private protected const int EscalateAfterRepeats = 3;
    private const string UnansweredStatusMessage = "The Core did not answer the status request";

    private protected enum ControlKind
    {
        Level,
        Mute,
        Text
    }

    private protected readonly NamedControls Controls;
    private protected readonly TcpClient CoreClient;
    private readonly StatusRequests _statusRequests;
    private int _blankControlNames;

    private protected QsysDsp(string name, TcpClient tcpClient, int pollIntervalInMilliseconds, TimeProvider? timeProvider)
        : base(name, tcpClient, pollIntervalInMilliseconds, timeProvider)
    {
        CoreClient = tcpClient;
        Controls = new NamedControls(PushProperties, LogWarning, (key, message) => RaiseOngoingIssue(key, message), ResolveIssue, SetDetail, RemoveDetail);
        _statusRequests = new StatusRequests(MaxUnansweredStatusRequests, SendStatusRequest, ReportUnansweredStatus, ReconnectAfterUnansweredStatus);
    }

    public abstract void RecallSnapshot(string name, int snapshot);

    private protected abstract bool AcceptsControlName(string controlName, string controlType);

    private protected abstract void ControlAdded(string controlName, ControlKind kind, bool isNew);

    private protected abstract void SendStatusRequest();

    private protected virtual string UnansweredStatusDetail() => "";

    public override void AddControl(VolumeLevelHandler volumeLevelHandler, string controlName)
    {
        if (!AcceptsControlName(controlName, LevelControlType))
            return;
        ControlAdded(controlName, ControlKind.Level, Controls.AddLevel(controlName, volumeLevelHandler, handler => new QscGain(handler)));
    }

    public override void AddControl(MuteStateHandler muteStateHandler, string controlName)
    {
        if (!AcceptsControlName(controlName, MuteControlType))
            return;
        ControlAdded(controlName, ControlKind.Mute, Controls.AddMute(controlName, muteStateHandler, handler => new QscMute(handler)));
    }

    public override void AddControl(StringValueHandler stringValueHandler, string controlName)
    {
        if (!AcceptsControlName(controlName, StringControlType))
            return;
        ControlAdded(controlName, ControlKind.Text, Controls.AddString(controlName, stringValueHandler, handler => new QscInt(handler)));
    }

    public override void LevelUp(string controlName, int amount = 1) => Controls.LevelUp(controlName, amount, SetLevel);

    public override void LevelDown(string controlName, int amount = 1) => Controls.LevelDown(controlName, amount, SetLevel);

    public override int GetLevel(string gainName) => Controls.GetLevel(gainName);

    public override void ToggleAudioMute(string muteName) => Controls.ToggleMute(muteName, SetAudioMute);

    public override MuteState GetAudioMute(string muteName) => Controls.GetMute(muteName);

    public override string GetValue(string intName) => Controls.GetString(intName);

    private protected void ShowBlankControlName(string controlName, string controlType)
    {
        int order = Interlocked.Increment(ref _blankControlNames);
        using (PushProperties("ShowBlankControlName"))
            Controls.ShowBadControl(controlName, $"{BadControlIssuePrefix}\"{controlName}\" {controlType} {order}",
                $"The {controlType} control name \"{controlName}\" is blank, so it is not sent to the Core (blank control {order})");
    }

    private protected void RequestStatus() => _statusRequests.Request();

    private void ReportUnansweredStatus() =>
        RaiseMomentaryIssue(UnansweredStatusMessage + UnansweredStatusDetail(), UnansweredStatusIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);

    private void ReconnectAfterUnansweredStatus()
    {
        using (PushProperties("ReconnectAfterUnansweredStatus"))
        {
            LogWarning("The Core has not answered {Count} status requests, reconnecting", MaxUnansweredStatusRequests);
            CommunicationState = CommunicationState.Error;
            CoreClient.Reconnect();
        }
    }

    private protected void StatusAnswered()
    {
        _statusRequests.Answered();
        ResolveIssue(UnansweredStatusIssueKey);
    }

    private protected void ResetUnansweredStatus() => _statusRequests.Answered();
}
