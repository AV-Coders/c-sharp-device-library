using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVCoders.Core;

namespace AVCoders.Dsp;

public class QsysQrc : QsysDsp
{
    public const ushort DefaultPort = 1710;
    public const int DefaultPollIntervalInMilliseconds = 15000;
    public const string ChangeGroupId = "AVCoders";
    public const double AutoPollRateSeconds = 0.5;
    public const int UnknownComponentErrorCode = 7;
    public const int UnknownControlErrorCode = 8;
    public const int LogonRequiredErrorCode = 10;
    public const int MaxLogonAttempts = 2;
    public const string RejectedIssueKey = "rejected-request";
    public const string UnreadableReplyIssueKey = "unreadable-reply";
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private const byte Delimiter = 0;
    private const char ComponentSeparator = '.';
    private const int PositionDecimals = 2;
    private const double PercentScale = 100;
    private const int MaxUnframedBytes = 8 * 1024 * 1024;
    private const int ShownUnframedBytes = 200;
    private const string MaskedPassword = "***";
    private const string ExpiredWhileHeld = "Expired while held for";
    private const string HeldForTheLogon = "the logon";
    private const string HeldForTheComponentList = "the Core's component list";
    private const string JsonRpcVersion = "2.0";

    private const string LogonMethod = "Logon";
    private const string StatusGetMethod = "StatusGet";
    private const string EngineStatusMethod = "EngineStatus";
    private const string ComponentSetMethod = "Component.Set";
    private const string GetComponentsMethod = "Component.GetComponents";
    private const string AddComponentControlMethod = "ChangeGroup.AddComponentControl";
    private const string AutoPollMethod = "ChangeGroup.AutoPoll";
    private const string InvalidateMethod = "ChangeGroup.Invalidate";
    private const string PollMethod = "ChangeGroup.Poll";

    private const string JsonRpcField = "jsonrpc";
    private const string IdField = "id";
    private const string MethodField = "method";
    private const string ParamsField = "params";
    private const string ResultField = "result";
    private const string ErrorField = "error";
    private const string CodeField = "code";
    private const string NameField = "Name";
    private const string ControlsField = "Controls";
    private const string PositionField = "Position";
    private const string ValueField = "Value";
    private const string StringField = "String";
    private const string ComponentField = "Component";
    private const string ChangesField = "Changes";
    private const string ChangeGroupIdField = "Id";
    private const string RateField = "Rate";
    private const string UserField = "User";
    private const string PasswordField = "Password";
    private const string StateField = "State";
    private const string DesignNameField = "DesignName";
    private const string DesignCodeField = "DesignCode";

    private const string ActiveState = "Active";
    private const string SnapshotLoadControlPrefix = "load_";
    private const int StatusGetParams = 0;
    private const string GetComponentsParams = "test";
    private const int SnapshotLoadValue = 1;
    private const int MutedValue = 1;
    private const int UnmutedValue = 0;
    private const string DesignLabel = "Design";
    private const string DesignCodeLabel = "Design code";
    private const string CoreLabel = "Core";

    private readonly string _username;
    private readonly string _password;
    private readonly SentCommandTracker<QrcRequest> _sent;
    private readonly Dictionary<int, (QrcRequest Request, long HandedAt)> _unwritten = new();
    private readonly List<QrcSet> _held = [];
    private readonly List<byte> _received = [];
    private readonly object _lock = new();
    private readonly object _sendLock = new();

    private int _nextId;
    private long _nextSequence;
    private bool _loggedIn;
    private int? _logonId;
    private int _logonAttempts;
    private bool _loggingOnStopped;
    private bool _logonFailing;
    private string? _designCode;
    private HashSet<string>? _components;
    private int? _componentListId;
    private string? _componentListProblem;

    private readonly record struct ComponentControl(string Component, string Control);

    private readonly record struct Resolution(ComponentControl? Split, bool AwaitsComponentList);

    private sealed record QrcSet(string ControlName, ComponentControl? Split, JsonObject Fields, string Subject, long Sequence,
        long MadeAt, TimeSpan Lifetime, string? TriggerId)
    {
        public string HeldFor { get; init; } = string.Empty;
    }

    private sealed record QrcRequest(int Id, string Method, JsonNode? Params, string Subject, IReadOnlyList<string> Controls,
        string? TriggerId, ComponentControl? Split = null, QrcSet? Set = null);

