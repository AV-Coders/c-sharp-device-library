# AVCoders.Power

PDU and outlet drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Power
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Power). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `EatonPdu`, `EatonOutlet` — Eaton (SNMP)
- `ServerEdgePdu`, `ServerEdgeOutlet` — ServerEdge (REST)

## Usage

Drivers talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
