using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AVCoders.Core;

public record Event(
    DateTimeOffset Timestamp,
    EventType Type,
    string Info,
    IReadOnlyDictionary<string, string> Context);

public abstract class LogBase
{
    /// <summary>
    /// Factory used to create every <see cref="LogBase"/> instance's logger. Consumers set this once at
    /// startup (e.g. <c>LogBase.LoggerFactory = new SerilogLoggerFactory(Log.Logger);</c>). Left unset,
    /// logging is silently discarded via <see cref="NullLoggerFactory"/>.
    /// </summary>
    public static ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>
    /// The name consumers subscribe to for tracing — e.g. OpenTelemetry
    /// <c>.WithTracing(t =&gt; t.AddSource(LogBase.ActivitySourceName))</c>.
    /// </summary>
    public const string ActivitySourceName = "AVCoders.Core";

    /// <summary>
    /// Source of the per-method spans created by <see cref="PushProperties"/>. It is also available for
    /// consumers to start their own parent spans; sub-spans nest under whatever <see cref="Activity.Current"/>
    /// is active. No registered listener means <c>StartActivity</c> returns null at near-zero cost.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly ILogger _logger;
    private string _name;
    public const string MethodProperty = "Method";
    public const string TriggerProperty = "Trigger";
    public readonly string InstanceUid = Guid.NewGuid().ToString();
    private readonly Dictionary<string, string> _logProperties = new ();
    public StringHandler? NameChangedHandlers;
    private readonly List<Error> _errors = [];
    private readonly List<Event> _events = [];
    private readonly object _eventsLock = new();
    private readonly object _errorsLock = new();
    private int _errorLimit = 10;
    private int _eventLimit = 100;
    public event ActionHandler? EventsUpdated;
    public event ActionHandler? ErrorsUpdated;

    private readonly List<Issue> _issues = [];
    // Consecutive momentary raises per key since the last ResolveIssue(key) — drives escalation.
    // Distinct from Issue.OccurrenceCount, which is cumulative and never resets.
    private readonly Dictionary<string, int> _consecutiveMomentary = new();
    private readonly object _issuesLock = new();
    private int _issueLimit = 50;
    public event EventHandler<IssuesChangedEventArgs>? IssuesChanged;

    public IReadOnlyList<Event> Events
    {
        get { lock (_eventsLock) return _events.ToList(); }
    }

    public IReadOnlyList<Error> Errors
    {
        get { lock (_errorsLock) return _errors.ToList(); }
    }

    /// <summary>The full bounded issue history — ongoing, momentary and resolved entries, oldest first.</summary>
    public IReadOnlyList<Issue> Issues
    {
        get { lock (_issuesLock) return _issues.ToList(); }
    }

    /// <summary>The issues currently affecting this instance, oldest first.</summary>
    public IReadOnlyList<Issue> OngoingIssues
    {
        get { lock (_issuesLock) return _issues.Where(i => i.Status == IssueStatus.Ongoing).ToList(); }
    }

    public string Name
    {
        get => _name;
        protected set
        {
            if (value == _name)
                return;
            _name = value;
            NameChangedHandlers?.Invoke(value);
        }
    }

    protected LogBase(string name)
    {
        _name = name;
        _logger = LoggerFactory.CreateLogger(GetType());
        // Instances are process-lifetime in this library's model; transiently-created inheritors
        // must call LogBaseRegistry.Deregister on teardown or they stay rooted here.
        LogBaseRegistry.Register(this);
    }

    public void AddLogProperty(string name, string value)
    {
        _logProperties[name] = value;
    }

