# AVCoders.SignalR.Camera

SignalR hub and bridge for AV Coders cameras: power, preset recall and tracking mode. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.Camera
```

Available from the AV Coders GitHub Packages feed (`https://nuget.pkg.github.com/AV-Coders/index.json`) and nuget.org. MIT licensed.

## What's inside

- `ICameraHub` / `CameraHub`
- `CameraUiSignalR` - subscribes to an `AVCoders.Camera` driver and rebroadcasts state
- `CameraManager`
