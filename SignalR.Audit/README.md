# AVCoders.SignalR.Audit

SignalR hub filter that records which touch panel triggered each state-changing hub call. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.Audit
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.SignalR.Audit). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Register

With `using AVCoders.SignalR.Audit;`:

```csharp
builder.Services.AddSignalR(options => options.AddFilter<HubAuditFilter>());
```

A program that already passes options adds `options.AddFilter<HubAuditFilter>();` inside its existing lambda. It is a global filter: it covers every hub the program maps, the `AVCoders.SignalR.*` hubs and the program's own. It needs no DI registration.

## If it isn't registered

Each `AVCoders.SignalR.*` hub checks `Actor.Current` on `JoinGroup`, on every command it dispatches and on every call for a group nothing is registered for. A call with no actor means the filter didn't run, and the hub's `HubConnections` (`DisplayHub.Connections` and the others, from `AVCoders.SignalR.Connections`) raises an ongoing Major issue under the key `no-actor`:

> PowerOn on DisplayHub arrived with no actor: the host hasn't registered HubAuditFilter, so this hub's calls aren't attributed or audited. Connection {connection id}, remote address {address}

- The method, connection id and remote address are those of the first call with no actor, the address as the connection reports it, or `none` when the connection has no HTTP context. Later calls leave the message as it is.
- There is one such issue per hub, however many connections or calls arrive with no actor.
- It resolves at the next call to that hub that carries an actor.
- A program's own hub derived from `GroupHub<T>` gets the check in `AddToGroup` and `ReportUnknownGroup`, and in each command that calls `ReportInvocation()`. An `AddToGroup` in its `OnConnectedAsync` is checked too: it passes when the filter is registered and raises the issue when it isn't.

## What it records

On every hub call, and on each connect and disconnect (`OnConnectedAsync`, `OnDisconnectedAsync`), it sets `Actor.Current` (from `AVCoders.Core`) to a `Panel` actor for the length of the call, so work a hub does on connect, such as an `AddToGroup` in its `OnConnectedAsync`, carries it too:

| Field | Source |
|---|---|
| `Ip` | the connection's remote address, IPv4-mapped addresses converted to IPv4 |
| `UserAgent` | the `User-Agent` header of the panel's connection request |
| `HostName` | reverse DNS of the IP via `HostNames.Get`: empty until the first lookup completes, then cached for 10 minutes |
| `ConnectionId` | the SignalR connection id |

It also starts a new trigger id (`Trigger.Start()`) on every hub call, `JoinGroup` and `Get` methods included, and on each connect and disconnect. The audit entry carries it as `TriggerId`. Connects and disconnects are not recorded.

The hubs run their commands on `Task.Run`, which carries `Actor.Current` and `Trigger.Id` along, so the hub's own log lines and the driver logs written while the command runs carry `Actor` and `TriggerId`.

For each state-changing call it then calls `Audit.Record`, before the hub method runs:

| Field | Value |
|---|---|
| `Target` | the first argument when it is a string other than a `pin` (the group / room name), otherwise the hub name |
| `Action` | hub class name without `Hub`, a dot, then the method: `Display.PowerOn`, `Volume.SetVolumeLevel` |
| `Detail` | the other arguments as `name=value`, space separated; dictionaries as their keys only (`args=[presenter, mode]`); a `pin` argument as `[redacted]` |

`JoinGroup` and every method whose name starts with `Get` are read-only and are not recorded. Every other method is recorded, including PIN checks such as `CheckPin`, with the PIN shown as `[redacted]`.

`Audit` keeps the last 1000 entries in memory (`Audit.GetEntries()`) and logs each one through `LogBase.LoggerFactory` under the `AVCoders.Audit` category, so entries reach whatever log servers the program's logger factory writes to.

Example entry for a panel at 10.0.0.21 switching the boardroom display to HDMI 2:

| Field | Value |
|---|---|
| `Target` | `Boardroom` |
| `Action` | `Display.SetInput` |
| `Detail` | `input=Hdmi2` |
| `Actor` | `Panel 10.0.0.21 (tsw-boardroom.corp.local)`, with user agent and connection id |
| `TriggerId` | `a3f9c2e1` |

## Reading the records

- A record means the call was requested. Hub commands are fire-and-forget: the record is written before the hub method runs, and the command can still be dropped (unknown group, bad argument, a later filter refusing it) or fail at the device.
- A failure the transport or driver reports later is written onto the record under the same trigger id, as an outcome: `Dropped`, `Rejected` or `NoReply` (`AuditEntry.Outcomes`; see the `AVCoders.Core` README, "Outcome"). An unknown group, a bad argument or a refusing filter writes none, and a record with no outcome means no failure was recorded.
- The IP identifies the panel, not a person. Panels send no user identity.
