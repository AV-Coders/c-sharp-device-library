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
- **`VolumeControl`** — volume/mute state for drivers that need it. `SupportsLevel` and `SupportsMute` (both `true` unless the driver overrides them) say which halves a control has; `RestoreLevel` skips a half it doesn't have. `SaveLevel` saves only what the device has reported: a level once a volume report has arrived, a mute state when it isn't `Unknown`; `RestoreLevel` sends nothing for a half that had no report.
- **`CommunicationClient`** — transport abstraction (concrete clients live in `AVCoders.CommunicationClients`), with `ConnectionReason` beside `ConnectionState`: see [Why a client isn't connected](#why-a-client-isnt-connected).
- **`SentCommandTracker<TCommand>`** — the commands a driver has sent, in send order, so each reply or error can be matched to the command it answers: see [Matching replies to sent commands](#matching-replies-to-sent-commands).
- **`ExtronSis`** — what Extron SIS error codes mean, shared by the Extron drivers in every package: `MeansNotSupported(error)` is true for `E10` (invalid command), `E13` (invalid parameter) and `E14` (not valid for this configuration). `Printable(command)` shows a command with its escape as `Esc ` and without its trailing carriage return.
- Shared enums, handler delegates and `ThreadWorker`.

## Logging

Wire a logger factory **once at startup**. Until you do, logging is silently discarded:

```csharp
LogBase.LoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
```

Any MEL provider works (Serilog via `SerilogLoggerFactory`, etc.). Each log line is scoped with the instance's `Class`, `InstanceName`, `InstanceUid` and the calling `Method`. Spans are emitted under `LogBase.ActivitySourceName` (`"AVCoders.Core"`) and are zero-cost when no listener is registered.

`LogException(e, message)` writes one Error line with the exception attached. When the exception has an inner exception, that line's message also carries the inner exception's type and message: `{Context} ({InnerExceptionType}: {InnerExceptionMessage})`. `LogException(e, level, message)` writes the same line at another level, such as Debug for an expected connection failure. Either way, the exception is added to `Errors`.

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for the full logging, tracing and diagnostics-buffer guide.

## Why a client isn't connected

`CommunicationClient.ConnectionReason` sits beside `ConnectionState`, which keeps its values and when they're set. It's null while nothing is wrong. Otherwise it holds:

- **`Category`** — `TimedOut`, `Refused`, `HostNotFound`, `Unreachable`, `CredentialsRejected`, `ClosedByDevice`, `Requested` or `Other`.
- **`Description`** — the text of the `connection` issue, such as "10.0.0.1:23 refused the connection".
- **`RawException`** — the exception's type and message, each inner exception after ` ---> `, or null when there was no exception.
- **`Since`** — when this category was first seen. A later failure with the same category keeps it.

`ConnectionReasonHandlers` is raised with the new reason, or null, each time it changes. A handler that throws is logged.

For transport writers:

- **`ReportConnectionFailure(exception)`** sets the reason and raises the `connection` issue, both from `ExplainConnectionError(exception)`. It maps the socket errors `TimedOut`, `ConnectionRefused`, `HostNotFound`, `HostUnreachable` and `NetworkUnreachable` to their categories and `ConnectionReset` to `ClosedByDevice`. A `TimeoutException` or `OperationCanceledException` (the client's own connect timeout) is `TimedOut`, an exception wrapping a `SocketException` takes the socket error's category, and anything else is `Other`. A transport overrides `ExplainConnectionError` for its own exceptions and falls back to the base. `DescribeConnectionError(exception)` returns just the description.
- **`ReportConnectionFailure(category, description, exception)`** is for a failure with its own category, such as an MQTT result code. `ReportConnectionFailure(description)` is `Other`.
- **`SetConnectionReason(category, description, exception)`** sets the reason without raising the issue, such as for a REST 401 while the client stays Connected.
- **`ClearConnectionFailures()`**, which the change to Connected calls, clears the reason.
- While `DesiredConnectionState` is Disconnected, the reason is `Requested` ("Disconnected on request") and failures don't replace it. Setting `DesiredConnectionState` to anything else clears `Requested`.
- **`MirrorConnectionReason(reason)`** copies another client's reason, for a client that wraps a transport.

## Matching replies to sent commands

`SentCommandTracker<TCommand>` keeps every command a driver sends, in send order, with the `Trigger.Id` it was sent under. `TCommand` is the driver's own description of a command, such as a record holding its text and the reply tag it expects. Construct one per connection with the reply timeout, and an optional `TimeProvider` (the real clock by default).

- **`Add(command)`** — record a command as it is sent. `Add(command, lateErrorWindow)` lets it take an error for that long after its normal reply, like a save that reports its failure later. Each `Add` moves the commands past their reply timeout to the missed list that `TakeMissed()` returns. The tracker holds at most `MaxCommands` (256) commands, the missed ones included. Above that, `Add` drops the oldest, a missed one first, and returns it, so the driver can log a Warning with the dropped command and `MaxCommands`. Otherwise it returns null.
- **`TakeReply(answers)`** — a reply arrived. Returns the oldest command still awaiting a reply for which `answers(command)` is true, and removes it. A command with a late-error window stays, but takes no second reply. Returns null when no command matches, such as for a verbose-mode change notice or a reply to another controller's command. Nothing is consumed then, and the driver decides how to log it.
- **`TakeError(canReceive)`** — an error arrived that names no command, such as a bare SIS `E13`, an NEC NULL message or a Philips NACK. Returns the oldest command still awaiting a reply for which `canReceive` is true (any command when it is null). If no command is awaiting a reply, it returns the oldest command in its late-error window instead. Returns null when nothing can take the error.
- **`TakeMissed()`** — removes and returns, in send order, every command that wasn't answered within the reply timeout. A command past its timeout takes no reply or error even before this is called. Call it from the poll so a missed reply is always reported. Late-error windows that have closed are dropped without being reported.
- **`IsAwaitingReply(matches)`** — whether a matching command is still awaiting its reply, to avoid sending a query twice.
- **`Clear()`** — forget every command, without reporting any as missed, such as on a reconnect.

Resume the returned `TriggerId` with `using (Trigger.Resume(sent.TriggerId))` so the reply's log lines join the action that sent the command. All members are thread-safe. The `answers`, `canReceive` and `matches` functions run under the tracker's lock, so keep them to comparisons on the command.

```csharp
private readonly SentCommandTracker<ExtronSisQuery> _sent = new(QueryReplyTimeout, timeProvider);

_sent.Add(query);
CommunicationClient.Send(query.Command);

if (_sent.TakeError() is { } rejected)
    using (Trigger.Resume(rejected.TriggerId))
        LogInformation("{Query} was rejected with {Error}", rejected.Command.Name, line);
```

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
