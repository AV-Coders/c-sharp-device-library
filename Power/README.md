# AVCoders.Power

PDU and outlet drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Power
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Power). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `TrippLitePdu`, `TrippLiteOutlet` — Tripp Lite / Eaton PowerAlert units (SNMPv3), including ATS models. Discovers the outlet count, names and controllability from the device, polls outlet states with a single walk, and on ATS models reports input feed voltages, the active source and a power-redundancy issue when a feed is lost. Replaces the former `EatonPdu`/`EatonOutlet`, which hardcoded eight outlets and spoke the Tripp Lite MIB despite the name.
  - Shows the model, serial number, firmware and MAC as details, read on each discovery (start-up and `Reinitialise`). A firmware, serial number or MAC the unit doesn't support (`noSuchObject`, `noSuchInstance` or `endOfMibView`) shows `Not supported: ` and the raw reply, with no issue. One the unit doesn't answer shows "no answer" in warning tone, discovery carries on and it is asked again on the next poll whose outlet, ATS and sensor polls all succeed, after that poll's alarm count; the third unanswered query in a row raises a minor issue, and the first answer resolves it
  - Shows the output voltage and power, each input feed and each environment sensor reading as details on every poll
  - Polls the alarm count (`tlpAlarmsPresent`) every poll and shows it as a detail. While an outlet, ATS or sensor poll is failing, the alarm count and any owed identity reads aren't sent, so one fault raises only the communication issue; they resume on the first poll whose outlet, ATS and sensor polls all succeed. A count above 0 shows the count and every alarm table description as received, and raises a minor issue listing the ones that aren't deliberate states: "Load Off" entries and entries naming an input whose warning is disabled (`DisableInputWarning`) are left out, and the issue resolves when none remain. An unanswered count shows "no answer" and one that isn't a number shows the raw reply, both in warning tone; either is asked again on the next poll, the third in a row raises a minor issue, the first count resolves it, and neither affects the communication state. A unit that doesn't support the count shows `Not supported: ` and the raw reply, with no issue, and isn't asked again until `Reinitialise`
  - An SNMP reply with a non-zero error status (RFC 3416) counts as answered, so the client stays Connected, and its raw status and index are shown as `error status GenError (5), index 1`:
    - Firmware, serial number, MAC and alarm count: `noSuchName` (2), `noAccess` (6) and `authorizationError` (16) are rejections and get the `Not supported: ` treatment above. Every other status, such as `tooBig` (1), `genErr` (5) or `resourceUnavailable` (13), is a miss: the status shows in warning tone and the query is asked again, as for an unanswered one
    - Outlet, ATS and sensor polls, discovery queries and outlet commands fail exactly as an unanswered one does, with the message ending `was answered with error status …` instead of `was not answered` (or `was not acknowledged` for a command)
  - Object names and OIDs come from the `TRIPPLITE-PRODUCTS` MIB (Tripp Lite, LAST-UPDATED 202105120000Z); `…850` is `1.3.6.1.4.1.850`:
    - Device table: `tlpDeviceNumDevices` …850.1.1.1.1.0, and the `tlpDeviceType`, `tlpDeviceModel` and `tlpDeviceName` columns …850.1.1.1.2.1.3, .5 and .6
    - Agent: `tlpAgentVersion` …850.1.2.1.1.2.0 (Firmware), `tlpAgentMAC` …850.1.2.1.1.4.0, `tlpAgentSerialNum` …850.1.2.1.1.5.0 (Serial)
    - Alarms: `tlpAlarmsPresent` …850.1.3.1.0, `tlpAlarmDetail` column …850.1.3.2.1.6
    - PDU (`tlpPdu` …850.1.1.3.2) and ATS (`tlpAts` …850.1.1.3.4), each object prefixed `tlpPdu` or `tlpAts`: `IdentNumOutlets` .1.2.1.4, `OutletName` .3.3.1.1.2, `OutletState` .3.3.1.1.4, `OutletControllable` .3.3.1.1.5, `OutletCommand` .3.3.1.1.6
    - ATS only: `tlpAtsDeviceOutputActivePowerTotal` .2.1.1.9, `tlpAtsInputSourceAvailability` .3.1.1.1.12, `tlpAtsInputSourceInUse` .3.1.1.1.13, `tlpAtsInputPhaseVoltage` .3.1.2.1.5, `tlpAtsOutputVoltage` .3.2.1.1.4
    - Envirosense (`tlpEnvirosense` …850.1.1.3.3): `tlpEnvIdentTempSupported` .1.2.1.1, `tlpEnvIdentHumiditySupported` .1.2.1.2, `tlpEnvTemperatureC` .3.1.1.1, `tlpEnvTemperatureInAlarm` .3.1.1.3, `tlpEnvHumidityHumidity` .3.2.1.1, `tlpEnvHumidityInAlarm` .3.2.1.2
- `ServerEdgePdu`, `ServerEdgeOutlet` — ServerEdge (REST)
- `ThorRf11IqPdu`, `ThorRf11IqOutlet` — Thor Technologies RF11iQ Smart Rack Guard (HTTP, basic auth). Channels 1-7 are the switched GPO outlets and channel 8 switches the IEC outputs as a group; the device stores no names, so the constructor takes them keyed by channel. Polls outlet states, per-channel current, surge-protection health for both circuits and the fault buzzer; a reboot is off-then-on after `RebootDelay`. Raises a critical issue while a circuit reports damaged and a minor one while the buzzer sounds. Shows the firmware version from each status poll as a detail, read after the outlet states, circuits and buzzer, so a firmware handler that throws doesn't skip them. A status reply with no `ver` element is read again at the next poll, and the third in a row raises a minor issue; a blank `ver` shows the raw element with no issue.

## Usage

Drivers talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
