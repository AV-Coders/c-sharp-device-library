# AVCoders.Matrix

Matrix switcher and AV-over-IP drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Matrix
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Matrix). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `ExtronIn16Xx`, `ExtronSw`, `ExtronDtpCpxx` (DTP CrossPoint 82/84/86/108), `ExtronDtp2Cp82` (DTP2 CrossPoint 82)
  - `ExtronSw`, `ExtronIn18Xx` and `ExtronDtp2Cp82` show the model, firmware, part number, unit name and MAC as
    details. The model comes from the model reply on connect, which then triggers the other queries, after its status
    queries. Any still unanswered are sent again at the end of each 20 s poll
  - `ExtronDtpCpxx` shows the model, and the firmware and part number from the connect banner
    (`(c) Copyright 2025, Extron Electronics, DTPCP108, V2.04, 60-1381-01`); a banner it can't read is logged as a
    warning with the raw text. After the model reply's port and tie queries it reads the firmware build (`*Q`), part
    number (`N`), unit name (`Esc CN`) and MAC (`Esc CH`). After the `0TC` keepalive in every 20 s poll it reads the
    system status (`S`), shown as received after `Sts00*`: supply voltage, temperature and fan speeds (DTP
    CrossPoint 84 user guide 68-2349-01 Rev. D, p. 62), then any identity read still unanswered
  - `ExtronIn18Xx` and `ExtronDtp2Cp82` read the internal temperature after the status queries in every 20 s poll and
    show the value exactly as received. An empty value shows the raw reply
  - Every command is tracked in send order, and each reply and each `E` code goes to the command it answers, since
    SIS answers in order on a connection. A tie command is answered only by a tie reply with its input, output and
    plane (`In2 All`, `In2*1 All`, `Out1 In2 All`, leading zeros ignored; output 0 means all outputs). A notice the
    switcher sends unasked in verbose mode 3, such as a tie made from the front panel or the room processor, updates
    the state and answers no command, unless it is exactly the reply a command is waiting for
  - A rejected control or status command (route, tie, name, HDCP, poll) raises a momentary `rejected-command` issue
    naming the command and the code, except that `ExtronDtpCpxx` logs the `E13`s to its output HDCP probes at Debug (the whole-output query on a split output, and the A/B queries on an unsplit
    one). An `E` code with no command awaiting a reply logs a warning with the raw code. A command with no reply
    within 5 s is logged at Debug at the next poll
  - A metadata query the switcher rejects with `E10`, `E13` or `E14` shows `Not supported: ` and the code in its
    detail, with no issue, and isn't sent again until the switcher reconnects
  - Any other `E` code (e.g. `E22` busy), or no reply within 5 s (checked at the next poll), is
    a miss and the query is sent again at the next poll. The third miss in a row raises a Minor issue for that query
    carrying the last raw reply or "no reply". The first good reply resolves it
  - A tagged model reply `ExtronSw` or `ExtronIn18Xx` can't read raises a `model` issue
- `ExtronIn18Xx` — IN1806 / IN1808 scaling switchers. Inputs are discovered from the model reply
  (`EndpointsChangedHandlers` fires when the count changes; existing ports are kept) and carry signal presence and
  HDCP status. Output 1A (HDMI) and 1B (DTP2/HDBT) exist from construction as `GetOutputs()[0]` and `[1]` and carry
  sink detection and HDCP status. The HDMI loop out is not exposed. `NumberOfInputs`, which the route checks use, is
  the constructor's `numberOfInputs` until the model reply arrives, then the discovered count; a model reply whose
  count differs from the constructor's logs a warning naming both.
- `ExtronAnnotator401VideoMatrix` — the HDMI input and two HDMI outputs of an Extron Annotator 401 as a
  routing-free `VideoMatrix`, sharing the annotator's communication client with the `AVCoders.Annotator` driver.
  The endpoints exist from construction and carry sync state and HDCP status; both outputs always show the only
  input, so the routing methods only log.
- `CiscoRoomOsVideoMatrix` — the video inputs and outputs of a Cisco RoomOS / CE codec as a routing-free
  `VideoMatrix`, sharing the codec's communication client with the `AVCoders.Conference` driver. Connectors are
  discovered from the codec's status (`EndpointsChangedHandlers` fires as they appear) and carry sync state,
  resolution, HDCP and history. Input names come from the codec's connector configuration; outputs stay "Output n"
  and expose the connected display's EDID name as `ConnectedDeviceName`, logged on change. Each output shows the
  display's preferred format and each input its source's format status as details, removed when the session drops.
