# AVCoders.Core

Foundation for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library) — every driver package builds on it. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Core
```

Published to GitHub Packages — add the `https://nuget.pkg.github.com/AV-Coders/index.json` source first. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## What's inside

- **`LogBase`** — logging through the `Microsoft.Extensions.Logging` (MEL) abstraction (no hard dependency on any logging framework), an in-memory ring buffer of recent `Events`/`Errors`, and a static `ActivitySource` for tracing.
- **`DeviceBase`** — power and communication state, implements `IDevice`.
- **`VolumeControl`** — volume/mute state for drivers that need it.
- **`CommunicationClient`** — transport abstraction (concrete clients live in `AVCoders.CommunicationClients`).
- Shared enums, handler delegates and `ThreadWorker`.

## Logging

Wire a logger factory **once at startup**. Until you do, logging is silently discarded:

```csharp
LogBase.LoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
```

Any MEL provider works (Serilog via `SerilogLoggerFactory`, etc.). Each log line is scoped with the instance's `Class`, `InstanceName`, `InstanceUid` and the calling `Method`. Spans are emitted under `LogBase.ActivitySourceName` (`"AVCoders.Core"`) and are zero-cost when no listener is registered.

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for the full logging, tracing and diagnostics-buffer guide.
