# AVCoders.CommunicationClients

Transport clients for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library) — the concrete `CommunicationClient` implementations that drivers talk to hardware through. Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.CommunicationClients
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.CommunicationClients). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Transports

- `AvCodersTcpClient`
- `AvCodersUdpClient`
- `AvCodersSshClient`
- `AvCodersMqttClient`
- `AvCodersRestClient`
- `AvCodersMulticastClient`
- `AvCodersTcpServer`
- `AvCodersSnmpV3Client`
- `AvCodersWakeOnLan`

## Usage

A driver is decoupled from *how* it is connected — you construct a transport and hand it to the driver:

```csharp
var comms = new AvCodersTcpClient("192.168.1.50", 4352, "Projector", CommandStringFormat.Ascii);
```

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring examples.