    public QsysQrc(TcpClient tcpClient, string name = "Qsys", string username = "", string password = "",
        int pollIntervalInMilliseconds = DefaultPollIntervalInMilliseconds)
        : this(tcpClient, name, username, password, pollIntervalInMilliseconds, null)
    {
    }

    internal QsysQrc(TcpClient tcpClient, string name, string username, string password, int pollIntervalInMilliseconds, TimeProvider? timeProvider)
        : base(name, tcpClient, pollIntervalInMilliseconds, timeProvider)
    {
        _username = username;
        _password = password;
        _sent = new SentCommandTracker<QrcRequest>(ReplyTimeout, TimeProvider);
        if (password.Length > 0)
            tcpClient.MaskInRequests(PasswordText(password), PasswordText(MaskedPassword));
        tcpClient.RequestHandlers += RequestWritten;
        tcpClient.ResponseByteHandlers += HandleResponse;
        tcpClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(tcpClient.ConnectionState);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override void SetLevel(string controlName, int percentage)
    {
        if (!IsComponentControl(controlName))
        {
            using (PushProperties("SetLevel"))
                LogWarning("Not setting {ControlName} to {Percentage}: a QRC control name is <component>.<control>", controlName, percentage);
            return;
        }
        var position = Math.Round(percentage / PercentScale, PositionDecimals);
        SendSet(NewSet(controlName, null, new JsonObject { [PositionField] = position }, controlName));
    }

    public override void SetAudioMute(string controlName, MuteState muteState)
    {
        if (!IsComponentControl(controlName))
        {
            using (PushProperties("SetAudioMute"))
                LogWarning("Not setting {ControlName} to {State}: a QRC control name is <component>.<control>", controlName, muteState);
            return;
        }
        var value = muteState == MuteState.On ? MutedValue : UnmutedValue;
        SendSet(NewSet(controlName, null, new JsonObject { [ValueField] = value }, controlName));
    }

    public override void SetValue(string controlName, string value)
    {
        if (!IsComponentControl(controlName))
        {
            using (PushProperties("SetValue"))
                LogWarning("Not setting {ControlName} to {Value}: a QRC control name is <component>.<control>", controlName, value);
            return;
        }
        SendSet(NewSet(controlName, null, new JsonObject { [ValueField] = value }, controlName));
    }

    public override void RecallSnapshot(string name, int snapshot)
    {
        var load = $"{SnapshotLoadControlPrefix}{snapshot}";
        SendSet(NewSet($"{name}{ComponentSeparator}{load}", new ComponentControl(name, load),
            new JsonObject { [ValueField] = SnapshotLoadValue }, $"{name} snapshot {snapshot}"));
    }

    public override Task Reinitialise(CancellationToken token = default)
    {
        using (PushProperties("Reinitialise"))
        {
            lock (_sendLock)
            {
                lock (_lock)
                    _componentListProblem = null;
                if (IsLoggedIn())
                {
                    TransmitChangeGroup();
                    Transmit(Invalidate());
                }
                else if (ResumeStoppedLogon())
                {
                    LogOn();
                }
            }
        }
        return Task.CompletedTask;
    }

    protected override Task Poll(CancellationToken token)
    {
        using (PushProperties("Poll"))
        {
            ForgetUnwritten();
            ReportMissedReplies();
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return Task.CompletedTask;
            ResumeStoppedLogon();
            RequestStatus();
        }
        return Task.CompletedTask;
    }

    private protected override void SendStatusRequest()
    {
        lock (_sendLock)
            Transmit(Request(StatusGetMethod, JsonValue.Create(StatusGetParams), "the Core's status", []));
    }

    private protected override string UnansweredStatusDetail()
    {
        lock (_lock)
            return _received.Count == 0 ? "" : $" (it sent {_received.Count} bytes with no NUL message end: {Bytes.Escaped(_received, ShownUnframedBytes)})";
    }

    private protected override bool AcceptsControlName(string controlName, string controlType)
    {
        if (string.IsNullOrWhiteSpace(controlName))
        {
            ShowBlankControlName(controlName, controlType);
            return false;
        }
        if (IsComponentControl(controlName))
            return true;
        using (PushProperties("AddControl"))
            Controls.ShowBadControl(controlName, $"{BadControlIssuePrefix}{controlName}",
                $"The {controlType} control name \"{controlName}\" isn't <component>.<control>, so it is not sent to the Core");
        return false;
    }

    private protected override void ControlAdded(string controlName, ControlKind kind, bool isNew)
    {
        if (!isNew)
            return;
        using (PushProperties("ControlAdded"))
        {
            lock (_sendLock)
            {
                if (IsLoggedIn())
                    AddToChangeGroup([controlName]);
            }
        }
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        using (PushProperties("HandleConnectionState"))
        {
            AddEvent(EventType.Connection, connectionState.ToString());
            lock (_lock)
            {
                _loggedIn = false;
                _logonId = null;
                _logonAttempts = 0;
                _loggingOnStopped = false;
                _componentListId = null;
                _componentListProblem = null;
                _received.Clear();
            }
            ResetUnansweredStatus();
            if (connectionState != ConnectionState.Connected)
                ShowNotConnected();
        }
    }

    private void HandleResponse(byte[] response)
    {
        List<string> messages = [];
        string? dropped = null;
        lock (_lock)
        {
            foreach (var received in response)
            {
                if (received != Delimiter)
                {
                    _received.Add(received);
                    continue;
                }
                messages.Add(Encoding.UTF8.GetString(_received.ToArray()));
                _received.Clear();
            }
            if (_received.Count > MaxUnframedBytes)
            {
                dropped = $"Dropped {_received.Count} bytes from the Core with no NUL message end: {Bytes.Escaped(_received, ShownUnframedBytes)}";
                _received.Clear();
            }
        }
        using (PushProperties("HandleResponse"))
        {
            if (dropped != null)
                RaiseMomentaryIssue(dropped, UnreadableReplyIssueKey);
            foreach (var message in messages.Where(message => message.Trim().Length > 0))
                HandleMessage(message);
        }
    }

    private void HandleMessage(string message)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(message);
            MaterialiseEveryObject(root);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            RaiseMomentaryIssue($"Unreadable reply from the Core ({e.Message}): {message}", UnreadableReplyIssueKey);
            return;
        }
        if (root is not JsonObject reply)
        {
            RaiseMomentaryIssue($"Unreadable reply from the Core (not a JSON object): {message}", UnreadableReplyIssueKey);
            return;
        }
        try
        {
            HandleMessage(reply, message);
        }
        catch (Exception e)
        {
            LogException(e, $"Handling a message from the Core threw: {message}");
        }
    }

    private static void MaterialiseEveryObject(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject properties:
                foreach (var property in properties)
                    MaterialiseEveryObject(property.Value);
                return;
            case JsonArray items:
                foreach (var item in items)
                    MaterialiseEveryObject(item);
                return;
        }
    }

    private void HandleMessage(JsonObject reply, string message)
    {
        bool logonFailing;
        lock (_lock)
            logonFailing = _logonFailing;
        if (!logonFailing)
            CommunicationState = CommunicationState.Okay;

        switch (reply[MethodField]?.ToString())
        {
            case EngineStatusMethod:
                HandleCoreStatus(reply[ParamsField] as JsonObject, message, true);
                return;
            case PollMethod:
                if ((reply[ParamsField] as JsonObject)?[ChangesField] is JsonArray changes)
                    ApplyChanges(changes);
                else
                    LogWarning("A change group poll from the Core has no list of changes: {Message}", message);
                return;
            case null:
                HandleReply(reply, message);
                return;
            default:
                LogWarning("Unrecognised notification from the Core: {Message}", message);
                return;
        }
    }

    private void HandleReply(JsonObject reply, string message)
    {
        var request = reply[IdField] is JsonValue idValue && idValue.TryGetValue<int>(out var id) ? Answered(id) : null;
        using (Trigger.Resume(request?.TriggerId))
        using (PushProperties("HandleReply"))
        {
            if (reply[ErrorField] is JsonObject error)
            {
                Audit.RecordOutcome(request?.TriggerId, AuditOutcomeKind.Rejected, Name, message);
                HandleError(request, error, message);
                return;
            }
            if (request != null && request.Method != LogonMethod)
            {
                lock (_lock)
                    _logonAttempts = 0;
            }

            var result = reply[ResultField];
            if (result is JsonObject poll && poll[ChangesField] is JsonArray changes)
            {
                ApplyChanges(changes);
                return;
            }
            switch (request?.Method)
            {
                case LogonMethod:
                    if (result is JsonValue accepted && accepted.GetValueKind() == JsonValueKind.True)
                        LoggedIn();
                    else
                        RefuseLogon($"The Core refused the logon as {_username}: {message}");
                    return;
                case StatusGetMethod:
                    StatusAnswered();
                    HandleCoreStatus(result as JsonObject, message, false);
                    return;
                case ComponentSetMethod:
                    SetAnswered(request, result, message);
                    return;
                case GetComponentsMethod:
                    ComponentListAnswered(request, result, message);
                    return;
                case null:
                    LogDebug("A reply from the Core matches no request awaiting one: {Message}", message);
                    return;
            }
        }
    }

    private void HandleError(QrcRequest? request, JsonObject error, string message)
    {
        int? code = error[CodeField] is JsonValue codeValue && codeValue.TryGetValue<int>(out var number) ? number : null;
        switch (request?.Method)
        {
            case LogonMethod when code == LogonRequiredErrorCode:
                RetryLogon(message);
                return;
            case LogonMethod:
                RefuseLogon($"The Core refused the logon as {_username}: {message}");
                return;
            case not null when code == LogonRequiredErrorCode:
                if (request.Method == StatusGetMethod)
                    StatusAnswered();
                LogOnAgain(request, message);
                return;
            case StatusGetMethod:
                StatusAnswered();
                RaiseMomentaryIssue($"The Core rejected the status request: {message}", RejectedIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);
                return;
            case AddComponentControlMethod:
            case ComponentSetMethod when code is UnknownComponentErrorCode or UnknownControlErrorCode:
                foreach (var controlName in request.Controls)
                    Controls.ShowBadControl(controlName, $"{BadControlIssuePrefix}{controlName}", $"The Core rejected the control {controlName}: {message}");
                return;
            case GetComponentsMethod:
                ComponentListFailed(request, $"The Core rejected {GetComponentsMethod}: {message}");
                return;
            case null:
                RaiseMomentaryIssue($"The Core sent an error for no request it was sent: {message}", RejectedIssueKey, IssueSeverity.Minor, EscalateAfterRepeats);
                return;
            default:
                RaiseMomentaryIssue($"The Core rejected {request.Method} on {request.Subject}: {message}", RejectedIssueKey,
                    IssueSeverity.Minor, EscalateAfterRepeats);
                return;
        }
    }

    private void SetAnswered(QrcRequest request, JsonNode? result, string message)
    {
        if (result is JsonArray unknown && request.Split is { } split && UnknownControlNames(unknown).Contains(split.Control))
        {
            foreach (var controlName in request.Controls)
                Controls.ShowBadControl(controlName, $"{BadControlIssuePrefix}{controlName}", $"The Core doesn't know the control {controlName}: {message}");
            return;
        }
        ResolveIssue(RejectedIssueKey);
        foreach (var controlName in request.Controls)
            Controls.ControlReported(controlName);
    }

    private static HashSet<string> UnknownControlNames(JsonArray unknown) =>
    [
        ..unknown.Select(entry => entry switch
        {
            JsonValue name when name.GetValueKind() == JsonValueKind.String => name.ToString(),
            JsonObject control when control[ValueField] == null => control[NameField]?.ToString(),
            _ => null
        }).OfType<string>()
    ];

    private void HandleCoreStatus(JsonObject? status, string message, bool isEngineStatus)
    {
        if (status == null)
        {
            LogWarning("The Core's status has no details: {Message}", message);
            return;
        }
        var state = status[StateField]?.ToString() ?? "";
        var designCode = status[DesignCodeField]?.ToString() ?? "";
        SetDetail(DesignLabel, status[DesignNameField]?.ToString() ?? "");
        SetDetail(DesignCodeLabel, designCode);
        SetDetail(CoreLabel, state, state == ActiveState ? DetailTone.Normal : DetailTone.Error);
        if (state != ActiveState)
        {
            RaiseOngoingIssue(CoreStandbyIssueKey, $"The Core is {state}, not {ActiveState}, so it ignores commands: {message}", IssueSeverity.Critical);
            return;
        }
        ResolveIssue(CoreStandbyIssueKey);

        bool designChanged;
        lock (_lock)
        {
            designChanged = _designCode != null && _designCode != designCode;
            _designCode = designCode;
            if (designChanged)
            {
                _components = null;
                _componentListId = null;
                _componentListProblem = null;
            }
        }
        if (!IsLoggedIn())
        {
            LogOn();
            return;
        }
        if (!isEngineStatus && !designChanged)
            return;
        AddEvent(EventType.Other, isEngineStatus
            ? "The Core sent its engine status, rebuilding the change group"
            : $"Design redeployed as {designCode}, rebuilding the change group");
        lock (_sendLock)
        {
            if (IsLoggedIn())
                TransmitChangeGroup();
        }
    }

    private void LogOn()
    {
        if (BlankLogin())
        {
            if (!IsLoggedIn())
                LoggedIn();
            return;
        }
        lock (_sendLock)
        {
            QrcRequest logon;
            lock (_lock)
            {
                if (_loggedIn || _logonId != null || _loggingOnStopped)
                    return;
                _logonAttempts++;
                logon = Request(LogonMethod, new JsonObject { [UserField] = _username, [PasswordField] = _password }, _username, []);
                _logonId = logon.Id;
            }
            LogInformation("Logging on to the Core as {Username}", _username);
            Transmit(logon);
        }
    }

    private void RetryLogon(string message)
    {
        bool capped;
        lock (_lock)
        {
            _logonId = null;
            capped = _logonAttempts >= MaxLogonAttempts;
        }
        if (capped)
        {
            RefuseLogon($"The Core refused the logon as {_username} {MaxLogonAttempts} times on this connection: {message}");
            return;
        }
        LogWarning("The Core asked for a logon again: {Message}", message);
        LogOn();
    }

    private void LogOnAgain(QrcRequest request, string message)
    {
        if (BlankLogin())
        {
            RefuseLogon($"The Core needs a logon, and no username or password is set: {message}");
            return;
        }
        if (request.Set != null)
            Hold(request.Set, HeldForTheLogon);
        bool logonInFlight;
        bool capped;
        lock (_lock)
        {
            _loggedIn = false;
            _componentListId = null;
            logonInFlight = _logonId != null;
            capped = _logonAttempts >= MaxLogonAttempts;
        }
        if (logonInFlight)
            return;
        if (capped)
        {
            RefuseLogon($"The Core asked for a logon again after {MaxLogonAttempts} logons on this connection: {message}");
            return;
        }
        LogWarning("The Core asked for a logon again: {Message}", message);
        LogOn();
    }

    private void LogonUnanswered(QrcRequest logon)
    {
        lock (_lock)
        {
            if (_logonId != logon.Id)
                return;
            _logonId = null;
            _logonFailing = true;
        }
        RaiseOngoingIssue(LoginIssueKey,
            $"The Core did not answer the logon as {_username} within {ReplyTimeout.TotalSeconds} s, so the driver is reconnecting: {MaskedLogon(logon.Id)}",
            IssueSeverity.Critical);
        CommunicationState = CommunicationState.Error;
        CoreClient.Reconnect();
    }

    private bool ResumeStoppedLogon()
    {
        lock (_lock)
        {
            if (!_loggingOnStopped)
                return false;
            _loggingOnStopped = false;
            _logonAttempts = 0;
        }
        LogInformation("Trying the logon as {Username} again", _username);
        return true;
    }

    private void RefuseLogon(string message)
    {
        lock (_lock)
        {
            _loggedIn = false;
            _logonId = null;
            _loggingOnStopped = true;
            _logonFailing = true;
        }
        RaiseOngoingIssue(LoginIssueKey, message, IssueSeverity.Critical);
        CommunicationState = CommunicationState.Error;
    }

    private void LoggedIn()
    {
        lock (_sendLock)
        {
            lock (_lock)
            {
                _loggedIn = true;
                _logonId = null;
                _logonFailing = false;
            }
            TransmitChangeGroup();
            ReleaseHeld();
        }
        ResolveIssue(LoginIssueKey);
        CommunicationState = CommunicationState.Okay;
        AddEvent(EventType.Connection, "Logged on");
    }

    private bool IsLoggedIn()
    {
        lock (_lock)
            return _loggedIn;
    }

    private bool BlankLogin() => string.IsNullOrEmpty(_username) && string.IsNullOrEmpty(_password);

    private void ReportMissedReplies()
    {
        foreach (var missed in _sent.TakeMissed().Where(missed => missed.Command.Method != StatusGetMethod))
        {
            using (Trigger.Resume(missed.TriggerId))
            using (PushProperties("ReportMissedReplies"))
            {
                Audit.RecordOutcome(missed.TriggerId, AuditOutcomeKind.NoReply, Name,
                    $"No reply to {missed.Command.Method} on {missed.Command.Subject} within {ReplyTimeout.TotalSeconds} s");
                switch (missed.Command.Method)
                {
                    case LogonMethod:
                        LogonUnanswered(missed.Command);
                        break;
                    case GetComponentsMethod:
                        ComponentListFailed(missed.Command,
                            $"The Core did not answer {Message(missed.Command.Id, missed.Command.Method, missed.Command.Params)} within {ReplyTimeout.TotalSeconds} s");
                        break;
                    default:
                        LogWarning("No reply from the Core to {Method} on {Subject} within {Timeout} s",
                            missed.Command.Method, missed.Command.Subject, ReplyTimeout.TotalSeconds);
                        break;
                }
            }
        }
    }

    private void TransmitChangeGroup()
    {
        AddToChangeGroup(ControlNames());
        Transmit(Request(AutoPollMethod, new JsonObject { [ChangeGroupIdField] = ChangeGroupId, [RateField] = AutoPollRateSeconds },
            ChangeGroupId, []));
    }

    private List<string> ControlNames() => Controls.Names();

    private bool AddToChangeGroup(List<string> controlNames)
    {
        List<(string Name, ComponentControl Split)> resolved = [];
        var awaitsComponentList = false;
        foreach (var controlName in controlNames)
        {
            var resolution = Resolve(controlName);
            if (resolution.Split is { } split)
                resolved.Add((controlName, split));
            else if (resolution.AwaitsComponentList)
                awaitsComponentList = true;
            else
                ShowUnsplit(controlName);
        }
        foreach (var component in resolved.GroupBy(control => control.Split.Component))
            Transmit(AddComponentControls(component.Key, [..component]));
        if (awaitsComponentList)
            RequestComponentList();
        return resolved.Count > 0;
    }

    private QrcRequest AddComponentControls(string component, List<(string Name, ComponentControl Split)> controls)
    {
        var names = new JsonArray([..controls.Select(control => (JsonNode)new JsonObject { [NameField] = control.Split.Control })]);
        var parameters = new JsonObject
        {
            [ChangeGroupIdField] = ChangeGroupId,
            [ComponentField] = new JsonObject { [NameField] = component, [ControlsField] = names }
        };
        return Request(AddComponentControlMethod, parameters, component, [..controls.Select(control => control.Name)]);
    }

    private QrcRequest Invalidate() =>
        Request(InvalidateMethod, new JsonObject { [ChangeGroupIdField] = ChangeGroupId }, ChangeGroupId, []);

    private Resolution Resolve(string controlName)
    {
        var first = controlName.IndexOf(ComponentSeparator);
        if (first == controlName.LastIndexOf(ComponentSeparator))
            return new Resolution(new ComponentControl(controlName[..first], controlName[(first + 1)..]), false);
        HashSet<string>? components;
        bool listFailed;
        lock (_lock)
        {
            components = _components;
            listFailed = _componentListProblem != null;
        }
        if (components == null)
            return new Resolution(null, !listFailed);
        var component = ComponentPrefixes(controlName).FirstOrDefault(components.Contains);
        return new Resolution(component == null ? null : new ComponentControl(component, controlName[(component.Length + 1)..]), false);
    }

    private static IEnumerable<string> ComponentPrefixes(string controlName)
    {
        for (var dot = controlName.LastIndexOf(ComponentSeparator); dot > 0; dot = controlName.LastIndexOf(ComponentSeparator, dot - 1))
            yield return controlName[..dot];
    }

    private static bool IsComponentControl(string controlName)
    {
        var first = controlName.IndexOf(ComponentSeparator);
        return first > 0 && controlName.LastIndexOf(ComponentSeparator) < controlName.Length - 1;
    }

    private static bool HasMoreThanOneDot(string controlName) =>
        controlName.IndexOf(ComponentSeparator) != controlName.LastIndexOf(ComponentSeparator);

    private void ShowUnsplit(string controlName)
    {
        string? problem;
        int listed;
        lock (_lock)
        {
            problem = _componentListProblem;
            listed = _components?.Count ?? 0;
        }
        Controls.ShowBadControl(controlName, $"{BadControlIssuePrefix}{controlName}", problem == null
            ? $"No component the Core lists ({listed} of them) starts the control name \"{controlName}\", so it is not sent to the Core"
            : $"The control name \"{controlName}\" has more than one dot, and with no component list from the Core the driver can't tell its component from its control, so it is not sent to the Core: {problem}");
    }

    private void RequestComponentList()
    {
        QrcRequest request;
        lock (_lock)
        {
            if (!_loggedIn || _componentListId != null || _components != null || _componentListProblem != null)
                return;
            request = Request(GetComponentsMethod, JsonValue.Create(GetComponentsParams), "the design's components", []);
            _componentListId = request.Id;
        }
        Transmit(request);
    }

    private void ComponentListAnswered(QrcRequest request, JsonNode? result, string message)
    {
        if (result is not JsonArray list)
        {
            ComponentListFailed(request, $"The Core's component list is unreadable: {message}");
            return;
        }
        HashSet<string> components = [..list.OfType<JsonObject>().Select(component => component[NameField]?.ToString()).OfType<string>()];
        lock (_lock)
        {
            if (_componentListId != request.Id)
                return;
            _componentListId = null;
            _components = components;
        }
        LogDebug("The Core lists {Count} components: {Components}", components.Count, string.Join(", ", components.Order()));
        lock (_sendLock)
        {
            if (!IsLoggedIn())
                return;
            if (AddToChangeGroup([..ControlNames().Where(HasMoreThanOneDot)]))
                Transmit(Invalidate());
            ReleaseHeld();
        }
    }

    private void ComponentListFailed(QrcRequest request, string problem)
    {
        lock (_lock)
        {
            if (_componentListId != request.Id)
                return;
            _componentListId = null;
            _componentListProblem = problem;
        }
        LogWarning("Control names with more than one dot can't be sent without the Core's component list: {Problem}", problem);
        lock (_sendLock)
        {
            if (!IsLoggedIn())
                return;
            AddToChangeGroup([..ControlNames().Where(HasMoreThanOneDot)]);
            ReleaseHeld();
        }
    }

    private void ApplyChanges(JsonArray changes)
    {
        foreach (var change in changes.OfType<JsonObject>())
        {
            var name = change[NameField]?.ToString() ?? "";
            var component = change[ComponentField]?.ToString();
            var controlName = component == null ? name : $"{component}{ComponentSeparator}{name}";
            Controls.ControlReported(controlName);
            var gain = Controls.FindLevel(controlName);
            var mute = Controls.FindMute(controlName);
            var text = Controls.FindString(controlName);
            if (gain != null && change[PositionField] is JsonValue position && position.TryGetValue<double>(out var level))
                gain.SetVolumeFromPercentage(Math.Round(level * PercentScale));
            if (mute != null && change[ValueField] is JsonValue value)
                mute.MuteState = IsMuted(value) ? MuteState.On : MuteState.Off;
            if (text != null)
                text.Value = change[StringField]?.ToString() ?? "";
        }
    }

    private static bool IsMuted(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetValue<double>(out var number) && number != UnmutedValue,
        _ => false
    };

    private QrcSet NewSet(string controlName, ComponentControl? split, JsonObject fields, string subject) =>
        new(controlName, split, fields, subject, Interlocked.Increment(ref _nextSequence), TimeProvider.GetTimestamp(),
            TimeSpan.FromSeconds(CoreClient.QueueTimeoutFor(WaitsForAReconnect())), Trigger.Id);

    private bool WaitsForAReconnect() =>
        Actor.Current is { IsPerson: true } && (CoreClient.ReconnectingForSend || CoreClient.DesiredConnectionState == ConnectionState.Disconnected);

    private void SendSet(QrcSet set)
    {
        lock (_sendLock)
        {
            bool waitsForTheLogon;
            lock (_lock)
            {
                _held.RemoveAll(held => held.ControlName == set.ControlName);
                waitsForTheLogon = !_loggedIn && CommunicationClient.ConnectionState == ConnectionState.Connected;
            }
            if (waitsForTheLogon)
                Hold(set, HeldForTheLogon);
            else
                TransmitSet(set);
        }
    }

    private void TransmitSet(QrcSet set)
    {
        var resolution = set.Split is { } explicitSplit ? new Resolution(explicitSplit, false) : Resolve(set.ControlName);
        if (resolution.Split is { } split)
        {
            Transmit(SetRequest(set, split));
            return;
        }
        if (!resolution.AwaitsComponentList)
        {
            ShowUnsplit(set.ControlName);
            return;
        }
        Hold(set, HeldForTheComponentList);
        RequestComponentList();
    }

    private QrcRequest SetRequest(QrcSet set, ComponentControl split)
    {
        var control = new JsonObject { [NameField] = split.Control };
        foreach (var (field, value) in set.Fields)
            control[field] = value?.DeepClone();
        var parameters = new JsonObject { [NameField] = split.Component, [ControlsField] = new JsonArray(control) };
        return new QrcRequest(NextId(), ComponentSetMethod, parameters, set.Subject, [set.ControlName], set.TriggerId, split, set);
    }

    private void Hold(QrcSet set, string heldFor)
    {
        lock (_lock)
        {
            if (_held.Exists(held => held.ControlName == set.ControlName && held.Sequence > set.Sequence))
                return;
            _held.RemoveAll(held => held.ControlName == set.ControlName);
            _held.Add(set with { HeldFor = heldFor });
        }
    }

    private void ReleaseHeld()
    {
        List<QrcSet> held;
        lock (_lock)
        {
            held = [.._held.OrderBy(set => set.Sequence)];
            _held.Clear();
        }
        foreach (var set in held)
        {
            using (Trigger.Resume(set.TriggerId))
            using (PushProperties("ReleaseHeld"))
            {
                var age = TimeProvider.GetElapsedTime(set.MadeAt);
                if (age >= set.Lifetime)
                {
                    LogWarning("Dropping the set of {Subject} made {Age} s ago: it is older than the client keeps a queued send ({Timeout} s)",
                        set.Subject, Math.Round(age.TotalSeconds, 1), set.Lifetime.TotalSeconds);
                    Audit.RecordOutcome(set.TriggerId, AuditOutcomeKind.Dropped, Name,
                        $"{ExpiredWhileHeld} {set.HeldFor} after {age.TotalSeconds:0} s, timeout {set.Lifetime.TotalSeconds:0} s: {set.Subject} {set.Fields.ToJsonString()}");
                    continue;
                }
                TransmitSet(set);
            }
        }
    }

    private QrcRequest Request(string method, JsonNode? parameters, string subject, IReadOnlyList<string> controls) =>
        new(NextId(), method, parameters, subject, controls, Trigger.Id);

    private int NextId() => Interlocked.Increment(ref _nextId);

    private void Transmit(QrcRequest request)
    {
        using (Trigger.Resume(request.TriggerId))
        {
            lock (_lock)
                _unwritten[request.Id] = (request, TimeProvider.GetTimestamp());
            CommunicationClient.Send(Message(request.Id, request.Method, request.Params) + (char)Delimiter);
        }
    }

    private void RequestWritten(string message)
    {
        if (IdOf(message) is not { } id)
            return;
        QrcRequest request;
        SentCommand<QrcRequest>? forgotten;
        lock (_lock)
        {
            if (!_unwritten.Remove(id, out var unwritten))
                return;
            request = unwritten.Request;
            using (Trigger.Resume(request.TriggerId))
                forgotten = _sent.Add(request);
        }
        if (forgotten == null)
            return;
        using (Trigger.Resume(request.TriggerId))
        using (PushProperties("RequestWritten"))
            LogWarning("Forgot {Method} on {Subject}: more than {Max} requests are awaiting a reply",
                forgotten.Command.Method, forgotten.Command.Subject, SentCommandTracker<QrcRequest>.MaxCommands);
    }

    private static int? IdOf(string message)
    {
        try
        {
            return JsonNode.Parse(message.TrimEnd((char)Delimiter)) is JsonObject request && request[IdField] is JsonValue id
                && id.TryGetValue<int>(out var number) ? number : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private QrcRequest? Answered(int id)
    {
        lock (_lock)
        {
            if (_sent.TakeReply(request => request.Id == id) is { } sent)
                return sent.Command;
            return _unwritten.Remove(id, out var unwritten) ? unwritten.Request : null;
        }
    }

    private void ForgetUnwritten()
    {
        var queueTime = TimeSpan.FromSeconds(CoreClient.QueueTimeoutFor(true));
        lock (_lock)
        {
            foreach (var id in _unwritten.Where(unwritten => TimeProvider.GetElapsedTime(unwritten.Value.HandedAt) >= queueTime)
                         .Select(unwritten => unwritten.Key).ToList())
                _unwritten.Remove(id);
        }
    }

    private string MaskedLogon(int id) =>
        Message(id, LogonMethod, new JsonObject { [UserField] = _username, [PasswordField] = MaskedPassword });

    private static string PasswordText(string password) => new JsonObject { [PasswordField] = password }.ToJsonString()[1..^1];

    private static string Message(int id, string method, JsonNode? parameters) =>
        new JsonObject { [JsonRpcField] = JsonRpcVersion, [IdField] = id, [MethodField] = method, [ParamsField] = parameters?.DeepClone() }
            .ToJsonString();
}
