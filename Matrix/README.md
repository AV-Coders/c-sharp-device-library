# AVCoders.Matrix

Matrix switcher and AV-over-IP drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Matrix
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Matrix). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `ExtronIn16Xx`, `ExtronIn18Xx`, `ExtronSw`, `ExtronDtpCpxx` (DTP CrossPoint 82/84/86/108), `ExtronDtp2Cp82` (DTP2 CrossPoint 82)
- `CiscoRoomOsVideoMatrix` — the video inputs and outputs of a Cisco RoomOS / CE codec as a routing-free
  `VideoMatrix`, sharing the codec's communication client with the `AVCoders.Conference` driver. Carries sync
  state, resolution, HDCP and history per connector. Input names come from the codec's connector configuration;
  outputs stay "Output n" and expose the connected display's EDID name as `ConnectedDeviceName`, logged on change.
- `SvsiEncoder`, `SvsiDecoder`
- `BlustreamAmf41W`
- `Navigator`, `NavEncoder`, `NavDecoder`
- `AVoIPEndpoint`

## Usage

Drivers derive from `VideoMatrix` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