    /// <summary>
    /// Opens a logging scope carrying this instance's context (custom properties, InstanceUid, Class,
    /// InstanceName and the Method) and, when a method name is known, a tracing sub-span named
    /// <c>Class.Method</c> under the current <see cref="Activity"/>. Wrap work in
    /// <c>using (PushProperties())</c>; both are ambient for the block. <paramref name="methodName"/>
    /// defaults to the calling member via <see cref="CallerMemberNameAttribute"/> — pass it explicitly
    /// only to override, or <c>null</c> to suppress the span and Method property.
    /// </summary>
    protected IDisposable PushProperties([CallerMemberName] string? methodName = null)
    {
        var scope = new Dictionary<string, object>(_logProperties.Count + 4);
        foreach (var property in _logProperties)
            scope[property.Key] = property.Value;

        scope["InstanceUid"] = InstanceUid;
        scope["Class"] = GetType().Name;
        if (Name != string.Empty)
            scope["InstanceName"] = Name;
        if (methodName != null)
            scope[MethodProperty] = methodName;

        var logScope = _logger.BeginScope(scope) ?? NullScope.Instance;

        // Start a child span only when someone is listening (HasListeners avoids the name allocation
        // otherwise). It parents to Activity.Current, so it nests under any externally-created span.
        if (methodName == null || !ActivitySource.HasListeners())
            return logScope;

        var activity = ActivitySource.StartActivity($"{GetType().Name}.{methodName}");
        if (activity == null)
            return logScope;

        foreach (var property in scope)
            activity.SetTag(property.Key, property.Value);

        return new MethodScope(activity, logScope);
    }

    protected void LogVerbose(string template, params object?[] args) => Write(LogLevel.Trace, null, template, args);
    protected void LogDebug(string template, params object?[] args) => Write(LogLevel.Debug, null, template, args);
    protected void LogInformation(string template, params object?[] args) => Write(LogLevel.Information, null, template, args);
    protected void LogWarning(string template, params object?[] args) => Write(LogLevel.Warning, null, template, args);
    protected void LogError(string template, params object?[] args) => Write(LogLevel.Error, null, template, args);
    protected void LogError(Exception e, string template, params object?[] args) => Write(LogLevel.Error, e, template, args);

    private void Write(LogLevel level, Exception? exception, string template, object?[] args)
    {
        if (!_logger.IsEnabled(level))
            return;
        _logger.Log(level, exception, template, args);
    }

    protected IReadOnlyDictionary<string, string> SnapshotContext()
    {
        var context = new Dictionary<string, string>(_logProperties)
        {
            ["InstanceUid"] = InstanceUid,
            ["Class"] = GetType().Name
        };
        if (Name != string.Empty)
            context["InstanceName"] = Name;
        return context;
    }

    protected void LogException(Exception e, string? message = null)
    {
        // null: don't override the caller's ambient Method scope with "LogException".
        using (PushProperties(null))
        {
            _logger.LogError(e, "{Context}", message ?? e.Message);
            if (e.InnerException != null)
                _logger.LogError(e.InnerException, "{Context}", e.InnerException.Message);
        }

        lock (_errorsLock)
        {
            _errors.Add(new Error(DateTimeOffset.UtcNow, message ?? e.Message, e));
            LimitErrors();
        }
        ErrorsUpdated?.Invoke();
    }

    public void ClearErrors()
    {
        lock (_errorsLock)
        {
            _errors.Clear();
        }
        ErrorsUpdated?.Invoke();
    }

    private void LimitErrors()
    {
        if (_errors.Count > _errorLimit)
            _errors.RemoveRange(0, _errors.Count - _errorLimit);
    }

    protected void AddEvent(EventType type, string info)
    {
        // null: don't override the caller's ambient Method scope with "AddEvent".
        using (PushProperties(null))
        {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("{EventType}: {EventInfo}", type, info);
        }
        lock (_eventsLock)
        {
            _events.Add(new Event(DateTimeOffset.UtcNow, type, info, SnapshotContext()));
            LimitEvents();
        }
        EventsUpdated?.Invoke();
    }

    public void ClearEvents()
    {
        lock (_eventsLock)
        {
            _events.Clear();
        }
        EventsUpdated?.Invoke();
    }

    private void LimitEvents()
    {
        if (_events.Count > _eventLimit)
            _events.RemoveRange(0, _events.Count - _eventLimit);
    }

