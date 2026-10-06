# AVCoders.Matrix

Matrix switcher and AV-over-IP drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Matrix
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Matrix). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `ExtronIn16Xx`, `ExtronSw`, `ExtronDtpCpxx` (DTP CrossPoint 82/84/86/108), `ExtronDtp2Cp82` (DTP2 CrossPoint 82)
- `ExtronIn18Xx` — IN1806 / IN1808 scaling switchers. Inputs are discovered from the model reply
  (`EndpointsChangedHandlers` fires when the count changes; existing ports are kept) and carry signal presence and
  HDCP status. Output 1A (HDMI) and 1B (DTP2/HDBT) exist from construction as `GetOutputs()[0]` and `[1]` and carry
  sink detection and HDCP status. The HDMI loop out is not exposed.
- `ExtronAnnotator401VideoMatrix` — the HDMI input and two HDMI outputs of an Extron Annotator 401 as a
  routing-free `VideoMatrix`, sharing the annotator's communication client with the `AVCoders.Annotator` driver.
  The endpoints exist from construction and carry sync state and HDCP status; both outputs always show the only
  input, so the routing methods only log.
- `CiscoRoomOsVideoMatrix` — the video inputs and outputs of a Cisco RoomOS / CE codec as a routing-free
  `VideoMatrix`, sharing the codec's communication client with the `AVCoders.Conference` driver. Connectors are
  discovered from the codec's status (`EndpointsChangedHandlers` fires as they appear) and carry sync state,
  resolution, HDCP and history. Input names come from the codec's connector configuration; outputs stay "Output n"
  and expose the connected display's EDID name as `ConnectedDeviceName`, logged on change.
- `SvsiEncoder`, `SvsiDecoder`
- `AntricaEncoder` — the HDMI input of an Antrica ANT-35000 series encoder, polled every 10 seconds over its HTTP
  API through a `RestComms` client (`AvCodersRestClient`). Reports signal presence (`GIS_VIDEOLOSS1`) and the
  detected input format (`VID_INPUTFORMAT`, read as decimal or `0x` hex) as the input resolution. HDCP is always
  `NotSupported`. HTTP API authentication (`NET_USEHTTPAPIAUTH`) is not supported.
- `BlustreamAmf41W`
- `Navigator`, `NavEncoder`, `NavDecoder`
- `AVoIPEndpoint`

## Usage

Drivers derive from `VideoMatrix` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
