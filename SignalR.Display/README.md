# AVCoders.SignalR.Display

SignalR hub and bridge for AV Coders displays: power, input, volume and mute. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.Display
```

Available from the AV Coders GitHub Packages feed (`https://nuget.pkg.github.com/AV-Coders/index.json`) and nuget.org. MIT licensed.

## What's inside

- `IDisplayHub`
- `DisplayUiSignalR` - subscribes to an `AVCoders.Display.Display` and rebroadcasts state
- `DisplayManager`