- `SvsiEncoder`, `SvsiDecoder` — show the serial number, name, MAC, IP, firmware, FPGA and web versions from the
  status dump as details. A `UPDFAILED` other than 0 raises an `update-failed` issue. The decoder also shows its
  video, audio and frame drop counters, and `NEEDVSTRM` as Needs Video Stream (Warning when it isn't 0)
- `AntricaEncoder` — the HDMI input of an Antrica ANT-35000 series encoder, polled every 10 seconds over its HTTP
  API through a `RestComms` client (`AvCodersRestClient`). Reports signal presence (`GIS_VIDEOLOSS1`) and the
  detected input format (`VID_INPUTFORMAT`, read as decimal or `0x` hex) as the input resolution. HDCP is always
  `NotSupported`. HTTP API authentication (`NET_USEHTTPAPIAUTH`) is not supported.
- `BlustreamAmf41W`
- `Navigator`, `NavEncoder`, `NavDecoder` — endpoints show their serial number, hostname and the `AudI`, `StrmI` and
  `Lnk` fields of the `I` reply as details. The Navigator shows encoder and decoder online, offline and present but
  not connected counts from each inventory report, which it reads on connect and in `SeedSystemState`
  - Inventory states `0` (unassigned), `1` (online), `2` (offline) and `3` (present but not connected) are valid
    (NAVigator guide 68-2740-01_D p.137). The offline and not-connected counts are Warning tone when non-zero. A
    registered endpoint in state `2` or `3` is Disconnected. State `1` sets it Connected only in the first report after
    the Navigator connects; after that only a relayed reply does, so an endpoint that's listed online but never
    answers still reaches its Critical issue. Any other state raises a Minor `inventory-read` issue
  - After the Navigator connects, each endpoint is sent `3CV` and its reads (`DNUM`, `CN`, `98I`, `I`) wait for its
    `Vrb3` reply, or 3 s without one, so the replies come back tagged. The 90 s endpoint poll is unchanged
  - Every 30 s while connected, the Navigator sends `Inventory*I*RPRT` and `Inventory*O*RPRT` again. That keeps the
    SSH session busy (the NAVigator closes an idle session after about 75 s) and refreshes which endpoints are
    online. `Ties*A*RPRT` is sent on connect only, and endpoint polls are unchanged
  - A bare `E13` while the inventory lists offline or not-connected encoders or decoders is a missed reply for the oldest unanswered
    tunnel command to a registered endpoint of that type that isn't answering. It's logged at Debug on that endpoint
    with the command and the raw code. The endpoint's own three-poll offline handling is unchanged. Any other error
    code, or an `E13` no such command can take, is a Warning as before
  - A tunnel command to an endpoint that's answering (Connected) with no relayed reply and no `E13` within 20 s is
    unanswered. More than 5 in a row, with no relayed reply to a later command, raise a Critical `endpoint-relay`
    issue naming the latest command. The next relayed reply to one of the Navigator's own commands resolves it.
    Replies the Navigator relays for another controller don't count, and a reconnect starts the count again
  - Encoders and decoders on the Navigator that aren't registered to this module raise Minor issues. An online
    (`1`) inventory number that no registered endpoint has claimed by `DNUM` is asked its IP with
    `{<n>i|o:Esc CI}` (NAVigator guide 68-2740-01_D p.139 for numbered encapsulation, NAV E 501 guide p.74 and
    NAV SD 501 guide p.75 for `CI`). The bench reply is `{1442i}Ipi 10.1.3.210`. A reply whose payload is an IP
    answers the oldest awaiting `CI` query whatever its key. A reply that isn't a registered IP raises
    `unregistered:<n>i|o` with the number, type, reported IP and raw reply. A query with no reply within 20 s, or an
    unreadable reply, is asked again at the next inventory poll. Once the number has been unclaimed for a full
    endpoint poll (90 s), so endpoints that report `DNUM` late raise nothing, it raises the same issue with
    `IP unknown: no reply to {<n>i|o}:CI`. The issue resolves when a registered endpoint claims the number or the
    inventory shows it as `0`
  - Offline (`2`) and not-connected (`3`) devices can't report an IP. When a type has more such unclaimed numbers than registered
    endpoints still without a device number, `unclaimed-offline:i|o` (Minor) lists both. With equal counts it stays
    quiet, and the registered endpoints' own not-responding issues show instead
  - A NAV endpoint's `ConnectionReason` is the Navigator's own while the Navigator's SSH client is disconnected, and
    `TimedOut` ("… is not responding via the Navigator tunnel") while the Navigator is connected but the endpoint
    isn't answering or the inventory lists it as offline
- `AVoIPEndpoint`

## Usage

Drivers derive from `VideoMatrix` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
