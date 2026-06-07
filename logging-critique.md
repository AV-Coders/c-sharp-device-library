# Logging Critique — AV-Coders c-sharp-device-library

Critique of how logging is done across the solution (captured 2026-06-02).

## How logging works today

Two parallel systems live in `Core/LogBase.cs`:

1. **Serilog static API** — `Log.Verbose/Debug/Information/Warning/Error(...)` called directly throughout. Context (Class, InstanceName, Method, InstanceUid, custom props) is pushed via `PushProperties()` using Serilog's ambient `LogContext`.
2. **In-memory ring buffers** — `AddEvent()` and `LogException()` also append to bounded `_events` (100) / `_errors` (10) lists, exposed as `Events`/`Errors` with `EventsUpdated`/`ErrorsUpdated` change events for consumers (UI/dashboards).

The bones are good: context enrichment, thread-safe bounded buffers, a single `LogException` choke point, structured templates at most call sites. But there are real problems.

## The significant issues

### 1. Hard dependency on Serilog's global static logger (biggest one)
`LogBase` calls `Serilog.Log.Error/Verbose` — the static `Log` facade. For a device library that others consume, this is the wrong coupling:
- Forces every consumer onto Serilog, specifically onto `Log.Logger` being configured globally. If they don't, every `Log.*` call goes to `SilentLogger` and vanishes — nothing signals this.
- Untestable — can't assert on log output without hijacking the global sink.
- Fights the .NET norm. Libraries are expected to take `Microsoft.Extensions.Logging.ILogger` (or `ILoggerFactory`) by injection. Serilog plugs into that fine on the consumer side.
- **Fix:** inject an `ILogger` (MEL abstraction) into `LogBase` via constructor, default `NullLogger.Instance`. Consumers wire Serilog → MEL once. Highest-value change.

### 2. String interpolation at ad-hoc `Log.*` call sites
Call sites outside `LogBase` interpolate runtime data into the message template, e.g. `Log.Verbose($"MqttClientNotConnectedException: {e}")`. This bakes values into the message text (losing queryable properties) and re-introduces the brace hazard — a `{` or `}` in the interpolated value can throw or mangle a template parse. Use template + parameter: `Log.Verbose("{Context}", e)`. (The `LogBase` core paths — `AddEvent`/`LogException` — already do this correctly.)

### 3. The two systems can disagree; `Log.*`-only call sites bypass the buffer
State changes go through `AddEvent` (buffer + Serilog). But many direct `Log.Warning/Error` calls (e.g. `Display.cs:148`, `QsysEcp.cs:109`) only hit Serilog — never land in `Errors`/`Events`, so a consumer watching the buffers misses them. Meanwhile errors go to `Errors` but not `Events`. No single timeline. Decide whether the buffer is the source of truth; if so, route everything through `AddEvent`/`LogException` and never call `Log.*` directly outside `LogBase`.

## Smaller issues
- **No severity axis.** Everything funnels to `Log.Verbose`, so the sink can't be filtered by importance. Note `EventType` is a *category, not a severity* — the two are orthogonal, so don't map category onto a log level. If sink-side filtering by importance is ever wanted, add a severity parameter separate from `EventType`. (Low priority; `EventType` is already emitted as a structured property, so consumers can filter the buffer by category today.)
- **`PushProperties()` cost on hot paths.** Allocates a `List<IDisposable>`, dictionary-iterates, pushes N `LogContext` frames unconditionally — even when the log is below active level and dropped. On per-byte/per-message RX paths (`AvCodersTcpClient`, SSH) this is wasted allocation. Guard with `Log.IsEnabled(level)`.
- **Inconsistent application of `PushProperties`.** Some `Log.*` calls are wrapped (get Class/Method/Instance context), many aren't (`Display.cs:148`).
- **`AddEvent` always fires `EventsUpdated` and logs even if `info` duplicates** — property setters guard `if (value == _powerState) return;`, but unguarded callers spam.
- **Silent `catch { }` in cleanup** (`oldStream?.Dispose()`) and a bare `catch { return false; }` in TCP — fine as cleanup, but a `Log.Debug` would help when disposes start throwing for real reasons.
- **No correlation between a buffered `Event` and its Serilog line.** `LogContext.Clone()` is captured into the Event (nice), but no shared id links the in-memory event to the sink record.

## Suggested priority order
1. Replace the static `Log` with an injected `ILogger` (MEL abstraction, `NullLogger` default).
2. Sweep ad-hoc interpolated `Log.*` call sites outside `LogBase` onto template + parameter (#2).
3. Pick one diagnostic timeline — funnel all logging through `LogBase` so buffers and sink agree (#3).
4. Perf/consistency polish (guard `PushProperties`, apply it uniformly).

## Key files
- `Core/LogBase.cs` — `AddEvent`, `LogException`, `PushProperties`, Events/Errors buffers.
- `Core/Enums.cs` — `EventType` enum (Other, Power, Input, VideoMute, Transport, Connection, DriverState, Volume, Error, Preset, Level) — a category, not a severity.
- `Core/Core.csproj` — depends only on `Serilog` 4.3.0; no sinks configured in the library.
