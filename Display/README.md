# AVCoders.Display

Display, projector and LED-wall drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Display
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Display). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `PjLink`
- `SamsungMdc`
- `SonySerialControl`, `SonySimpleIpControl`, `SonyRest`
- `NecUhdExternalControl`
- `PhilipsSICP`
- `LGCommercial`
- `CecDisplay`
- `NovaStarH5`
- `ColorlightDeviceControlProtocolClassB`

## Usage

Wire a driver to a transport (from `AVCoders.CommunicationClients`) and subscribe to its state-change events:

```csharp
var comms   = new AvCodersTcpClient("192.168.1.50", 4352, "Projector", CommandStringFormat.Ascii);
var display = new PjLink(comms, "Projector", defaultInput);

display.OnPowerStateChanged += state => Console.WriteLine($"Power: {state}");
display.PowerOn();
```

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for logging, tracing and the full architecture overview.
