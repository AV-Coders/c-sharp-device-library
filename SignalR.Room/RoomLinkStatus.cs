namespace AVCoders.SignalR.Room;

public enum RoomLinkState
{
    Unlinked,
    Leading,
    Following
}

public record RoomLinkStatus(bool SupportsLinking, RoomLinkState State, string Partner);
