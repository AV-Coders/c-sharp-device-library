# AVCoders.Power

PDU and outlet drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Power
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Power). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `TrippLitePdu`, `TrippLiteOutlet` — Tripp Lite / Eaton PowerAlert units (SNMPv3), including ATS models. Discovers the outlet count, names and controllability from the device, polls outlet states with a single walk, and on ATS models reports input feed voltages, the active source and a power-redundancy issue when a feed is lost. Replaces the former `EatonPdu`/`EatonOutlet`, which hardcoded eight outlets and spoke the Tripp Lite MIB despite the name.
- `ServerEdgePdu`, `ServerEdgeOutlet` — ServerEdge (REST)
- `ThorRf11IqPdu`, `ThorRf11IqOutlet` — Thor Technologies RF11iQ Smart Rack Guard (HTTP, basic auth). Channels 1-7 are the switched GPO outlets and channel 8 switches the IEC outputs as a group; the device stores no names, so the constructor takes them keyed by channel. Polls outlet states, per-channel current, surge-protection health for both circuits and the fault buzzer; a reboot is off-then-on after `RebootDelay`. Raises a critical issue while a circuit reports damaged and a minor one while the buzzer sounds.

## Usage

Drivers talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
