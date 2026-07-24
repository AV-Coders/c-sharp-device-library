namespace AVCoders.Core;

public enum IssueStatus
{
    /// <summary>Affecting the device now; stays until the driver calls ResolveIssue.</summary>
    Ongoing,
    /// <summary>A one-off incident (e.g. a missed poll response). Recorded instantly as history.</summary>
    Momentary,
    /// <summary>A formerly ongoing issue the driver has observed recover. Kept as history.</summary>
    Resolved
}

public enum IssueSeverity
{
    Minor,
    Major,
    Critical
}

/// <summary>What a single <see cref="LogBase.IssuesChanged"/> firing represents.</summary>
public enum IssueChangeKind
{
    /// <summary>A new entry was appended — ongoing or momentary, including escalation-created ongoing entries.</summary>
    Raised,
    /// <summary>An existing entry changed — a message or severity update, or a momentary coalesce (occurrence count bump).</summary>
    Updated,
    /// <summary>An ongoing entry transitioned to <see cref="IssueStatus.Resolved"/>.</summary>
    Resolved,
    /// <summary>Entries were evicted by the cap or a limit change.</summary>
    Trimmed
}

/// <summary>
/// An incident raised by a driver. <see cref="Id"/> is assigned once and survives message updates,
/// coalescing and resolution, so external systems (e.g. ticketing) can correlate by it; a re-raise
/// after resolution is a new issue with a new Id. <see cref="ResolvedAt"/> is non-null only for
/// <see cref="IssueStatus.Resolved"/> entries.
/// </summary>
public record Issue(
    Guid Id,
    string Key,
    string Message,
    IssueStatus Status,
    IssueSeverity Severity,
    DateTimeOffset RaisedAt,
    DateTimeOffset LastRaisedAt,
    int OccurrenceCount,
    DateTimeOffset? ResolvedAt);

public class IssuesChangedEventArgs(Issue? changedIssue, IssueChangeKind kind, IReadOnlyList<Issue> issues)
    : EventArgs
{
    /// <summary>The affected issue. Null only for <see cref="IssueChangeKind.Trimmed"/>.</summary>
    public Issue? ChangedIssue { get; } = changedIssue;
    public IssueChangeKind Kind { get; } = kind;
    /// <summary>Snapshot of the instance's full issue list after the change, oldest first.</summary>
    public IReadOnlyList<Issue> Issues { get; } = issues;
}
