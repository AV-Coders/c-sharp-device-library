# AVCoders.SignalR.Room

SignalR hub and bridge for room state. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.Room
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.SignalR.Room). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## What's inside

- `IRoomHub` / `RoomHub`
- `RoomUiSignalR` - registers a `RoomManager` with the hub and rebroadcasts its power, properties and link status
- `RoomManager` - wraps a `DeviceBase` for power, and holds the room's string property bag and link status
- `RoomLinkStatus` - whether the room can start a link, its `RoomLinkState` (`Unlinked`, `Leading`, `Following`) and its partner room's name

## Contract

Hub path convention: `/hubs/room`. UIs join a group by the manager's name, the same way as the source hub.

Client to server (`RoomHub`):

| Method | Notes |
| --- | --- |
| `JoinGroup(string groupName)` | Pushes `OnPowerStateChanged`, `OnPropertiesSnapshot` and `OnRoomLinkChanged` to the caller, in that order. |
| `GetGroups()` | Registered group names. |
| `PowerOn(string group)` / `PowerOff(string group)` | Forwarded to the wrapped device. |
| `PowerOnWithArgs(string group, Dictionary<string, string> args)` / `PowerOffWithArgs(...)` | Raise `RoomManager.OnPowerOnRequested` / `OnPowerOffRequested` with the args; the device isn't powered. `null` args arrive as an empty dictionary. |
| `LinkRooms(string group)` | Raises `RoomManager.OnLinkRoomsRequested` when the room supports linking, is `Unlinked` and its power is `On`. Otherwise it logs a warning with the room's link status and power, and does nothing. |
| `UnlinkRooms(string group)` | Raises `RoomManager.OnUnlinkRoomsRequested` when the room is `Leading` or `Following`, whether or not it supports linking. Otherwise it logs a warning with the same values, and does nothing. |

A call for an unknown group is dropped, and is reported as an ongoing `unknown-group:<group>` issue on `RoomHub.Connections`: see the `AVCoders.SignalR.Connections` README. `HubAuditFilter` records `LinkRooms` and `UnlinkRooms` as `Room.LinkRooms` and `Room.UnlinkRooms` against the group, like the other commands.

Server to client (`IRoomHub`):

| Method | Notes |
| --- | --- |
| `OnPowerStateChanged(PowerState state)` | Sent on join and whenever the device's power changes. |
| `OnPropertiesSnapshot(Dictionary<string, string> properties)` | Sent on join. |
| `OnPropertyChanged(string key, string value)` | Sent on every `RoomManager.SetProperty`. |
| `OnRoomLinkChanged(RoomLinkStatus link)` | Sent on join and whenever `SetLinkSupported`, `SetLinkPartner` or `SetLinkState` changes the status. Setting a value it already has sends nothing. |

Commands are fire-and-forget: the hub returns as soon as the work is queued, and the new state comes back through the callbacks.

## Room linking

The program owns the link between two rooms. `RoomManager` carries each room's side of it for UIs, and passes UI requests to the program:

- `SetLinkSupported(true)` on a room that can start a link, for example the room that leads a pair.
- `SetLinkPartner(name)` on both rooms, with the other room's name. It is sent in every state, so a UI can name the partner before linking.
- `SetLinkState(state)` on both rooms whenever the link changes, however it changed.
- Handle `OnLinkRoomsRequested` and `OnUnlinkRoomsRequested`, and report the result with `SetLinkState`.

A UI offers link while `supportsLinking` is true, `state` is `Unlinked` and power is `On`, and unlink while `state` is `Leading` or `Following`. A room with no partner keeps the starting status, `false`, `Unlinked` and `""`.

## Wire shape

`RoomLinkStatus` is serialised by the SignalR JSON protocol with camelCase names and the state as an integer (`0` Unlinked, `1` Leading, `2` Following):

```json
{
  "supportsLinking": true,
  "state": 1,
  "partner": "Room B"
}
```
