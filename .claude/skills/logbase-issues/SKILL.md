---
name: logbase-issues
description: How the LogBase Issue/incident system works — statuses, severity, escalation, the registry, and connection-failure reporting. Use when raising/resolving issues in a driver, consuming IssuesChanged, working on dashboards or ticketing integrations, or modifying the issue system itself.
---

# The LogBase issues feature

> **Maintenance note: this skill mirrors the code. If you change the issues feature
> (Core\Issue.cs, the issue members of Core\LogBase.cs, Core\LogBaseRegistry.cs, or the
> connection-failure members of Core\CommunicationClient.cs), update this skill in the same
> change.** Introduced in commits `c5400b9` (issue system, replacing ActiveErrors) and
> `ca51f00` (connection-failure reporting).

## Model (`Core\Issue.cs`, namespace `AVCoders.Core`)

```csharp
enum IssueStatus { Ongoing, Momentary, Resolved }
enum IssueSeverity { Minor, Major, Critical }
enum IssueChangeKind { Raised, Updated, Resolved, Trimmed }

record Issue(Guid Id, string Key, string Message, IssueStatus Status, IssueSeverity Severity,
    DateTimeOffset RaisedAt, DateTimeOffset LastRaisedAt, int OccurrenceCount,
    DateTimeOffset? ResolvedAt);

class IssuesChangedEventArgs : EventArgs   // ChangedIssue (null only for Trimmed), Kind, Issues snapshot
```

- **Ongoing** — affecting the device now; stays until the driver calls `ResolveIssue(key)`.
- **Momentary** — a one-off incident (e.g. one unanswered poll). Instantly historical,
  never appears in `GetOngoingIssues()`. There is **no TTL** — nothing expires.
- **Resolved** — a recovered ongoing issue, kept as history with `ResolvedAt` set.
- `Id` is assigned once and survives message/severity updates, coalescing and resolution.
  External correlation (tickets, UI dictionaries) must key on `Id` — `Issue` is a record
  whose instances are replaced on every update. Re-raise after resolve = new entry, new Id.

## Per-instance API (every LogBase — devices, comm clients, ThreadWorkers, …)

Protected (drivers call these; all safe to call every poll cycle):

- `RaiseMomentaryIssue(message, key = message, severity = Minor, escalateAfter = null)` —
  if the latest entry for the key is Momentary it **coalesces** (Message updated,
  `OccurrenceCount`+1, `LastRaisedAt` refreshed) instead of appending. Coalescing is
  key-based, not message-based (messages often vary per occurrence).
- `RaiseOngoingIssue(key, message, severity = Major)` — no-op if an Ongoing entry with the
  same key/message/severity exists; message/severity changes update in place (same Id,
  original RaisedAt).
- `ResolveIssue(key)` — transitions the Ongoing entry to Resolved (kept, `ResolvedAt` set),
  resets the escalation counter for the key, silent no-op otherwise. Call it on **every
  successful response** — that is what resets escalation.

Public: `GetIssues()` (full bounded history, oldest first), `GetOngoingIssues()`,
`SetIssueLimit(int)` (default cap 50), `event EventHandler<IssuesChangedEventArgs> IssuesChanged`.
The getters are **methods, not properties** (SonarCloud S2365 — the snapshot cost must be
visible at the call site) and return an immutable snapshot cached until the next mutation,
so repeated calls between changes return the same instance and are allocation-free.
`RoomManager.GetProperties()` in SignalR.Room follows the same convention.

### Escalation (the flapping-gap fix)

Momentary issues are instantly historical, so a device failing *every* poll would show zero
ongoing issues. `escalateAfter: n` fixes this: after `n` **consecutive** momentary raises of
a key with no intervening `ResolveIssue(key)`, an Ongoing entry is raised under the same key
with message `"{message} ({n} consecutive occurrences)"` and severity **one level higher**
(capped at Critical). The consecutive counter is separate from `OccurrenceCount` (which is
cumulative and never resets) and lives in `_consecutiveMomentary`, **capped at 200 keys**
(oldest evicted on insert; protects against message-as-key raises growing it forever).

### Change kinds and eventing

Every mutation fires `IssuesChanged` with the affected issue + kind: `Raised` (new entry,
incl. escalation-created ongoing entries — escalation fires Updated-then-Raised), `Updated`
(message/severity change or momentary coalesce), `Resolved`, `Trimmed` (cap eviction via
`SetIssueLimit`; ChangedIssue is null). Idempotent re-raises fire nothing. Subscribers are
invoked individually and guarded — one throwing subscriber is logged to the instance's
`Errors` buffer and the rest still run. Events fire synchronously on driver comm/poll
threads: UI consumers must marshal (Blazor `InvokeAsync`).

Cap eviction prefers historical entries (Momentary/Resolved) over Ongoing, oldest first,
never the just-raised entry. Do not treat the issue history as durable storage.

Raising/resolving also writes one `EventType.Error` entry to the separate `Events` buffer.
The `Errors`/`LogException` buffer is unrelated (exception history) — do not confuse them.

## Design intent (user-stated, not derivable from code)

- **Ongoing issues become tickets in an external ticketing system**, wired **per device**
  via each instance's `IssuesChanged` handler: `Kind == Raised && Status == Ongoing` → open
  ticket keyed by `Id`; `Kind == Resolved` → close it. Devices differ, so integrations
  subscribe per instance — not via the registry.
