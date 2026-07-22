# AVCoders.Conference

Conferencing codec drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0**.

## Install

```bash
dotnet add package AVCoders.Conference
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Conference). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `CiscoRoomOs` — Cisco RoomOS control, with output and microphone faders
- `CiscoRoomOsPhonebookParser` — phonebook parsing

## Usage

Drivers derive from `Conference` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