    public void SetEventLimit(int limit)
    {
        lock (_eventsLock)
        {
            _eventLimit = limit;
            LimitEvents();
        }
        EventsUpdated?.Invoke();
    }

    public void SetErrorLimit(int limit)
    {
        lock (_errorsLock)
        {
            _errorLimit = limit;
            LimitErrors();
        }
        ErrorsUpdated?.Invoke();
    }

    /// <summary>
    /// Records a momentary issue (e.g. a missed poll response). Momentary issues are instantly
    /// historical — they never appear in <see cref="OngoingIssues"/>. Re-raising while the latest
    /// entry for <paramref name="key"/> (defaults to the message) is momentary coalesces into it,
    /// bumping <see cref="Issue.OccurrenceCount"/> and <see cref="Issue.LastRaisedAt"/>.
    /// When <paramref name="escalateAfter"/> is supplied, that many consecutive momentary raises of
    /// the key without an intervening <see cref="ResolveIssue"/> raises an ongoing issue under the
    /// same key, one severity level higher — call <see cref="ResolveIssue"/> on every successful
    /// response so recovery both resolves the ongoing issue and resets the count.
    /// </summary>
    protected void RaiseMomentaryIssue(string message, string? key = null,
        IssueSeverity severity = IssueSeverity.Minor, int? escalateAfter = null)
    {
        key ??= message;
        var now = DateTimeOffset.UtcNow;
        Issue momentary;
        IssueChangeKind kind;
        bool isNewOrChanged;
        Issue? escalated = null;
        lock (_issuesLock)
        {
            var last = _issues.FindLastIndex(i => i.Key == key);
            if (last >= 0 && _issues[last].Status == IssueStatus.Momentary)
            {
                var existing = _issues[last];
                isNewOrChanged = existing.Message != message || existing.Severity != severity;
                momentary = existing with
                {
                    Message = message, Severity = severity, LastRaisedAt = now,
                    OccurrenceCount = existing.OccurrenceCount + 1
                };
                _issues[last] = momentary;
                kind = IssueChangeKind.Updated;
            }
            else
            {
                isNewOrChanged = true;
                momentary = new Issue(Guid.NewGuid(), key, message, IssueStatus.Momentary, severity,
                    now, now, 1, null);
                _issues.Add(momentary);
                LimitIssues(momentary);
                kind = IssueChangeKind.Raised;
            }

            var consecutive = _consecutiveMomentary.GetValueOrDefault(key) + 1;
            _consecutiveMomentary[key] = consecutive;
            if (escalateAfter is { } threshold && consecutive >= threshold
                && _issues.FindLastIndex(i => i.Key == key && i.Status == IssueStatus.Ongoing) < 0)
            {
                escalated = new Issue(Guid.NewGuid(), key,
                    $"{message} ({consecutive} consecutive occurrences)", IssueStatus.Ongoing,
                    Escalate(severity), now, now, 1, null);
                _issues.Add(escalated);
                LimitIssues(escalated);
            }
        }
        if (isNewOrChanged)
        {
            LogWarning("{IssueMessage}", message);
            AddEvent(EventType.Error, message);
        }
        RaiseIssuesChanged(momentary, kind);
        if (escalated != null)
        {
            LogWarning("{IssueMessage}", escalated.Message);
            AddEvent(EventType.Error, escalated.Message);
            RaiseIssuesChanged(escalated, IssueChangeKind.Raised);
        }
    }

