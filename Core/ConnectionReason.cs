namespace AVCoders.Core;

public enum ConnectionReasonCategory
{
    TimedOut,
    Refused,
    HostNotFound,
    Unreachable,
    CredentialsRejected,
    ClosedByDevice,
    Requested,
    Other
}

public record ConnectionReason(ConnectionReasonCategory Category, string Description, string? RawException, DateTimeOffset Since);

public delegate void ConnectionReasonHandler(ConnectionReason? reason);
