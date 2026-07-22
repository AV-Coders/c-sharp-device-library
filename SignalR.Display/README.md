# AVCoders.SignalR.Display

SignalR hub and bridge for AV Coders displays: power, input, volume and mute. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.Display
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.SignalR.Display). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## What's inside

- `IDisplayHub`
- `DisplayUiSignalR` - subscribes to an `AVCoders.Display.Display` and rebroadcasts state
- `DisplayManager`
