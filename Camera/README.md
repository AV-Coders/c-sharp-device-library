# AVCoders.Camera

PTZ camera and auto-tracking drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

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

### Cameras that can't reply

If the camera is wired one-way (transmit only), set `DeviceSendsResponses = false` on the driver. Drivers that normally wait for replies (`SonyVisca`, `AverVisca`) then stop polling and update power and preset feedback as each command is handed to the transport. `LumensCL511` never reads replies, so it reports `false` from construction and logs a warning if asked to change.

Assumed feedback is only as good as the transport: if a client queues or drops bytes while it is disconnected, the driver cannot tell and still reports the command as done.
