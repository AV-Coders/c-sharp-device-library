# AVCoders.Dsp

Audio DSP drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Dsp
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Dsp). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `BiampTtp` — Biamp Tesira
  - Shows the Tesira's model, revision, firmware, serial number, hostname, network interfaces and DNS as details. They're read on connect and on each `Reinitialise`, which also runs every few minutes while idle
  - Instance tags the driver uses that aren't in the Tesira's configuration show as Missing Controls, with an issue for each
  - The idle keep-alive reads the active fault list every 10 s and raises each fault as an issue. Its round trip shows as Reply Time. A reply slower than `BiampTtp.SlowKeepAliveReplyMs` (500 ms) raises an issue, and one missing after `BiampTtp.KeepAliveReplyWindowMs` (1 s) is flagged straight away. `BiampTtp.MaxSlowKeepAlives` slow or `BiampTtp.MaxMissedKeepAlives` missed keep-alives in a row reconnect the session, which clears the TTP slowdown in older firmware
  - Raises an issue when the firmware is below 4.11, which isn't supported, or is a 5.x below 5.7
- `QsysEcp` — QSC Q-SYS
  - Pass an optional `RestComms` for Core Manager (`https`, `QsysEcp.CoreManagerPort` 443), plus a username and password when Access Control is on, to show the Core's firmware, serial number and hardware ID as details. They're read on connect and on each design redeploy
  - `QsysVolumeControl` — a level and a mute as one `VolumeControl`
  - `QsysVolumeOnlyControl` — a level with no mute (`SupportsMute` is `false`)
  - `QsysMuteOnlyControl` — a mute with no level (`SupportsLevel` is `false`)
- `BoseCspSoIP` — Bose ControlSpace
- `CecAudioSystem` — a CEC soundbar or AVR (verified on a Sonos Beam). A single `VolumeControl`, not a `Dsp` subclass

## Usage

Drivers derive from `Dsp` (except `CecAudioSystem`, which is a `VolumeControl` over a CEC `SerialClient`) and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
