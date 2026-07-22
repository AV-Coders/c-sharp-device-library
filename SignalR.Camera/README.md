# AVCoders.SignalR.Camera

SignalR hub and bridge for AV Coders cameras: power, preset recall and tracking mode. Part of the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.SignalR.Camera
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.SignalR.Camera). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## What's inside

- `ICameraHub` / `CameraHub`
- `CameraUiSignalR` - subscribes to an `AVCoders.Camera` driver and rebroadcasts state
- `CameraManager`
