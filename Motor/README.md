# AVCoders.Motor

Screen, blind and shade motor drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Motor
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Motor). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `ScreenTechnicsConnect`
- `Grandview`
- `MotoluxBlindTransmitter`
- `BondDevice`, `BondGroup`

## Usage

Drivers derive from `Motor` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
