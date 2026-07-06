# Migrating from 2026.7.x (Serilog era) to the MEL release

This release removes the library's hard dependency on Serilog. All `AVCoders.*` packages
now log through the `Microsoft.Extensions.Logging` (MEL) abstraction instead of Serilog's
static `Log` facade. This guide covers upgrading a consuming program from build
**2026.7.524** (or any earlier Serilog-era build).

> **The one thing you cannot skip:** if your program does not set `LogBase.LoggerFactory`
> at startup, every device driver logs to `NullLoggerFactory` — **all device logging
> silently disappears**. Nothing throws; the logs are just gone.

## Required changes

### 1. Wire up the logger factory at startup

Previously the packages logged through Serilog's static `Log.Logger`, so configuring
Serilog was enough. Now you must hand the library a logger factory once, before creating
any devices.

Keeping Serilog as your sink (the usual case — add the `Serilog.Extensions.Logging`
package):

```csharp
using Serilog;
using Serilog.Extensions.Logging;
using AVCoders.Core;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()      // surfaces the Class/InstanceName/Method scope properties
    .WriteTo.Console()
    .CreateLogger();

LogBase.LoggerFactory = new SerilogLoggerFactory(Log.Logger);
```

Or any other MEL provider:

```csharp
LogBase.LoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
```

Your existing Serilog configuration (sinks, minimum levels, enrichers, `appsettings`
integration) is otherwise unchanged.

### 2. Add a Serilog package reference if you relied on the transitive one

`AVCoders.Core` 2026.7.x pulled in `Serilog 4.3.0` transitively. That reference is gone.
If your program uses Serilog APIs but never referenced the package directly, it will stop
compiling — add explicit references:

```xml
<PackageReference Include="Serilog" Version="4.3.0" />
<PackageReference Include="Serilog.Extensions.Logging" Version="8.0.0" />
```

## If you wrote custom drivers (subclasses of LogBase / DeviceBase / drivers)

Custom drivers previously called Serilog's static `Log.Debug(...)` etc. directly. Those
calls still compile (against your own Serilog reference) and still write to your Serilog
pipeline, **but** they bypass MEL, so the per-instance context (`Class`, `InstanceName`,
`InstanceUid`, `Method`) pushed by `PushProperties` may no longer be attached to them.

Switch to the new protected helpers inherited from `LogBase`, which log through the
instance's MEL logger with full context:

| Before (Serilog static)       | After (inherited from `LogBase`)           |
| ----------------------------- | ------------------------------------------ |
| `Log.Verbose("...")`          | `LogVerbose("...")`                        |
| `Log.Debug("...")`            | `LogDebug("...")`                          |
| `Log.Information("...")`      | `LogInformation("...")`                    |
| `Log.Warning("...")`          | `LogWarning("...")`                        |
| `Log.Error("...")`            | `LogError("...")`                          |
| `Log.Error(ex, "...")`        | `LogError(ex, "...")`                      |

Message templates (`"{Property}"` placeholders) work exactly as before — MEL and Serilog
share the template syntax.

Also note:

- `PushProperties()` now defaults its method name via `[CallerMemberName]` — the explicit
  `PushProperties("MethodName")` argument is only needed to override, and
  `PushProperties(null)` suppresses the Method property. Existing calls keep working.
- Wrap driver work in `using (PushProperties())` as before; the scope flows to every
  `Log*` call inside the block.

## What you get for free after migrating

- **Tracing:** each `PushProperties` block emits an OpenTelemetry-compatible span under
  the `ActivitySource` name `"AVCoders.Core"` (`LogBase.ActivitySourceName`). Opt in with
  `.WithTracing(t => t.AddSource(LogBase.ActivitySourceName))`; zero cost when no listener
  is registered.
- **State-change events:** `DeviceBase.OnCommunicationStateChanged` and matching
  `On*Changed` C# events on `VolumeControl`, `Display` and the Cisco conference drivers,
  alongside the existing handler delegates.
- **No framework lock-in:** `AVCoders.Core`'s only logging dependency is
  `Microsoft.Extensions.Logging.Abstractions`.

## Versioning note

Package versions are date-based (`YYYY.MM.X`) and do **not** signal breaking changes.
Pin your current `2026.7.524` reference until you have applied step 1 above; upgrading
without it produces a program that runs normally but logs nothing from any device.

## Checklist

- [ ] Add `Serilog.Extensions.Logging` (or another MEL provider) to the program
- [ ] Add explicit `Serilog` reference if it was only transitive
- [ ] Set `LogBase.LoggerFactory` once at startup, before any device is constructed
- [ ] Confirm `.Enrich.FromLogContext()` is in the Serilog configuration
- [ ] Custom drivers: replace static `Log.*` calls with inherited `Log*` helpers
- [ ] Run the program and verify device logs appear with `Class`/`InstanceName` properties
