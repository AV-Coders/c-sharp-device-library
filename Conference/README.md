# AVCoders.Conference

Conferencing codec drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Conference
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Conference). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `CiscoRoomOs` — Cisco RoomOS control, with output and microphone faders
  - Shows the codec's model, firmware (`SystemUnit Software Version`), software name, serial number, MAC address, Ethernet speed, IP address, subnet mask and gateway as details. They're read on each login
  - Uptime shows as a detail, read on login and with each heartbeat (52 s)
  - Temperature, Webex, each SIP registration (`SIP Registration 1`, with the codec's reason) and each connected peripheral (`Peripheral 1001`: name, type, status, software and serial) show as details. They're read on login and then follow `xFeedback`
  - Raises an issue when the temperature is High (minor) or Critical (critical), when Webex reports Error, when a SIP registration fails, and when a peripheral is LostConnection or ResponseTimedOut. Each clears when the codec reports it recovered; the Webex issue also clears on Disabled or Stopped. A SIP registration or peripheral missing from the listing read after a login loses its detail and its issue
  - A `ControlSystem` peripheral other than this driver's own (`AV-Coders-RoomOS-Module{instanceId}`) shows as a detail in normal tone and raises no issue, whatever its status. This driver's own entry raises the issue like any other peripheral
  - Each Critical, Error or Warning message in the codec's diagnostics (the web interface's issue list) raises an issue keyed by its type, at critical, major or minor. Info messages show in the `Diagnostics (info)` detail instead
  - Every read is tagged with `resultId`. A read the codec rejects (`status=Error`, which is what CE 9 returns for Webex) shows `Not supported: ` and the raw reply in its detail, with no issue. A rejected uptime read isn't asked again until the next login. A read answered with no value shows the raw reply in its detail, with no issue. A temperature or Webex status outside the documented value space shows as received, with no issue. An uptime that isn't a number shows as received in warning tone and is read again at the next poll; the third in a row raises a minor issue carrying the raw reply, and the next number resolves it
  - Lines starting `*r PhonebookSearchResult` are left to `CiscoRoomOsPhonebookParser`: `CiscoRoomOs` doesn't parse them or log them as unhandled feedback. The transport's traffic log still records every line
- `CiscoRoomOsPhonebookParser` — phonebook parsing

## Usage

Drivers derive from `Conference` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