    /// <summary>
    /// Raises an ongoing issue (e.g. a device stuck on the wrong input) that stays in
    /// <see cref="OngoingIssues"/> until <see cref="ResolveIssue"/> is called with the same
    /// <paramref name="key"/>. Re-raising an unchanged key/message/severity is a no-op, so this is
    /// safe to call every poll cycle; a changed message or severity updates the entry in place.
    /// </summary>
    protected void RaiseOngoingIssue(string key, string message, IssueSeverity severity = IssueSeverity.Major)
    {
        var now = DateTimeOffset.UtcNow;
        Issue issue;
        IssueChangeKind kind;
        lock (_issuesLock)
        {
            var index = _issues.FindLastIndex(i => i.Key == key && i.Status == IssueStatus.Ongoing);
            if (index >= 0)
            {
                var existing = _issues[index];
                if (existing.Message == message && existing.Severity == severity)
                    return;
                issue = existing with { Message = message, Severity = severity, LastRaisedAt = now };
                _issues[index] = issue;
                kind = IssueChangeKind.Updated;
            }
            else
            {
                issue = new Issue(Guid.NewGuid(), key, message, IssueStatus.Ongoing, severity,
                    now, now, 1, null);
                _issues.Add(issue);
                LimitIssues(issue);
                kind = IssueChangeKind.Raised;
            }
        }
        LogWarning("{IssueMessage}", message);
        AddEvent(EventType.Error, message);
        RaiseIssuesChanged(issue, kind);
    }

    /// <summary>
    /// Resolves an ongoing issue once the driver has observed the condition recover. The entry stays
    /// in <see cref="Issues"/> with <see cref="IssueStatus.Resolved"/> and its original
    /// <see cref="Issue.Id"/>; a later re-raise of the key is a new issue. Also resets the
    /// consecutive-failure count used by escalation, so this is safe (and intended) to call on every
    /// successful response.
    /// </summary>
    protected void ResolveIssue(string key)
    {
        Issue resolved;
        lock (_issuesLock)
        {
            _consecutiveMomentary.Remove(key);
            var index = _issues.FindLastIndex(i => i.Key == key && i.Status == IssueStatus.Ongoing);
            if (index < 0)
                return;
            resolved = _issues[index] with { Status = IssueStatus.Resolved, ResolvedAt = DateTimeOffset.UtcNow };
            _issues[index] = resolved;
        }
        LogInformation("Resolved issue {IssueKey}", key);
        AddEvent(EventType.Error, $"Resolved: {key}");
        RaiseIssuesChanged(resolved, IssueChangeKind.Resolved);
    }

    public void SetIssueLimit(int limit)
    {
        lock (_issuesLock)
        {
            _issueLimit = limit;
            LimitIssues(null);
        }
        RaiseIssuesChanged(null, IssueChangeKind.Trimmed);
    }

    private static IssueSeverity Escalate(IssueSeverity severity) =>
        severity == IssueSeverity.Critical ? IssueSeverity.Critical : severity + 1;

    // Must be called under _issuesLock. Evicts historical entries (momentary/resolved) first, oldest
    // first, then ongoing ones, but never the just-raised protectedIssue.
    private void LimitIssues(Issue? protectedIssue)
    {
        if (_issues.Count <= _issueLimit)
            return;
        foreach (var issue in _issues
                     .Where(i => i.Id != protectedIssue?.Id)
                     .OrderBy(i => i.Status == IssueStatus.Ongoing)
                     .ThenBy(i => i.RaisedAt)
                     .Take(_issues.Count - _issueLimit)
                     .ToList())
            _issues.Remove(issue);
    }

    // Subscribers are invoked individually and guarded: these fire on driver comm/poll threads,
    // where an unhandled subscriber exception would kill the worker, and one faulty subscriber must
    // not starve the rest (the registry is a permanent subscriber).
    private void RaiseIssuesChanged(Issue? changedIssue, IssueChangeKind kind)
    {
        var handlers = IssuesChanged;
        if (handlers == null)
            return;
        var args = new IssuesChangedEventArgs(changedIssue, kind, Issues);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<IssuesChangedEventArgs>)handler)(this, args);
            }
            catch (Exception e)
            {
                LogException(e, "An IssuesChanged handler threw an exception");
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }

    /// <summary>Ends the method span and the log scope together when the <c>using</c> block exits.</summary>
    private sealed class MethodScope(Activity activity, IDisposable logScope) : IDisposable
    {
        public void Dispose()
        {
            logScope.Dispose();
            activity.Dispose();
        }
    }
}