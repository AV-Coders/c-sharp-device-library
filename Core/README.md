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
- **`CommunicationClient`** — transport abstraction (concrete clients live in `AVCoders.CommunicationClients`), with `ConnectionReason` beside `ConnectionState`: see [Why a client isn't connected](#why-a-client-isnt-connected). `MaskInRequests(text, shownAs)` keeps a secret a driver sends, such as a password, out of the logs and the comms console: wherever a request carries `text`, the Verbose `Sending` lines and the string and byte request handlers get `shownAs` in its place, and the device still gets `text`. Byte requests are matched as Latin-1 text, the encoding `Send(string)` uses. An empty `text` masks nothing.
- **`SentCommandTracker<TCommand>`** — the commands a driver has sent, in send order, so each reply or error can be matched to the command it answers: see [Matching replies to sent commands](#matching-replies-to-sent-commands).
- **`ExtronSis`** — what Extron SIS error codes mean, shared by the Extron drivers in every package: `MeansNotSupported(error)` is true for `E10` (invalid command), `E13` (invalid parameter) and `E14` (not valid for this configuration). `Printable(command)` shows a command with its escape as `Esc ` and without its trailing carriage return.
- **`Bytes`** — byte helpers. `Escaped(bytes, maxShown)` shows bytes a device sent, for an issue or a log: printable ASCII as it is, every other byte as `\xHH`, at most `maxShown` bytes, with `...` after a cut.
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
- **`TimeUntilNextMiss()`** — how long until the oldest command awaiting a reply misses its reply timeout. Zero when a missed command is waiting for `TakeMissed()`, null when no command awaits a reply. A driver with no poll arms a timer with it after each `Add` and after each `TakeMissed()`, so a missed reply is reported when it happens.
- **`IsAwaitingReply(matches)`** — whether a matching command is still awaiting its reply, to avoid sending a query twice.
- **`Clear()`** — forget every command, without reporting any as missed, such as on a reconnect.

Resume the returned `TriggerId` with `using (Trigger.Resume(sent.TriggerId))` so the reply's log lines join the action that sent the command. Pass the `TriggerId` of each `TakeMissed()` result and each rejected command to `Audit.RecordOutcome`, so the audit entry of the action that sent it shows the failure: see [Outcome](#outcome-what-happened-to-a-recorded-command). All members are thread-safe. The `answers`, `canReceive` and `matches` functions run under the tracker's lock, so keep them to comparisons on the command.

```csharp
private readonly SentCommandTracker<ExtronSisQuery> _sent = new(QueryReplyTimeout, timeProvider);

_sent.Add(query);
CommunicationClient.Send(query.Command);

if (_sent.TakeError() is { } rejected)
{
    Audit.RecordOutcome(rejected.TriggerId, AuditOutcomeKind.Rejected, Name, $"{rejected.Command.Name} answered {line}");
    using (Trigger.Resume(rejected.TriggerId))
        LogInformation("{Query} was rejected with {Error}", rejected.Command.Name, line);
}
```

## Audit: who triggered an action

- **`Actor`** — who or what triggered the current action. `Kind` is one of:
  - a person, or a system relaying a person's press: `Browser`, `Mcp`, `Panel`, `Console` (a command typed at the processor console) and `Peer` (another processor or control system sending over a TCP server). `IsPerson` is true for these five.
  - the program's own account: `Schedule` (a timed action, such as a daily shutdown) and `System` (a part of the program, such as a worker loop or a client's event handler). `IsPerson` is false.
- **Its fields** — `Ip`, `UserAgent`, `HostName` (null until known), `ConnectionId` and `Name` (which schedule, which part of the program or which console command; empty for the other kinds).
- **Building one** — `new Actor(kind, ip, userAgent, hostName, connectionId)` for a browser, an MCP call or a panel. `Actor.Schedule(name)`, `Actor.System(name)`, `Actor.Console(command)` and `Actor.Peer(ip, hostName, connectionId)` for the others. A Crestron console command handler receives only its argument text, with nothing that identifies the session, so a `Console` actor carries the command name and the arguments belong in the audit detail.
- **`ToString()`** — `Browser 10.1.2.3 (LAPTOP-12)`, or `Mcp 10.1.2.3` without a host name, and the same for `Panel` and `Peer`. `Schedule DailyShutdown`, `System AvCodersTcpClient.Receive (Projector)` and `Console dumpheap` for the named kinds.
- **`Actor.Current`** — the ambient actor, held in an `AsyncLocal`. Set it where a request enters (a Blazor circuit, an MCP tool call, a SignalR hub call, a schedule, a console command) and it flows through the awaits of that call. A synchronous callback on a thread that outlives the call, such as a Crestron SDK event, uses `using (Actor.Begin(actor))`, which restores the previous actor on dispose. A null `Actor.Current` means no entry point set one.
- **Where the library sets an actor** — each `ThreadWorker` pass runs as `Actor.System(<worker name>)`, such as `System AvCodersTcpClient.Receive (Projector)`, whatever actor called `Restart()`, so polling, receive and send-queue loops are never attributed to whoever called `Connect()`. MQTT message handling, SSH.NET's error event, the C-Bus health and confirmation timers and the Cisco phonebook search timeout run as `Actor.System(<client or driver name>)`, and `ScreenTechnicsConnect`'s disconnect after a move as `Actor.System(<screen name>)`. `AvCodersTcpServer` runs each read from a connected client as `Actor.Peer(<its remote address>, HostNames.Get(<address>), <client id>)`, so a command another control system relays counts as a person's.
- **What depends on it** — only a send made by a person (`IsPerson`) reconnects a client a person disconnected on request: see the `AVCoders.CommunicationClients` README, "Reconnect on the next send a person makes".
- **`Actor`** (`LogBase.ActorProperty`) — while `Actor.Current` is set, every `LogBase` log scope, tracing span and `Events` entry carries `Actor` = `Actor.Current.ToString()`. Log lines written inside a worker pass carry the worker's `System` actor, including a driver's reply lines resumed under the trigger id of the command they answer.
- **`Audit.Record(target, action, detail)`** — records an `AuditEntry` (timestamp, `Actor.Current`, `Trigger.Id`, target, action, detail, and `Outcomes`, empty until a failure is written back). The last `Audit.EntryLimit` (1000) entries are kept in memory and returned oldest first by `Audit.GetEntries()`. There is no clear. Each entry is also logged at Information under the `Audit.Category` (`"AVCoders.Audit"`) logger, so remote log servers receive it. For an actor with a `Name` (Schedule, System, Console) the line is `{AuditTarget}: {AuditAction} {AuditDetail} by {ActorKind} {ActorName} {TriggerId}`. For any other, or no actor, it is `{AuditTarget}: {AuditAction} {AuditDetail} by {ActorKind} {ClientIp} {ClientHostName} {ClientUserAgent} {ClientConnectionId} {TriggerId}`, with `ActorKind` `Unknown` when there is no actor. If the host raises the minimum level for `AVCoders.*`, keep `AVCoders.Audit` at Information.
- **`Audit.Recorded`** — raised after each entry is stored, on the thread that recorded it. Each subscriber runs in its own try/catch; a throwing subscriber is logged as a Warning and the others still run. A subscriber must not block: it runs on the entry point's thread, and for `Updated` (below) on comms workers and inside a client's connect and disconnect, so a page posts its redraw and returns.
- **`HostNames.Get(IPAddress)`** — the reverse-DNS name for an address, or null. It never blocks: a miss returns null and starts one background lookup, so the name is there from a later call. Lookups run one at a time. Results, failures included, are cached for 10 minutes, since DHCP can hand the address to another machine; after that the last known name is returned while a fresh lookup runs. Call it each time you build an `Actor`.

```csharp
Actor.Current = new Actor(ActorKind.Browser, ip.ToString(), userAgent, HostNames.Get(ip), circuitId);
Audit.Record("Projector", "Power", "On");

using (Actor.Begin(Actor.Schedule("DailyShutdown")))
using (Trigger.Begin())
{
    Audit.Record("Boardroom", "Shut down");
    room.PowerOff();
}
```

### Outcome: what happened to a recorded command

A command can fail after its entry is recorded. `Audit.RecordOutcome(triggerId, kind, source, detail)` adds an `AuditOutcome(Timestamp, Kind, Source, Detail)` to `Outcomes` on every entry recorded within the last `Audit.OutcomeWindow` (10 minutes) that carries `triggerId`. `Kind` is `Dropped` (never sent), `Rejected` (the device answered with an error) or `NoReply` (no answer within the driver's reply timeout). `Source` is the client's or driver's `Name`, and `Detail` the text the client or driver logs for it.

- Only failures are written. An entry with no outcome means no failure was recorded, not that the device carried the command out: drivers that don't use `SentCommandTracker` write no `Rejected` or `NoReply`.
- An entry keeps its latest `Audit.OutcomeLimit` (20) outcomes.
- A null trigger id, or one that no entry in the window carries, writes nothing. An entry recorded after the command was sent, rather than before, has no outcome for a failure that happened before it was recorded.
- The window covers the longest wait the library has before it reports a failure: `SonyVisca` reports a move or preset that never completes 5 minutes after the command, at its next poll. Trigger ids are 8 hex characters, and a failed poll also calls `RecordOutcome` with its own id, so matching only recent entries keeps a poll's id from landing on an older entry that happens to carry the same id.
- `Audit.Updated` is raised once for each entry written to, with the new entry, on the thread that wrote the outcome: a comms worker, a driver's receive or poll pass, or a client's `Connect()`/`Disconnect()`. Entries are immutable records, so a page redraws from `Audit.GetEntries()`. Subscribers are guarded as for `Recorded` and must not block.
- `RecordOutcome` shows every control character in a detail as `\xHH`, whatever the source, so a command or reply with its terminator stays one visible line in the entry and in the Warning log line.
- Each write is logged at Warning under `AVCoders.Audit`: `{AuditTarget}: {AuditAction} {AuditOutcome} at {AuditOutcomeSource}: {AuditOutcomeDetail} {TriggerId}`.

Where the library writes them:

- The TCP, UDP and SSH clients write `Dropped` with the payload masked by `MaskInRequests` and escaped by `Bytes.Escaped`: printable ASCII as it is, every other byte as `\xHH`, so `PWR ON\r\n` reads `PWR ON\x0D\x0A` and a detail is always one visible line. TCP and UDP escape the bytes they send (Latin-1). SSH escapes the bytes it writes: a byte send's own bytes, otherwise the UTF-8 bytes of the text. A byte send with no text is shown as dash-separated hex. The payload follows one of these reasons: "Expired in the send queue after N s, timeout T s", "Dropped as the oldest in a full send queue of N" (N is `MaxQueueSize`, 100 by default), "Cleared from the send queue by a connect request", "Cleared from the send queue by a disconnect request", and "Not sent, the client was disconnected on request" for a send while disconnected on request that doesn't reconnect the client. A transport calls `RecordDroppedSend(triggerId, reason, message)` or `RecordDroppedSend(triggerId, reason, bytes, message)`.
- A queued send is dropped at its timeout whether or not the client is connected. While the client is offline, its send worker checks the queue on every pass (each second for TCP and SSH, each half second for UDP), so the drop's Warning and its outcome appear within a pass of the timeout, not at the next connect. A send that waits for a person's reconnect keeps its 15 s.
- `QsysQrc` writes `Dropped` for a set it held that is older than the client keeps a queued send when it is released, naming what it waited for: "Expired while held for the logon after N s, timeout T s: <control> <fields as JSON>", or "... held for the Core's component list ..." for a control name the component list has to split.
- These drivers write `NoReply` for each `SentCommandTracker.TakeMissed()` result, and `Rejected` for each `TakeError` result and each error reply matched to a sent command: `SonyVisca`, `NecUhdExternalControl`, `PhilipsSICP`, `QsysQrc`, `YamahaXmv`, `GlobalCacheContactClosure`, the `ExtronSisMatrix` drivers, `Navigator` and `ExtronSmp351`. No other driver writes `Rejected` or `NoReply`: `LGCommercial` and `SamsungMdc` track only their poll reads, and the rest track nothing. For a command to any other device, an entry with no outcome means no failure was recorded.

### Trigger id: which action

`Trigger.Id` is the current action's id, 8 lowercase hex characters (`a3f9c2e1`), held in an `AsyncLocal` separate from `Actor.Current`: the actor is who, the trigger is which action, and a flow a device starts has an id and the actor of whatever received it: the worker's `System` actor, `Actor.System(<client name>)` for an MQTT message, or `Actor.Peer(...)` for a TCP server read. `Trigger.Start()` sets a new id and returns it. Synchronous callbacks on threads that outlive the call, such as Crestron SDK event threads, use `using (Trigger.Begin())`, which starts a new id and restores the previous one on dispose. Each `ThreadWorker` pass starts its own, so every poll cycle, reconnect attempt and received chunk has one; browser events, MCP tool calls and SignalR hub calls start theirs in the UI and SignalR packages. `Audit.Record` stamps the current id and never starts one. Work that runs later on another thread, such as a queued command when it is sent or a device reply matched to the command that caused it, resumes the id it was given with `using (Trigger.Resume(id)) { ... }`, which restores the previous id on dispose. A null id, from a command sent outside any trigger such as at program startup, keeps the current id, so a matched reply stays under the receive's id. While an id is set, every `LogBase` log scope, tracing span and `Events` entry carries `TriggerId` (`LogBase.TriggerIdProperty`), next to `Actor`.
