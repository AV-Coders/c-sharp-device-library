namespace AVCoders.CommunicationClients;

public record QueuedPayload<T>(DateTimeOffset Timestamp, T Payload);