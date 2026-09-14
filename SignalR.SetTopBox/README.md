# AVCoders.SignalR.SetTopBox

SignalR hub and bridge for set-top box remote control: remote buttons, power and active-source state. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.SetTopBox
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.SignalR.SetTopBox). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## What's inside

- `ISetTopBoxHub` / `SetTopBoxHub`
- `SetTopBoxState` - name, source id, supported buttons, power, desired power, active source and comms state
- `SetTopBoxUiSignalR` - subscribes to a `SetTopBoxManager` and rebroadcasts state
- `SetTopBoxManager` - wraps an `AVCoders.MediaPlayer.MediaPlayer` that implements `ISetTopBox` (for example `AppleTvCec`)

## Contract

Hub path convention: `/hubs/settopbox`. UIs join a group by the manager's name, the same way as the source hub.

Client to server (`SetTopBoxHub`):

| Method | Notes |
| --- | --- |
| `JoinGroup(string groupName)` | Same name and signature as `SourceHub`; pushes the current `SetTopBoxState` to the caller. |
| `SendRemoteButton(string group, string button)` | `button` is a `RemoteButton` name, matched case-insensitively. Unknown or unsupported buttons are logged and dropped. |
| `PowerOn(string group)` / `PowerOff(string group)` | |
| `GetState(string group)` | Returns the current `SetTopBoxState`, or `null` for an unknown group. |
| `GetGroups()` | Registered group names. |

Server to client (`ISetTopBoxHub`):

| Method | Notes |
| --- | --- |
| `UpdateSetTopBox(SetTopBoxState state)` | Sent on join and whenever power, desired power, comms or active-source state changes. |

Commands are fire-and-forget: the hub returns as soon as the work is queued, and the new state comes back through `UpdateSetTopBox`.

## Wire shape

`SetTopBoxState` is serialised by the SignalR JSON protocol with camelCase names and enums as integers (`PowerState` and `CommunicationState` from `AVCoders.Core`):

```json
{
  "name": "Boardroom",
  "sourceId": "AppleTv",
  "supportedButtons": ["Enter", "Up", "Down", "Left", "Right", "Back", "Home", "Play", "Pause", "PowerOn", "PowerOff"],
  "powerState": 1,
  "desiredPowerState": 1,
  "isActiveSource": true,
  "communicationState": 1
}
```

`sourceId` is the `SourceId` of the matching entry in the source hub, so a panel can show a remote on that source's tile.

## Usage

```csharp
// Program / control-system startup
new SetTopBoxUiSignalR(new SetTopBoxManager(zoneName, appleTv, "AppleTv"), SignalRProgram.SetTopBoxHubContext!);
```

```csharp
// Web host
app.MapHub<SetTopBoxHub>("/hubs/settopbox");
```

`SetTopBoxManager` throws `ArgumentException` if the device does not implement `ISetTopBox`. Active-source reporting comes from `AppleTvCec.IsActiveSource`; other drivers report `false`.
