namespace AVCoders.Core;

public record Error(DateTimeOffset Timestamp, string Message, Exception? Exception = null);