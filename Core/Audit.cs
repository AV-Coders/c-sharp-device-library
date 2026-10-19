using Microsoft.Extensions.Logging;

namespace AVCoders.Core;

public record AuditEntry(DateTimeOffset Timestamp, Actor? Actor, string? TriggerId, string Target, string Action, string Detail);

public static class Audit
{
    public const string Category = "AVCoders.Audit";
    public const int EntryLimit = 1000;
    private const string UnknownActorKind = "Unknown";
    private static readonly List<AuditEntry> Entries = [];
    private static readonly object EntriesLock = new();

    public static event AuditEntryHandler? Recorded;

    public static void Record(string target, string action, string detail = "")
    {
        var entry = new AuditEntry(DateTimeOffset.UtcNow, Actor.Current, Trigger.Id, target, action, detail);
        lock (EntriesLock)
        {
            Entries.Add(entry);
            if (Entries.Count > EntryLimit)
                Entries.RemoveRange(0, Entries.Count - EntryLimit);
        }

        var actor = entry.Actor;
        LogBase.LoggerFactory.CreateLogger(Category).LogInformation(
            "{AuditTarget}: {AuditAction} {AuditDetail} by {ActorKind} {ClientIp} {ClientHostName} {ClientUserAgent} {ClientConnectionId} {TriggerId}",
            target, action, detail,
            actor?.Kind.ToString() ?? UnknownActorKind,
            actor?.Ip ?? string.Empty,
            actor?.HostName ?? string.Empty,
            actor?.UserAgent ?? string.Empty,
            actor?.ConnectionId ?? string.Empty,
            entry.TriggerId ?? string.Empty);

        RaiseRecorded(entry);
    }

    public static IReadOnlyList<AuditEntry> GetEntries()
    {
        lock (EntriesLock)
            return Entries.ToList();
    }

    private static void RaiseRecorded(AuditEntry entry)
    {
        var handlers = Recorded;
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
                LogBase.LoggerFactory.CreateLogger(Category).LogWarning(e, "An Audit.Recorded handler threw an exception");
            }
        }
    }
}
