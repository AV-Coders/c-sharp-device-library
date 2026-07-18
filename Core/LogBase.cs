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

    private readonly Dictionary<string, ActiveError> _activeErrors = new();
    private readonly object _activeErrorsLock = new();
    // Lazily created on the first momentary error, then quiesced (never disposed) when none remain.
    // LogBase is not IDisposable — driver instances are process-lifetime, so the timer rooting this
    // instance is acceptable and a quiescent timer costs nothing.
    private Timer? _activeErrorExpiryTimer;
    private int _activeErrorLimit = 50;
    public event EventHandler<ActiveErrorsChangedEventArgs>? ActiveErrorsChanged;

    /// <summary>TTL applied to momentary errors raised without an explicit TTL.</summary>
    public TimeSpan DefaultMomentaryErrorTtl { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<Event> Events
    {
        get { lock (_eventsLock) return _events.ToList(); }
    }

    public IReadOnlyList<Error> Errors
    {
        get { lock (_errorsLock) return _errors.ToList(); }
    }

    /// <summary>
    /// The errors currently affecting this instance, oldest first. Momentary errors leave the list
    /// when their TTL lapses; persistent errors leave when the driver clears them on recovery.
    /// </summary>
    public IReadOnlyList<ActiveError> ActiveErrors
    {
        get { lock (_activeErrorsLock) return _activeErrors.Values.OrderBy(e => e.RaisedAt).ToList(); }
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
    /// Raises a momentary error (e.g. a missed poll response) that auto-expires after
    /// <paramref name="ttl"/> (default <see cref="DefaultMomentaryErrorTtl"/>). Re-raising the same
    /// <paramref name="key"/> (defaults to the message) refreshes the existing entry's TTL.
    /// </summary>
    protected void RaiseMomentaryError(string message, TimeSpan? ttl = null, string? key = null)
    {
        key ??= message;
        var now = DateTimeOffset.UtcNow;
        bool isNewOrChanged;
        lock (_activeErrorsLock)
        {
            isNewOrChanged = !_activeErrors.TryGetValue(key, out var existing) || existing.Message != message;
            _activeErrors[key] = new ActiveError(key, message, ErrorPersistence.Momentary, now,
                now + (ttl ?? DefaultMomentaryErrorTtl));
            LimitActiveErrors(key);
            RescheduleExpiry();
        }
        if (isNewOrChanged)
        {
            LogWarning("{ActiveErrorMessage}", message);
            AddEvent(EventType.Error, message);
        }
        RaiseActiveErrorsChanged();
    }

    /// <summary>
    /// Raises a persistent error (e.g. a device stuck on the wrong input) that stays active until
    /// <see cref="ClearPersistentError"/> is called with the same <paramref name="key"/>.
    /// Re-raising an unchanged key/message is a no-op, so this is safe to call every poll cycle.
    /// </summary>
    protected void RaisePersistentError(string key, string message)
    {
        lock (_activeErrorsLock)
        {
            if (_activeErrors.TryGetValue(key, out var existing)
                && existing.Persistence == ErrorPersistence.Persistent && existing.Message == message)
                return;
            _activeErrors[key] = new ActiveError(key, message, ErrorPersistence.Persistent,
                DateTimeOffset.UtcNow, null);
            LimitActiveErrors(key);
            RescheduleExpiry();
        }
        LogWarning("{ActiveErrorMessage}", message);
        AddEvent(EventType.Error, message);
        RaiseActiveErrorsChanged();
    }

    /// <summary>
    /// Clears a persistent error once the driver has recovered the condition. Clearing a key that
    /// isn't active is a no-op, so this is safe to call every poll cycle.
    /// </summary>
    protected void ClearPersistentError(string key)
    {
        lock (_activeErrorsLock)
        {
            if (!_activeErrors.TryGetValue(key, out var existing)
                || existing.Persistence != ErrorPersistence.Persistent)
                return;
            _activeErrors.Remove(key);
        }
        LogInformation("Cleared active error {ActiveErrorKey}", key);
        AddEvent(EventType.Error, $"Cleared: {key}");
        RaiseActiveErrorsChanged();
    }

    public void SetActiveErrorLimit(int limit)
    {
        lock (_activeErrorsLock)
        {
            _activeErrorLimit = limit;
            LimitActiveErrors(null);
            RescheduleExpiry();
        }
        RaiseActiveErrorsChanged();
    }

    // Must be called under _activeErrorsLock. Evicts oldest momentary entries first (they would
    // expire anyway), then oldest persistent ones, but never the just-raised protectedKey.
    private void LimitActiveErrors(string? protectedKey)
    {
        if (_activeErrors.Count <= _activeErrorLimit)
            return;
        foreach (var key in _activeErrors.Values
                     .Where(e => e.Key != protectedKey)
                     .OrderBy(e => e.Persistence)
                     .ThenBy(e => e.RaisedAt)
                     .Take(_activeErrors.Count - _activeErrorLimit)
                     .Select(e => e.Key)
                     .ToList())
            _activeErrors.Remove(key);
    }

    // Must be called under _activeErrorsLock.
    private void RescheduleExpiry()
    {
        DateTimeOffset? next = null;
        foreach (var error in _activeErrors.Values)
        {
            if (error.ExpiresAt is { } expiry && (next == null || expiry < next))
                next = expiry;
        }

        if (next == null)
        {
            _activeErrorExpiryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        _activeErrorExpiryTimer ??= new Timer(ExpireActiveErrors, null, Timeout.Infinite, Timeout.Infinite);
        var due = next.Value - DateTimeOffset.UtcNow;
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;
        _activeErrorExpiryTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void ExpireActiveErrors(object? state)
    {
        var removed = false;
        lock (_activeErrorsLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _activeErrors.Where(kvp => kvp.Value.ExpiresAt <= now)
                         .Select(kvp => kvp.Key).ToList())
            {
                _activeErrors.Remove(key);
                removed = true;
            }
            RescheduleExpiry();
        }
        if (removed)
            RaiseActiveErrorsChanged();
    }

    private void RaiseActiveErrorsChanged() =>
        ActiveErrorsChanged?.Invoke(this, new ActiveErrorsChangedEventArgs(ActiveErrors));

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