# AVCoders.Lighting

Lighting and dimmer drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Lighting
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Lighting). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `CBusLight` — Clipsal C-Bus
  - `CBusSerialInterface.SetupPci` then reads the PCI's unit type, firmware, network voltage and status with IDENTIFY, and shows them as details. The network voltage and status are read again every `CBusSerialInterface.HealthReadInterval` (1 minute). Each `SetupPci` replaces the health-read timer, so overlapping calls still leave one health read per interval
  - A low network voltage raises a Major issue and a marginal one a Minor issue. The PCI's error flags show as a detail; EE errors raise a Major issue and the others, apart from power on reset, a Minor one
  - A read still unanswered at the next health read, or answered with a reply that isn't the length its header gives, isn't text, or isn't a 12-byte status, is asked again then. Three misses in a row raise a Minor issue carrying the last raw reply or "no reply", which the first good reply resolves. An empty text reply shows the raw reply as its detail
  - An 8-byte `0x82` reply is always a channel status reply, even from unit 0, never an IDENTIFY reply
- `DyNet` — Philips Dynalite
- `Zigbee2MqttLight` — Zigbee2MQTT
  - Publishes `{"state": "", "brightness": ""}` to `zigbee2mqtt/<topic>/get` each time its MQTT client connects, and once at creation if the client is already connected, so Zigbee2MQTT publishes the light's state on `zigbee2mqtt/<topic>`. The request goes out after the light's SUBSCRIBE on that connection. It isn't repeated or retried: a light that doesn't answer stays Unknown until it next publishes
  - Shows `linkquality` and `update.state` from each state message as details. An empty or absent value shows the raw JSON with no issue; a value that can't be read shows raw, and the third in a row raises a minor issue that the next readable value resolves

## Usage

Drivers talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
