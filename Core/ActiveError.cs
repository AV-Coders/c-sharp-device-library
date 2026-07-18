namespace AVCoders.Core;

public enum ErrorPersistence
{
    /// <summary>Auto-expires after a TTL (e.g. a missed poll response).</summary>
    Momentary,
    /// <summary>Stays active until the driver clears it by key (e.g. a display on the wrong input).</summary>
    Persistent
}

public record ActiveError(
    string Key,
    string Message,
    ErrorPersistence Persistence,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ExpiresAt);

public class ActiveErrorsChangedEventArgs(IReadOnlyList<ActiveError> activeErrors) : EventArgs
{
    public IReadOnlyList<ActiveError> ActiveErrors { get; } = activeErrors;
}
