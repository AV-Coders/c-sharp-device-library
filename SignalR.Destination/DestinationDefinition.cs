using AVCoders.SignalR.Source;

namespace AVCoders.SignalR.Destination;

public record DestinationDefinition(
    string Name,
    string DestinationId,
    string Icon,
    SourceDefinition CurrentSource,
    bool VideoMute);
