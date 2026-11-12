# AVCoders.SignalR.Connections

Connection tracking and unknown-group reporting shared by the AV Coders SignalR hubs. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.Connections
```

The `AVCoders.SignalR.*` hub packages depend on it, so a program that uses one of them already has it.

## What's inside

- `HubConnections`: one per hub, a `LogBase` named after the hub. It holds the open connections, the groups each one has joined, the registered groups and the recent unknown-group requests.
- `GroupHub<T>`: the base of every library hub. It opens a connection in `HubConnections` on connect and closes it on disconnect. `AddToGroup(group)` joins the SignalR group and counts the connection in it. `ReportUnknownGroup(group)` reports a request for a group nothing is registered for, under the calling method's name.
- `HubConnectionsSnapshot`, `GroupConnections` and `UnknownGroupRequest`: the state for a health page.

## Reading connections

Every library hub exposes its tracker as a static `Connections`: `SourceHub.Connections`, `VolumeHub.Connections`, `RoomHub.Connections`, `DestinationHub.Connections`, `DisplayHub.Connections`, `CameraHub.Connections` and `SetTopBoxHub.Connections`.

```csharp
var openPages = RoomHub.Connections.Count("L1-01");

RoomHub.Connections.Changed += (_, _) => UpdateControlSurfaces();

var health = RoomHub.Connections.GetSnapshot();
```

- `Count(group)` is the number of open connections that have joined the group.
- `Changed` is a plain `EventHandler`. It fires after every open, join, close, registration and unknown-group request, on the SignalR thread that made the change, so two changes can arrive out of order: a handler reads `Count(group)` or `GetSnapshot()` for the current state. A handler that throws is logged and the rest still run.
- `GetSnapshot()` gives the hub name, `OpenConnections` (every open connection, joined or not), `Groups` (every registered group, plus any group a connection joined that has nothing registered, in ordinal order, each with `Registered` and its `Connections`) and `UnknownGroupRequests` (the last 50, oldest first, each with when, the hub, the method, the connection id and the group exactly as received).
- Every `HubConnections` is in `LogBaseRegistry`, so `LogBaseRegistry.GetAll().OfType<HubConnections>()` lists every hub's tracker.

A connection counts in a group from `JoinGroup` until it disconnects. A connection can join several groups. A room page that opens the Source, Volume and Room hubs is three connections, one on each hub. A `JoinGroup` that finishes after its connection has closed (SignalR can run `OnDisconnectedAsync` while a call is still in flight) isn't counted.

## Unknown groups

When `JoinGroup` or a command names a group nothing is registered for, the caller sees what it always has: `JoinGroup` still adds the connection to the SignalR group and sends no state, commands are dropped, `SetTopBoxHub.GetState` returns `null`, and no call fails. The hub also reports it:

- It is logged at Warning with the method, the connection id, the hub, the group and the registered groups. `SetTopBoxHub` also keeps its own "{Method} ignored: no set-top box registered for group {Group}" warning.
- While the asking connection is open, the hub raises an ongoing Major issue under the key `unknown-group:<group>`: "{Method} from connection {id} asked for group '{group}', which has nothing registered. Registered groups: {groups}". The registry carries it to health pages and the management interface's Issues page. It resolves when every open connection that asked for the group has closed, or when the group is registered. A request from a connection that has already closed is logged and recorded but raises no issue.
- The request is added to the snapshot's `UnknownGroupRequests`, and a connection that joined the group counts in it with `Registered` false.

## A program's own hub

A hub in a program can use the same tracking:

```csharp
public class KeypadHub() : GroupHub<IKeypadHub>(Connections)
{
    public static HubConnections Connections { get; } = new(nameof(KeypadHub));

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        await AddToGroup(RoomFromPath());
    }
}
```

Register the groups it serves with `Connections.Register(group)` and call `ReportUnknownGroup(group)` for any other.

## Logging

The tracker's own lines (the unknown-group warning, and opens, joins, closes and registrations at Debug) go through a logger resolved from `LogBase.LoggerFactory` at each call, like the hubs' own lines. Two kinds of line go through the `LogBase` logger, which `HubConnections` takes when it is created, the first time the hub type is used: the issue's own Warning line and a `Changed` handler's exception. Set `LogBase.LoggerFactory` before mapping the hubs or registering managers so those reach the log too.
