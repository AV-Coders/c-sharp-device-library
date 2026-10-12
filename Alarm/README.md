# AVCoders.Alarm

Alarm panel base classes for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Alarm
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Alarm). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Classes

- `AlarmPanel` — a `DeviceBase` holding the panel's areas, zones and relays. A panel driver owns the protocol and routes status to its children.
- `Area` — armed or disarmed, with `Arm(pin)` and `Disarm(pin)`.
- `Zone` — sealed or unsealed, plus isolated and tampered flags. `ZoneType` is set in config: Reed, Motion, Smoke, GlassBreak or Panic.
- `Relay` — active or inactive, with `Activate()`, `Deactivate()` and `Activate(duration)`. `RelayType.Door` is a door lock, where active means unlocked.

Each state change raises an `EventHandler<T>` event (`StateChanged`, `IsolatedChanged`, `TamperedChanged`) only when the value changes.
