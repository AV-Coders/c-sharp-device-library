namespace AVCoders.Core;

public record SourcedIssue(LogBase Source, Issue Issue);

public class OngoingIssuesChangedEventArgs(IReadOnlyList<SourcedIssue> ongoingIssues) : EventArgs
{
    public IReadOnlyList<SourcedIssue> OngoingIssues { get; } = ongoingIssues;
}

/// <summary>
/// Every <see cref="LogBase"/> registers itself here on construction, giving dashboards an
/// aggregate view of what's wrong across the whole system via <see cref="GetOngoingIssues"/> and
/// <see cref="OngoingIssuesChanged"/>. Per-device integrations (e.g. ticketing) should subscribe to
/// each instance's <see cref="LogBase.IssuesChanged"/> instead — it carries the changed issue and
/// change kind. Transiently-created instances must call <see cref="Deregister"/> on teardown or
/// they stay rooted here for the life of the process.
/// </summary>
public static class LogBaseRegistry
{
    private static readonly List<LogBase> Instances = [];
    private static readonly object Lock = new();

    /// <summary>
    /// Fires when any instance's ongoing issue set changes — a "refresh your dashboard" signal
    /// carrying the aggregate snapshot. Momentary-only changes (blip counters ticking) are skipped.
    /// The originating instance is passed as sender. Subscriber exceptions are swallowed — handle
    /// your own if you want visibility.
    /// </summary>
    public static event EventHandler<OngoingIssuesChangedEventArgs>? OngoingIssuesChanged;

    public static void Register(LogBase instance)
    {
        lock (Lock)
        {
            Instances.Add(instance);
        }
        instance.IssuesChanged += OnInstanceIssuesChanged;
    }

    public static void Deregister(LogBase instance)
    {
        lock (Lock)
        {
            Instances.Remove(instance);
        }
        instance.IssuesChanged -= OnInstanceIssuesChanged;
    }

    public static IReadOnlyList<LogBase> GetAll()
    {
        lock (Lock)
        {
            return Instances.ToList();
        }
    }

    /// <summary>All ongoing issues across every registered instance, paired with their source.</summary>
    public static IReadOnlyList<SourcedIssue> GetOngoingIssues() =>
        GetAll().SelectMany(i => i.OngoingIssues.Select(issue => new SourcedIssue(i, issue))).ToList();

    public static void ClearEvents()
    {
        foreach (var instance in GetAll())
            instance.ClearEvents();
    }

    public static void ClearErrors()
    {
        foreach (var instance in GetAll())
            instance.ClearErrors();
    }

    public static void SetEventLimits(int limit)
    {
        foreach (var instance in GetAll())
            instance.SetEventLimit(limit);
    }

    public static void SetErrorLimits(int limit)
    {
        foreach (var instance in GetAll())
            instance.SetErrorLimit(limit);
    }

    public static void SetIssueLimits(int limit)
    {
        foreach (var instance in GetAll())
            instance.SetIssueLimit(limit);
    }

    private static void OnInstanceIssuesChanged(object? sender, IssuesChangedEventArgs e)
    {
        // Only changes that can affect the ongoing set warrant a dashboard refresh.
        var affectsOngoing = e.Kind switch
        {
            IssueChangeKind.Raised or IssueChangeKind.Updated => e.ChangedIssue?.Status == IssueStatus.Ongoing,
            IssueChangeKind.Resolved or IssueChangeKind.Trimmed => true,
            _ => true
        };
        if (!affectsOngoing)
            return;

        var handlers = OngoingIssuesChanged;
        if (handlers == null)
            return;
        var args = new OngoingIssuesChangedEventArgs(GetOngoingIssues());
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<OngoingIssuesChangedEventArgs>)handler)(sender, args);
            }
            catch
            {
                // The registry has no log sink of its own; per-subscriber isolation only.
            }
        }
    }
}