- **The registry is dashboards-only** (aggregate "what's wrong right now" view).
- **Severity, not category** — an EventType-style category was explicitly rejected
  ("different concepts"). Three statuses only; acknowledgement/suppression live in the
  ticketing system. If ack is ever needed: nullable AcknowledgedAt/By fields, NOT a fourth
  status (consumers' exhaustive switches).

## Registry (`Core\LogBaseRegistry.cs`)

Every LogBase **auto-registers in its constructor** and is rooted for process life.
Transiently-created inheritors must call `LogBaseRegistry.Deregister` on teardown (the
Extron matrix drivers do this via `ExtronMatrixEndpointListExtensions.DeregisterAndClear`
when rebuilding their SyncStatus endpoint lists — keep that pattern for any new runtime
list-rebuild).

- `GetOngoingIssues()` → `IReadOnlyList<SourcedIssue>` (`record SourcedIssue(LogBase Source, Issue Issue)`).
- `static event OngoingIssuesChanged` (`OngoingIssuesChangedEventArgs.OngoingIssues` =
  aggregate snapshot; sender = originating instance). Fires only for changes that can affect
  the ongoing set — **momentary-only changes are filtered out**. Registry subscriber
  exceptions are swallowed; handle your own.
- Fan-outs: `SetIssueLimits`, plus the pre-existing ClearEvents/ClearErrors/Set*Limits.

## Connection-failure reporting (`Core\CommunicationClient.cs`)

- `protected ReportConnectionFailure(reason)` — momentary issue under key `"connection"`
  (const `ConnectionIssueKey`); once failures have persisted for
  `ConnectionIssueThreshold` (public, default 2 min), raises a **Critical Ongoing** issue
  ("Unable to connect since HH:mm:ss. {reason}"). The `ConnectionState` setter auto-resolves
  it and resets the clock on the Connected transition.
- `protected DescribeConnectionError(Exception)` — human-readable reasons: SocketException
  codes (timed out / host not found / refused / unreachable / reset), HttpRequestException
  (unwraps inner SocketException), OperationCanceledException → "connection attempt timed
  out", fallback to `e.Message`.
- **Wired**: AvCodersTcpClient (connect-loop catches; shutdown cancellations excluded via
  `when (token.IsCancellationRequested)`), AvCodersSshClient (SSH-specific reasons incl.
  rejected auth), AvCodersMqttClient (initial + reconnect loop), AvCodersRestClient (per
  request), AvCodersSnmpV3Client (Get/Set/Walk catches; SNMP ErrorStatus paths deliberately
  do NOT report — the device responded), NavigatorTunnel (reports in `SetConnectionState`
  when set to Disconnected/Error — NavDeviceBase calls it every failing 90 s poll cycle).
- **Not wired (deliberate)**: UDP, multicast, Wake-on-LAN (fire-and-forget), AvCodersTcpServer (inbound).
- **crestron-components repo** (`C:\code\AV-Coders\crestron-components\CommunicationClients`):
  CrestronGenericDeviceCommunicationClient repurposes its ConnectionStateWorker as a 30 s
  loop reporting "{Name} is offline" while not Connected (online/offline events fire only
  once per transition, so a loop is needed for the threshold escalation).
  AvCodersSerialClient and AvCodersIrAsSerialClient raise a Critical **ongoing** issue
  directly on com/IR port registration failure — registration is one-shot with no retry, so
  the momentary/threshold path would never escalate. CrestronCecStream and
  CrestronMulticastClient are deliberately not wired.

## Driver examples to copy

- `Dsp\BiampTtp.cs` — `RaiseMomentaryIssue(..., key: "unanswered-query", escalateAfter: 3)`
  per missed poll; `ResolveIssue` where the response is matched.
- `Display\SamsungMDC.cs` — pending-flag pattern for unanswered volume polls.
- `Core\DeviceBase.cs` / `Display\Display.cs` — persistent-condition pattern
  (`RaiseOngoingIssue`/`ResolveIssue` from state setters; keys `PowerStateIssueKey`,
  `CommunicationIssueKey`, `InputIssueKey`; communication uses Critical).

## Tests

`CoreTest\IssuesTest.cs`, `CoreTest\LogBaseRegistryTest.cs` (both in xUnit collection
`"LogBaseIssues"` — registry fan-outs mutate global static state, so these must not run in
parallel with each other; registry tests assert containment/filter-by-Source, **never
global counts**), `CoreTest\ConnectionIssuesTest.cs`, `MatrixTest\NavigatorTunnelTest.cs`.
Convention: nested `TestLogBase` subclass exposing the protected raise methods;
`Method_Scenario_Expected` naming.

## Known trade-offs

- Registry roots every instance; the auto-registration + event snapshot means the
  "no subscribers" fast path in `RaiseIssuesChanged` never triggers — each raise allocates
  a small snapshot (Gen0 churn only, a few KB/s per continuously-failing device).
- An eviction from the 200-key consecutive counter can delay (never prevent) escalation of
  a hot key if 200 distinct momentary keys flood in mid-count.
- Full old-API → new-API mapping for consumers is in `MIGRATION.md`; consumer-facing docs
  in `README.md` ("Issues" section).
