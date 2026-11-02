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
  - A read the Tesira answers with `-ERR ATTRIBUTE_NOT_SUPPORTED` or `-ERR '<attribute>' is not supported by ...` shows `Not supported: ` and the raw reply in its details (the fault list in Active Faults), with no issue. Any other `-ERR` to a read counts as a miss and keeps the last value: the third in a row raises a Minor issue carrying the raw reply, and the next good reply resolves it
  - A reply to one of those reads (or the fault list) that can't be read shows the raw line in its details in warning tone and is read again at the next `Reinitialise` (the fault list at the next keep-alive). The third in a row raises a Minor issue carrying the raw line, and the next good reply resolves it. An empty firmware or serial number string shows the raw line with no issue
  - Every subscription is sent again on connect and on each `Reinitialise`, since the Tesira drops them when it reboots (Biamp, *Tesira Text Protocol*, `Tesira Text Protocol.pdf` p. 4: "they can be recreated by subscribing to the same block at regular intervals") or loads a new configuration. A re-subscribe the Tesira answers with `-ERR ALREADY_SUBSCRIBED` means the subscription is still in place: it counts as answered and is logged at Verbose, with no issue. That reply isn't in the manual; it's what firmware 5.2.0.8 answers. Any other `-ERR` to a set, preset recall or subscription raises the momentary `error-reply` issue, which names the reply and the command: `The Tesira returned <reply> to <command>`
  - The `+OK` or `-ERR` that answers a set, preset recall or subscription is logged under the trigger id of the call that sent it, so `/Logs?trigger=` shows the command and its reply together
- `QsysEcp` — QSC Q-SYS
  - A control the Core doesn't know (`bad_id "<name>"`) shows in Missing Controls, with an ongoing `bad-control:<name>` issue until it reports a value
  - A blank control name (empty or whitespace) for a level, mute, string or meter is never added to a change group or read (`cga`/`cg`). It's logged with the raw name and shows as `""` in Missing Controls. Each blank control raises its own ongoing issue, keyed by the raw name, its type and its registration order (e.g. `bad-control:"" level 1`), so none is hidden. A Core answers an empty name with `bad_id ""`, which raises `bad-control:`
  - `SetLevel`, `SetAudioMute` and `SetValue` send nothing for a blank control name, and log a Warning with the raw name
  - Pass an optional `RestComms` for Core Manager (`https`, `QsysEcp.CoreManagerPort` 443), plus a username and password when Access Control is on, to show the Core's firmware, serial number and hardware ID as details. They're read on connect and on each design redeploy. With a username the driver logs on first and reads with the token, keeps the token across reconnects, and logs on again only when Core Manager refuses it
  - A Core Manager HTTP 404, which is what firmware without that API returns, shows `Not supported: ` and the raw reply in those details, with no issue. Any other failure raises the Minor `core-manager` issue
  - `QsysVolumeControl` — a level and a mute as one `VolumeControl`
  - `QsysVolumeOnlyControl` — a level with no mute (`SupportsMute` is `false`)
  - `QsysMuteOnlyControl` — a mute with no level (`SupportsLevel` is `false`)
- `BoseCspSoIP` — Bose ControlSpace. A command the CSP rejects with a NAK raises an issue on its module (e.g. `nak:Bar Gain`), Major for code 01 (no such module, or a duplicate name) and 02 (illegal index), Minor otherwise; it resolves when that module next answers
  - Polls each control with a 30 ms gap between queries
- `CecAudioSystem` — a CEC soundbar or AVR (verified on a Sonos Beam). A single `VolumeControl`, not a `Dsp` subclass
  - Frames go out in order, 150 ms apart (100 ms after a key press, 500 ms after a status query, 750 ms after a discovery request). The spacing, the 300 ms status read after a key and the 1.5 s wait for Report Audio Status are awaited, so no thread is held while waiting, and the reply wait's timer is cancelled as soon as the status arrives

## Usage

Drivers derive from `Dsp` (except `CecAudioSystem`, which is a `VolumeControl` over a CEC `SerialClient`) and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
