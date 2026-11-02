# AVCoders.Motor

Screen, blind and shade motor drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Motor
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Motor). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `ScreenTechnicsConnect` — a command sent while disconnected goes out 500 ms after the connection opens, without holding up the connection event
- `Grandview`
- `MotoluxBlindTransmitter`
- `BondDevice`, `BondGroup`
  - `BondBridge` takes an optional name (default `Bond Bridge`) and shows the bridge's firmware, model, Bond ID and uptime as details, from the `/v2/sys/version` poll every 85 s. An HTTP 404, 405 or 501 reply shows every value as `Not supported: ` and the raw reply, with no issue. Any other failed or unreadable reply, or one with no `fw_ver`, is a miss: the third in a row raises the minor `version-read` issue with the last reply, and the next good reply resolves it. A missing model, Bond ID or uptime shows `Not supported: no "<property>" in the version reply`, with no issue, and logs the reply body once

## Usage

Drivers derive from `Motor` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
