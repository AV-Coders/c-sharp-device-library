# AVCoders.Camera

PTZ camera and auto-tracking drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Camera
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Camera). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `SonyVisca`
- `AverVisca` — implements `ITrackingCamera`
- `LumensCL511`
- `AutomateVX` (1 Beyond)

## Usage

Drivers derive from `CameraBase` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
