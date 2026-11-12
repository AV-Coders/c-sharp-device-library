using AVCoders.Core;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Connections;

public class HubConnections(string hubName) : LogBase(hubName)
{
    public const string UnknownGroupIssuePrefix = "unknown-group:";
    public const int UnknownGroupRequestLimit = 50;

    private readonly object _lock = new();
    private readonly Dictionary<string, OpenConnection> _connections = new();
    private readonly HashSet<string> _registeredGroups = new();
    private readonly Dictionary<string, string> _unknownGroupIssues = new();
    private readonly List<UnknownGroupRequest> _unknownGroupRequests = [];

    private static ILogger Logger => LoggerFactory.CreateLogger<HubConnections>();

    public event EventHandler? Changed;

    public void Open(string connectionId)
    {
        lock (_lock)
            _connections.TryAdd(connectionId, new OpenConnection());
        Log(LogLevel.Debug, nameof(Open), "Connection {ConnectionId} opened", connectionId);
        RaiseChanged();
    }

    public void Close(string connectionId, Exception? exception = null)
    {
        List<string> released;
        lock (_lock)
        {
            if (!_connections.Remove(connectionId, out var closed))
                return;
            released = closed.UnknownGroups.Where(group => !IsAskedFor(group)).ToList();
            foreach (var group in released)
                _unknownGroupIssues.Remove(group);
        }
        Log(LogLevel.Debug, nameof(Close), "Connection {ConnectionId} closed. Error: {Error}", connectionId, exception?.Message ?? "none");
        foreach (var group in released)
            ReconcileIssue(group);
        RaiseChanged();
    }

    public void Join(string connectionId, string group)
    {
        bool open;
        lock (_lock)
        {
            open = _connections.TryGetValue(connectionId, out var connection);
            if (open && !connection!.Groups.Add(group))
                return;
        }
        if (!open)
        {
            Log(LogLevel.Debug, nameof(Join), "Connection {ConnectionId} closed before it joined group {Group}, so it isn't counted", connectionId, group);
            return;
        }
        Log(LogLevel.Debug, nameof(Join), "Connection {ConnectionId} joined group {Group}", connectionId, group);
        RaiseChanged();
    }

    public void Register(string group)
    {
        lock (_lock)
        {
            if (!_registeredGroups.Add(group))
                return;
            foreach (var connection in _connections.Values)
                connection.UnknownGroups.Remove(group);
            _unknownGroupIssues.Remove(group);
        }
        Log(LogLevel.Debug, nameof(Register), "Group {Group} registered", group);
        ReconcileIssue(group);
        RaiseChanged();
    }

    public void ReportUnknownGroup(string connectionId, string method, string group)
    {
        string registered;
        bool raisesIssue;
        lock (_lock)
        {
            _unknownGroupRequests.Add(new UnknownGroupRequest(DateTimeOffset.UtcNow, Name, method, connectionId, group));
            if (_unknownGroupRequests.Count > UnknownGroupRequestLimit)
                _unknownGroupRequests.RemoveAt(0);
            registered = RegisteredGroupList();
            raisesIssue = _connections.TryGetValue(connectionId, out var connection) && !_registeredGroups.Contains(group);
            if (raisesIssue)
            {
                connection!.UnknownGroups.Add(group);
                _unknownGroupIssues[group] =
                    $"{method} from connection {connectionId} asked for group '{group}', which has nothing registered. Registered groups: {registered}";
            }
        }
        Log(LogLevel.Warning, nameof(ReportUnknownGroup),
            "{Method} from connection {ConnectionId} asked {Hub} for group {Group}, which has nothing registered. Registered groups: {RegisteredGroups}",
            method, connectionId, Name, group, registered);
        if (raisesIssue)
            ReconcileIssue(group);
        RaiseChanged();
    }

    public int Count(string group)
    {
        lock (_lock)
            return CountIn(group);
    }

    public HubConnectionsSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var groups = _registeredGroups
                .Union(_connections.Values.SelectMany(connection => connection.Groups))
                .Order(StringComparer.Ordinal)
                .Select(group => new GroupConnections(group, _registeredGroups.Contains(group), CountIn(group)))
                .ToList();
            return new HubConnectionsSnapshot(Name, _connections.Count, groups, _unknownGroupRequests.ToList());
        }
    }

    private void ReconcileIssue(string group)
    {
        var key = UnknownGroupIssuePrefix + group;
        var applied = IssueMessageFor(group);
        while (true)
        {
            if (applied == null)
                ResolveIssue(key);
            else
                RaiseOngoingIssue(key, applied);
            var current = IssueMessageFor(group);
            if (current == applied)
                return;
            applied = current;
        }
    }

    private string? IssueMessageFor(string group)
    {
        lock (_lock)
            return _unknownGroupIssues.GetValueOrDefault(group);
    }

    private int CountIn(string group) => _connections.Values.Count(connection => connection.Groups.Contains(group));

    private bool IsAskedFor(string group) => _connections.Values.Any(connection => connection.UnknownGroups.Contains(group));

    private string RegisteredGroupList() =>
        _registeredGroups.Count == 0 ? "none" : string.Join(", ", _registeredGroups.Order(StringComparer.Ordinal));

    private void Log(LogLevel level, string method, string template, params object?[] args)
    {
        var logger = Logger;
        if (!logger.IsEnabled(level))
            return;
        using (logger.BeginScope(new Dictionary<string, object?>
               {
                   ["Class"] = nameof(HubConnections),
                   ["InstanceName"] = Name,
                   [MethodProperty] = method,
                   [ActorProperty] = Actor.Current?.ToString(),
                   [TriggerIdProperty] = Trigger.Id
               }))
            logger.Log(level, template, args);
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers == null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                LogException(e, "A Changed handler threw an exception");
            }
        }
    }

    private sealed class OpenConnection
    {
        public HashSet<string> Groups { get; } = [];
        public HashSet<string> UnknownGroups { get; } = [];
    }
}
