using Microsoft.Extensions.Logging;

namespace AVCoders.Core;

public enum AuditOutcomeKind
{
    Dropped,
    Rejected,
    NoReply
}

public record AuditOutcome(DateTimeOffset Timestamp, AuditOutcomeKind Kind, string Source, string Detail);

public record AuditEntry(DateTimeOffset Timestamp, Actor? Actor, string? TriggerId, string Target, string Action, string Detail)
{
    public IReadOnlyList<AuditOutcome> Outcomes { get; init; } = [];
}

public static class Audit
{
    public const string Category = "AVCoders.Audit";
    public const int EntryLimit = 1000;
    public const int OutcomeLimit = 20;
    public static readonly TimeSpan OutcomeWindow = TimeSpan.FromMinutes(10);
    private const string UnknownActorKind = "Unknown";
    private static readonly List<AuditEntry> Entries = [];
    private static readonly object EntriesLock = new();

    public static event AuditEntryHandler? Recorded;

    public static event AuditEntryHandler? Updated;

    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    public static void Record(string target, string action, string detail = "")
    {
        var entry = new AuditEntry(Clock.GetUtcNow(), Actor.Current, Trigger.Id, target, action, detail);
        lock (EntriesLock)
        {
            Entries.Add(entry);
            if (Entries.Count > EntryLimit)
                Entries.RemoveRange(0, Entries.Count - EntryLimit);
        }

        var actor = entry.Actor;
        if (actor is { Name.Length: > 0 })
            LogBase.LoggerFactory.CreateLogger(Category).LogInformation(
                "{AuditTarget}: {AuditAction} {AuditDetail} by {ActorKind} {ActorName} {TriggerId}",
                target, action, detail, actor.Kind.ToString(), actor.Name, entry.TriggerId ?? string.Empty);
        else
            LogBase.LoggerFactory.CreateLogger(Category).LogInformation(
                "{AuditTarget}: {AuditAction} {AuditDetail} by {ActorKind} {ClientIp} {ClientHostName} {ClientUserAgent} {ClientConnectionId} {TriggerId}",
                target, action, detail,
                actor?.Kind.ToString() ?? UnknownActorKind,
                actor?.Ip ?? string.Empty,
                actor?.HostName ?? string.Empty,
                actor?.UserAgent ?? string.Empty,
                actor?.ConnectionId ?? string.Empty,
                entry.TriggerId ?? string.Empty);

        Raise(Recorded, entry, nameof(Recorded));
    }

    public static void RecordOutcome(string? triggerId, AuditOutcomeKind kind, string source, string detail)
    {
        if (triggerId == null)
            return;
        var now = Clock.GetUtcNow();
        var outcome = new AuditOutcome(now, kind, source, Visible(detail));
        var updated = new List<AuditEntry>();
        lock (EntriesLock)
        {
            for (var i = Entries.Count - 1; i >= 0 && Entries[i].Timestamp >= now - OutcomeWindow; i--)
            {
                if (Entries[i].TriggerId != triggerId)
                    continue;
                var outcomes = Entries[i].Outcomes.Append(outcome).TakeLast(OutcomeLimit).ToList();
                Entries[i] = Entries[i] with { Outcomes = outcomes };
                updated.Add(Entries[i]);
            }
        }

        foreach (var entry in updated)
        {
            LogBase.LoggerFactory.CreateLogger(Category).LogWarning(
                "{AuditTarget}: {AuditAction} {AuditOutcome} at {AuditOutcomeSource}: {AuditOutcomeDetail} {TriggerId}",
                entry.Target, entry.Action, kind, source, outcome.Detail, triggerId);
            Raise(Updated, entry, nameof(Updated));
        }
    }

    private static string Visible(string text) =>
        string.Concat(text.Select(c => char.IsControl(c) ? $"\\x{(int)c:X2}" : c.ToString()));

    public static IReadOnlyList<AuditEntry> GetEntries()
    {
        lock (EntriesLock)
            return Entries.ToList();
    }

    private static void Raise(AuditEntryHandler? handlers, AuditEntry entry, string eventName)
    {
        if (handlers == null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((AuditEntryHandler)handler)(entry);
            }
            catch (Exception e)
            {
                LogBase.LoggerFactory.CreateLogger(Category).LogWarning(e, "An Audit.{Event} handler threw an exception", eventName);
            }
        }
    }
}
