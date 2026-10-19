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

## What it records

On every hub call it sets `Actor.Current` (from `AVCoders.Core`) to a `Panel` actor for the length of the call:

| Field | Source |
|---|---|
| `Ip` | the connection's remote address, IPv4-mapped addresses converted to IPv4 |
| `UserAgent` | the `User-Agent` header of the panel's connection request |
| `HostName` | reverse DNS of the IP via `HostNames.Get`: empty until the first lookup completes, then cached for 10 minutes |
| `ConnectionId` | the SignalR connection id |

It also starts a new trigger id (`Trigger.Start()`) on every hub call, `JoinGroup` and `Get` methods included, and the audit entry carries it as `TriggerId`.

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
- The IP identifies the panel, not a person. Panels send no user identity.
