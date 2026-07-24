# c-sharp-device-library

The C# implementation of the AV Coders device library — a collection of drivers and
abstractions for controlling audio-visual hardware (displays, cameras, DSPs, matrix
switchers, PDUs, conferencing codecs, lighting, motors and more) over TCP, UDP, SSH,
serial, REST, MQTT, multicast and SNMP.

Each device domain ships as its own NuGet package (published to
[nuget.org](https://www.nuget.org/profiles/AV-Coders)) on top
of a shared `AVCoders.Core` foundation. Targets **.NET 8.0**.

## How it works

Every driver derives (directly or indirectly) from `LogBase` in `AVCoders.Core`, which
provides logging, an in-memory diagnostics buffer and tracing. Most drivers also derive
from `DeviceBase` (or, where there is volume to manage, from `VolumeControl`) and talk to
hardware through a `CommunicationClient` transport, so a driver is decoupled from *how* it
is connected:

```
LogBase                     logging + Events/Errors buffer + tracing
 ├─ VolumeControl           volume / mute state
 │   └─ Display             power + input + volume, IDevice          ← concrete drivers
 └─ DeviceBase              power / communication state, IDevice
     └─ CameraBase / Dsp / VideoMatrix / Conference / ...            ← concrete drivers
                                       │
                                CommunicationClient                   ← transport
                                  (TCP, UDP, SSH, REST, MQTT, multicast, SNMP, ...)
```

You wire a driver to a transport and subscribe to its state-change events:

```csharp
var comms   = new AvCodersTcpClient("192.168.1.50", 4352, "Projector", CommandStringFormat.Ascii);
var display = new PjLink(comms, "Projector", defaultInput);

display.OnPowerStateChanged += state => Console.WriteLine($"Power: {state}");
display.PowerOn();
```

## Packages

| Package | Domain | Notable drivers |
| --- | --- | --- |
| `AVCoders.Core` | Foundation: `LogBase`, `DeviceBase`, `CommunicationClient`/`VolumeControl`/`ThreadWorker` abstractions, enums, handler delegates | — |
| `AVCoders.CommunicationClients` | Concrete transports | `AvCodersTcpClient`, `AvCodersUdpClient`, `AvCodersSshClient`, `AvCodersMqttClient`, `AvCodersRestClient`, `AvCodersMulticastClient`, `AvCodersTcpServer`, `AvCodersSnmpV3Client`, `AvCodersWakeOnLan` |
| `AVCoders.Display` | Displays / projectors / LED walls | `PjLink`, `SamsungMdc`, `SonySerialControl`/`SonySimpleIpControl`/`SonyRest`, `NecUhdExternalControl`, `PhilipsSICP`, `LGCommercial`, `CecDisplay`, `NovaStarH5`, `ColorlightDeviceControlProtocolClassB` |
| `AVCoders.Matrix` | Matrix switchers / AV-over-IP | `ExtronIn16Xx`/`ExtronIn18Xx`/`ExtronSw`/`ExtronDtpCpxx`, `SvsiEncoder`/`SvsiDecoder`, `BlustreamAmf41W`, `Navigator`/`NavEncoder`/`NavDecoder`, `AVoIPEndpoint` |
| `AVCoders.Camera` | PTZ cameras & auto-tracking | `SonyVisca`, `AverVisca` (`ITrackingCamera`), `LumensCL511`, `AutomateVX` (1Beyond) |
| `AVCoders.Conference` | Conferencing codecs & phonebooks | `CiscoRoomOs` (+ output/mic faders), `CiscoRoomOsPhonebookParser` |
| `AVCoders.Dsp` | Audio DSPs | `BiampTtp` (Tesira), `QsysEcp` (Q-SYS), `BoseCspSoIP` |
| `AVCoders.Power` | PDUs / outlets | `EatonPdu`/`EatonOutlet` (SNMP), `ServerEdgePdu`/`ServerEdgeOutlet` (REST) |
| `AVCoders.MediaPlayer` | Media players / recorders / IPTV | `LumensLc300`, `ExtronSmp351`, `TriplePlay`, `VitecHttp`/`VitecServer`, `ExterityTci` |
| `AVCoders.Motor` | Screens / blinds / shades | `ScreenTechnicsConnect`, `Grandview`, `MotoluxBlindTransmitter`, `BondDevice`/`BondGroup` |
| `AVCoders.Lighting` | Lighting & dimmers | `CBusLight`, `DyNet` (Dynalite), `Zigbee2MqttLight` |
| `AVCoders.Annotator` | Annotation devices | `ExtronAnnotator401` |
| `AVCoders.Interface` | Touch panels / smart-home interfaces | `TybaTurn2` |
| `AVCoders.WirelessPresenter` | Wireless presentation | `ExtronSharelinkPro` |

`Climate/` (`TemperzoneUc8`, Modbus RTU HVAC) exists in the solution but is **not** currently
published as a package.

## Logging

> Upgrading from 2026.6.527 or earlier (a Serilog-based build)? See [MIGRATION.md](MIGRATION.md) —
> without a one-line startup change, device logging is silently discarded.

`AVCoders.Core` logs through the `Microsoft.Extensions.Logging` (MEL) abstraction — it has
**no hard dependency on any specific logging framework**. Wire a logger factory **once at
startup** via the static `LogBase.LoggerFactory`. Until you do, logging is silently
discarded through `NullLoggerFactory` (nothing breaks; nothing is logged).

Bridge an existing Serilog logger (requires the `Serilog.Extensions.Logging` package):

```csharp
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()      // surfaces the Class/InstanceName/Method scope properties
    .WriteTo.Console()
    .CreateLogger();

LogBase.LoggerFactory = new SerilogLoggerFactory(Log.Logger);
```

Or use any other MEL provider:

```csharp
LogBase.LoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
```

Each log line is automatically scoped with the instance's `Class`, `InstanceName`,
`InstanceUid` and the calling `Method`.

### Diagnostics buffer

Independently of the sink, every `LogBase` instance keeps a bounded in-memory ring buffer
of recent `Events` (default 100) and `Errors` (default 10), with `EventsUpdated` /
`ErrorsUpdated` change events — handy for driving a status UI without a log sink. Limits
are adjustable via `SetEventLimit` / `SetErrorLimit`.

### Issues

Every instance keeps a bounded list of the issues (incidents) its driver has raised: an
`Issues` history plus an `OngoingIssues` view of what's wrong *right now*, and an
`event EventHandler<IssuesChangedEventArgs> IssuesChanged`. Each `Issue` has a status:

- **Ongoing** — e.g. a display on the wrong input or power state, or `CommunicationState`
  dropping to `Error`. Stays in `OngoingIssues` until the driver observes the condition
  recover and calls `ResolveIssue`.
- **Momentary** — e.g. a DSP not answering one poll. Instantly historical; never counted
  as ongoing. Repeated raises of the same key coalesce into one entry, bumping
  `OccurrenceCount` and `LastRaisedAt`.
- **Resolved** — a formerly ongoing issue that has recovered. It stays in `Issues` (with
  `ResolvedAt` set) as history; a later re-raise of the key is a new issue.

Each issue also carries an `IssueSeverity` (`Minor`/`Major`/`Critical` — momentary raises
default to Minor, ongoing to Major) and a stable `Id` assigned at creation and preserved
through updates and resolution, so external systems can correlate by it.

Drivers raise these via `RaiseMomentaryIssue` / `RaiseOngoingIssue` / `ResolveIssue`; the
base classes already cover power-state, input and communication-state faults. Repeated
momentary failures can auto-escalate: pass `escalateAfter: n` and, after `n` consecutive
momentary raises of the key without an intervening `ResolveIssue`, an ongoing issue is
raised under the same key one severity level higher. Call `ResolveIssue` on every
successful response — it resolves the escalated issue *and* resets the consecutive count,
and is a no-op otherwise.

The list is capped at 50 entries (adjustable via `SetIssueLimit`); when over the cap,
historical (momentary/resolved) entries are evicted before ongoing ones, oldest first.
Raising or resolving also lands one entry in the `Events` history (`EventType.Error`).

`IssuesChangedEventArgs` carries the `ChangedIssue` and an `IssueChangeKind`
(`Raised`/`Updated`/`Resolved`/`Trimmed`), so per-device integrations react without
diffing snapshots. Wiring a ticketing system to a device looks like:

```csharp
device.IssuesChanged += (_, e) =>
{
    if (e.Kind == IssueChangeKind.Raised && e.ChangedIssue!.Status == IssueStatus.Ongoing)
        tickets.Open(e.ChangedIssue.Id, e.ChangedIssue.Message, e.ChangedIssue.Severity);
    else if (e.Kind == IssueChangeKind.Resolved)
        tickets.Close(e.ChangedIssue!.Id);
};
```

Events fire on driver comm/poll threads, so UI consumers should marshal (e.g. Blazor's
`InvokeAsync`), as with `EventsUpdated`. Subscribers are invoked individually and
exceptions they throw are caught and routed to the instance's `Errors` buffer/log — one
faulty subscriber can neither crash the process nor stop the remaining subscribers from
being notified.

### The registry (dashboards)

Every `LogBase` auto-registers with the static `LogBaseRegistry` on construction. The
registry is the dashboard view: `LogBaseRegistry.GetOngoingIssues()` returns every ongoing
issue across every instance (as `SourcedIssue` pairs), and
`LogBaseRegistry.OngoingIssuesChanged` fires — with the aggregate snapshot and the
originating instance as sender — whenever any instance's ongoing set changes
(momentary-only changes are skipped). Registry subscribers must handle their own
exceptions; the registry swallows them. The registry also fans out maintenance calls
(`ClearEvents`, `ClearErrors`, `SetEventLimits`, `SetErrorLimits`, `SetIssueLimits`).

Registration roots the instance for the life of the process, which matches how drivers are
used; anything created transiently must call `LogBaseRegistry.Deregister` on teardown.

## Tracing

`LogBase` exposes a static `ActivitySource` (`LogBase.ActivitySourceName` == `"AVCoders.Core"`).
Each `PushProperties(...)` block opens a `Class.Method` span under the current `Activity`,
so spans you create externally automatically become the parent. It is zero-cost when no
listener is registered.

Collect spans with OpenTelemetry:

```csharp
.WithTracing(t => t
    .AddSource(LogBase.ActivitySourceName)
    .AddOtlpExporter())
```

```csharp
using var activity = LogBase.ActivitySource.StartActivity("HandleRoomPowerOn");
display.PowerOn();   // each driver method nests its own span under this one
```

## Building & testing

```bash
dotnet restore
dotnet build
dotnet test
```

Most domains have a matching `*Test` xUnit project (tests use Moq); `Interface` and
`Climate` are the current exceptions. `CommunicationClientsTest` exercises the real
transports against loopback sockets and an in-process HTTP listener (no Moq).

## Versioning & releases

Two workflows publish packages:

- **CI** (`.github/workflows/dotnet.yml`) runs on every push and PR: it builds, tests,
  packs every published project and pushes `YYYY.MM.<run_number>-beta` **prereleases to
  GitHub Packages**. That feed requires GitHub authentication, so it's effectively
  internal — used for CI and dogfooding, not distribution.
- **Releases** (`.github/workflows/release.yml`) are manually triggered from `main`
  (and require a passing CI run for that commit). They publish stable
  `YYYY.M.<release_count_this_month>` packages to
  [nuget.org](https://www.nuget.org/profiles/AV-Coders) via trusted publishing (OIDC).
  This is the public channel — `dotnet add package AVCoders.<Name>` needs no extra
  source configuration.
