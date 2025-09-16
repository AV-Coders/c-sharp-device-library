namespace AVCoders.Core;

public record Error(DateTime Timestamp, string Message, Exception? Exception = null);