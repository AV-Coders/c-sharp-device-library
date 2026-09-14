using AVCoders.Core;

namespace AVCoders.SignalR.SetTopBox;

/// <summary>
/// Snapshot of a set-top box pushed to connected UIs. Serialised by the SignalR JSON protocol
/// with camelCase property names and enums as their integer values, like the other hubs.
/// </summary>
/// <param name="Name">The group name the box is registered under.</param>
/// <param name="SourceId">The <c>SourceId</c> from the source hub this box belongs to, so a
/// panel can show a remote on the matching source tile.</param>
/// <param name="SupportedButtons"><see cref="AVCoders.MediaPlayer.RemoteButton"/> names the driver accepts.</param>
public record SetTopBoxState(
    string Name,
    string SourceId,
    string[] SupportedButtons,
    PowerState PowerState,
    PowerState DesiredPowerState,
    bool IsActiveSource,
    CommunicationState CommunicationState);
