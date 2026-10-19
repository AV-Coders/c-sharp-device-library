# AVCoders.Core

Foundation for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library) — every driver package builds on it. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Core
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Core). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## What's inside

- **`LogBase`** — logging through the `Microsoft.Extensions.Logging` (MEL) abstraction (no hard dependency on any logging framework), an in-memory ring buffer of recent `Events`/`Errors`, and a static `ActivitySource` for tracing.
- **`DeviceBase`** — power and communication state, implements `IDevice`.
- **`VolumeControl`** — volume/mute state for drivers that need it. `SupportsLevel` and `SupportsMute` (both `true` unless the driver overrides them) say which halves a control has; `RestoreLevel` skips a half it doesn't have.
- **`CommunicationClient`** — transport abstraction (concrete clients live in `AVCoders.CommunicationClients`).
- Shared enums, handler delegates and `ThreadWorker`.

## Logging

Wire a logger factory **once at startup**. Until you do, logging is silently discarded:

```csharp
LogBase.LoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
```

Any MEL provider works (Serilog via `SerilogLoggerFactory`, etc.). Each log line is scoped with the instance's `Class`, `InstanceName`, `InstanceUid` and the calling `Method`. Spans are emitted under `LogBase.ActivitySourceName` (`"AVCoders.Core"`) and are zero-cost when no listener is registered.

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for the full logging, tracing and diagnostics-buffer guide.

## Audit: who triggered an action

- **`Actor`** — who triggered the current action: `Kind` (`Browser`, `Mcp`, `Panel`), `Ip`, `UserAgent`, `HostName` (null until known) and `ConnectionId`. `ToString()` gives `Browser 10.1.2.3 (LAPTOP-12)`, or `Mcp 10.1.2.3` without a host name.
- **`Actor.Current`** — the ambient actor, held in an `AsyncLocal`. Set it where a request enters (a Blazor circuit, an MCP tool call, a SignalR hub call) and it flows through the awaits of that call. `ThreadWorker` loops never inherit it, so polling and receive loops are not attributed to whoever called `Connect()`.
- **`Actor`** (`LogBase.ActorProperty`) — while `Actor.Current` is set, every `LogBase` log scope, tracing span and `Events` entry carries `Actor` = `Actor.Current.ToString()`.
- **`Audit.Record(target, action, detail)`** — records an `AuditEntry` (timestamp, `Actor.Current`, `Trigger.Id`, target, action, detail). The last `Audit.EntryLimit` (1000) entries are kept in memory and returned oldest first by `Audit.GetEntries()`. There is no clear. Each entry is also logged at Information under the `Audit.Category` (`"AVCoders.Audit"`) logger, with the structured properties `AuditTarget`, `AuditAction`, `AuditDetail`, `ActorKind` (`Unknown` without an actor), `ClientIp`, `ClientHostName`, `ClientUserAgent`, `ClientConnectionId` and `TriggerId`, so remote log servers receive it. If the host raises the minimum level for `AVCoders.*`, keep `AVCoders.Audit` at Information.
- **`Audit.Recorded`** — raised after each entry is stored. Each subscriber runs in its own try/catch; a throwing subscriber is logged as a Warning and the others still run.
- **`HostNames.Get(IPAddress)`** — the reverse-DNS name for an address, or null. It never blocks: a miss returns null and starts one background lookup, so the name is there from a later call. Lookups run one at a time. Results, failures included, are cached for 10 minutes, since DHCP can hand the address to another machine; after that the last known name is returned while a fresh lookup runs. Call it each time you build an `Actor`.

```csharp
Actor.Current = new Actor(ActorKind.Browser, ip.ToString(), userAgent, HostNames.Get(ip), circuitId);
Audit.Record("Projector", "Power", "On");
```

### Trigger id: which action

`Trigger.Id` is the current action's id, 8 lowercase hex characters (`a3f9c2e1`), held in an `AsyncLocal` separate from `Actor.Current`: the actor is who, the trigger is which action, and a flow a device starts has an id but no actor. `Trigger.Start()` sets a new id and returns it. Synchronous callbacks on threads that outlive the call, such as Crestron SDK event threads, use `using (Trigger.Begin())`, which starts a new id and restores the previous one on dispose. Each `ThreadWorker` pass starts its own, so every poll cycle, reconnect attempt and received chunk has one; browser events, MCP tool calls and SignalR hub calls start theirs in the UI and SignalR packages. `Audit.Record` stamps the current id and never starts one. Work that runs later on another thread, such as a queued command when it is sent or a device reply matched to the command that caused it, resumes the id it was given with `using (Trigger.Resume(id)) { ... }`, which restores the previous id on dispose. A null id, from a command sent outside any trigger such as at program startup, keeps the current id, so a matched reply stays under the receive's id. While an id is set, every `LogBase` log scope, tracing span and `Events` entry carries `TriggerId` (`LogBase.TriggerIdProperty`), next to `Actor`.
