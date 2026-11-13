# AVCoders.Interface

Touch panel and smart-home interface drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Interface
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Interface). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `GlobalCacheContactClosure`: Global Cache iTach contact-closure relays (IP2CC and others on the TCP API, port 4998). `Close(relay)` and `Open(relay)` hand `setstate` to the client whether or not it is connected. On every connect it asks each relay's state with `getstate`, then sets each relay the program has commanded to the last state the program commanded, including a command made while the client was not connected. Each `state` reply shows as a `Relay <module>:<relay>` Detail (`Closed`, `Open`, or the value as received). Momentary Minor issues: `rejected-command` for an `ERR_` or `unknowncommand` reply, naming the command and the reply; `unanswered-command` for a command with no reply within 5 s; `unreadable-reply` for a line it can't read, or for more than 1024 characters with no line end, shown escaped
- `TybaTurn2`

## Usage

Drivers talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
