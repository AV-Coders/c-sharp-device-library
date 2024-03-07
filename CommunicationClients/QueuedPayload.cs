namespace AVCoders.CommunicationClients;

public record QueuedPayload<T>(DateTime Timestamp, T Payload);