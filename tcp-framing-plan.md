# Plan: Opt-in message framing for stream transports

**Status: READY TO IMPLEMENT** — design agreed; all six initially-unsure devices (VISCA,
Lumens CL511, Lumens LC300, Extron Sharelink Pro, LG Commercial, Colorlight Class B) resolved
2026-06-12 against vendor specs in `OneDrive .../Resources/Documentation/`.

## Context

`AvCodersTcpClient` (and the SSH shell stream) hand drivers whatever a single read returns.
Message boundaries therefore depend on TCP segmentation luck, which fails three ways:

1. **Splitting** — a response straddling two reads arrives as two fragments that each fail to
   parse. Rare for small LAN messages, *guaranteed* for anything over the 2048-byte receive
   buffer (Cisco RoomOS phonebook dumps, Extron config readback).
2. **Coalescing** — multiple responses in one read. Text drivers that split on `\r` survive;
   binary parsers (PhilipsSICP) parse the first frame and discard the rest.
3. **Intermediaries** — serial-over-TCP gateways (SVSI decoder passthrough) forward bytes in
   bursts timed by *their* gathering logic, not the tunnelled device's message boundaries.

Evidence the problem is already being worked around: **SamsungMdc** and **NecUhdExternalControl**
each implement a private `_gather` buffer to reassemble frames across reads — the same logic,
written twice, unavailable to the other ~20 stream drivers.

Constraints from design discussion (2026-06-12):

- **Not all devices have delimiters or fixed sizes** (prompts like Extron `Password:`, banners).
  Framing must be opt-in per client; the default is passthrough — byte-identical to today.
- **Serial passthrough bursts** (SVSI): content-based framers are immune by construction; the
  idle-flush timeout must be configurable (default ~200ms) because intra-message gaps at low
  baud rates plus gateway jitter can exceed aggressive thresholds.
- **Keep-alive probe bytes** (`0x00` between AV-Coders client/server pairs): the current
  TcpServer filter only catches a probe that arrives as its own read; a probe coalesced with
  data passes through as `"\0real data"` today (proven by test). Framers must support
  framer-aware inter-frame noise bytes — scrubbed *between* frames only, since `0x00` is
  legitimate payload inside binary frames (MDC data/checksums).

## Design

### Interface (AVCoders.Core)

```csharp
public interface IMessageFramer
{
    /// Feed a raw chunk from the transport; returns zero or more complete frames.
    IReadOnlyList<byte[]> Ingest(byte[] chunk);
}
```

Applied inside the TCP/SSH receive path, between the read and `InvokeResponseHandlers`.
Handler signatures are unchanged; drivers see complete frames instead of raw chunks.
UDP (datagram boundaries preserved natively), REST, MQTT and SNMP are untouched.

### Built-in framers

| Framer | Behaviour | Safety rails |
| --- | --- | --- |
| `PassthroughFramer` *(default)* | Emits each chunk as-is — current behaviour, no opt-in required | — |
| `DelimiterFramer(byte[] delimiter, byte[]? noiseBytes)` | Buffers until delimiter; emits frame without delimiter; discards empty frames; scrubs noise bytes between frames | 64KB buffer cap → flush + LogWarning |
| `LengthPrefixedFramer(spec, byte? syncByte, byte[]? noiseBytes)` | Reads declared length from a header offset (+adjust for overhead); validates minimum frame size; with a sync byte, scans forward to resynchronise after garbage | garbage-skip cap; 64KB buffer cap |
| `TerminatorByteFramer(byte terminator, byte? syncByte)` | Buffers until terminator byte (VISCA `0xFF`, NEC `0x0D` with `0x01` SOH sync) | 64KB buffer cap |
| `FixedLengthFramer(int frameSize, byte? syncByte)` | Emits every N bytes; resyncs on sync byte if present (DyNet `0x1C`) | resync scan cap |
| `IdleFlushFramer(IMessageFramer? inner, TimeSpan timeout)` | Decorator: passes through the inner framer; flushes any buffered remainder after `timeout` of line silence. Standalone (no inner) = pure burst-gap framing for prompt-style devices | timeout configurable, default 200ms |

### Wiring

`SetFramer(IMessageFramer)` on `IpComms` (meaningful for TCP/SSH; no-op elsewhere). Drivers
that know their protocol call it in their constructor — same pattern as the existing
`client.ResponseByteHandlers +=` wiring — so applications change nothing. Applications can
override after construction for unusual topologies.

`AvCodersTcpServer`'s probe filtering becomes framer configuration (noise byte `0x00`),
replacing the read-boundary-dependent single-byte check and closing the `"\0real data"` hole.

## Implementation steps

1. **Core**: `IMessageFramer` + the six framers + exhaustive unit tests (pure logic, no
   sockets — split/coalesced/noise/garbage/oversize cases per framer).
2. **Transports**: integrate into `AvCodersTcpClient.Receive` and `AvCodersSshClient.Receive`;
   `SetFramer` on `IpComms`; extend `CommunicationClientsTest` with split-frame, coalesced-frame
   and noise-byte tests through real loopback sockets.
3. **Migrate the two self-buffering drivers**: replace `_gather` logic in SamsungMdc
   (`LengthPrefixed`, sync `0xAA`, length at [3], total = len+5) and NecUhdExternalControl
   (`TerminatorByte 0x0D`, sync `0x01`) — behaviour-equivalent, existing tests must pass.
4. **Opt-in rollout to Certain drivers** (table below), one domain per PR, relying on each
   domain's existing response-parsing tests plus new split/coalesce theory cases.
5. **Unsure drivers**: assign framers once protocol docs are confirmed (Open Questions).
6. TcpServer noise-byte configuration + retire the single-read probe check.

## Driver framer mapping (from 2026-06-12 inventory)

### Stream drivers (TCP/SSH) — framing applies

| Driver | Framer | Sync | Confidence |
| --- | --- | --- | --- |
| PjLink | `Delimiter("\r")` | — | Certain |
| SonySimpleIpControl | `Delimiter("\n")` | — | Certain |
| ExtronDtpCpxx, ExtronIN18xx | `Delimiter("\r")` | — | Certain |
| ExtronIN16xx, ExtronSW | `Delimiter("\r")` | — | Likely |
| ExtronSmp351 | `Delimiter("\r")` | — | Likely |
| ExtronAnnotator401 | `Delimiter("\r")` | — | Likely |
| SvsiEncoder / SvsiDecoder | `Delimiter("\r")` | — | Certain |
| BlustreamAmf41W | `Delimiter("\r")` | — | Likely |
| BiampTtp | `Delimiter("\n")` | — | Certain |
| QsysEcp | `Delimiter("\n")` | — | Certain |
| BoseCspSoIP | `Delimiter("\r")` | — | Certain |
| CiscoRoomOs (+ phonebook parser) | `Delimiter("\n")` | — | Certain — also fixes >2048-byte phonebook responses |
| ExterityTci | `Delimiter("\n")` + `IdleFlush` for login prompts | — | Likely |
| CBusSerialInterface | `Delimiter(0x0A)` | `0x5C` frame start | Certain |
| SamsungMdc | `LengthPrefixed(len at [3], total = len+5)` | `0xAA` | Certain — replaces `_gather` |
| PhilipsSICP | `LengthPrefixed(byte 0 = total, min 6)` | — | Certain |
| NecUhdExternalControl | `TerminatorByte(0x0D)` | `0x01` SOH | Certain — replaces `_gather` |
| DyNet | `FixedLength(8)` | `0x1C` | Certain (send-mostly; responses unparsed today) |
| Navigator / NavEncoder / NavDecoder | Passthrough (Navigator demultiplexes `{device}` wrapper itself) | — | Likely |
| LGCommercial | `Delimiter("x")` — responses end with literal ASCII `x`, not CR | — | Certain — LG External Control doc p.6: ack format is `[Command2][ ][Set ID][ ][OK/NG][Data][x]`. Safe: ack payload is a letter + hex digits, so `x` cannot occur mid-frame. Migration note: the driver's switch cases currently match `"01x"`/`"00x"` *including* the terminator — they become `"01"`/`"00"` once the framer strips it |
| SonyVisca / AverVisca (raw mode, `useIpHeaders=false`) | `TerminatorByte(0xFF)` | reply header `0x90`–`0xF0` | Certain — VISCA spec p.4: "When the terminator is FFH, it signifies the end of the packet"; packets 3–16 bytes; `0xFF` never appears in payload |
| SonyVisca / AverVisca (over-IP mode, `useIpHeaders=true`) | None — VISCA-over-IP is **UDP** (spec p.12), datagram boundaries are native. Wire with `AvCodersUdpClient`. If ever run over TCP: `LengthPrefixed(bytes 2–3 big-endian, +8 header)` | payload type `0x01`/`0x02` | Certain |
| LumensCL511 | `FixedLength(6)` — when response parsing is added; driver is currently send-only | `0xA0` STX / `0xAF` ETX | Certain — RS157 §4: command and return packets are both fixed 6 bytes |
| LumensLc300 | Custom `Lc300Framer` (composite — see note below) | `0x55` / `0x23` / `0x15` | Certain — RS182 §2.2/§3.2 |
| ExtronSharelinkPro | `Delimiter("\r\n")` (SSH, port 22023) | — | Certain — SLP 2500 manual p.62: "All responses … end with a carriage return and a line feed". Login prompts are consumed by SSH.NET keyboard-interactive auth, so no idle-flush needed |
| ColorlightDeviceControlProtocolClassB | Custom `ColorlightClassBFramer` — typed fixed-size frames (see note below) | first byte = frame type | Certain — Class B protocol doc §5.2 |

### No framer needed

- **Message-oriented transports**: SonyRest, AutomateVX, ServerEdgePdu, Grandview,
  BondDevice/BondGroup, VitecHttp/VitecServer, TriplePlay (REST/HTTP); Zigbee2MqttLight (MQTT);
  EatonPdu (SNMP); NovaStarH5 (UDP datagrams); TybaTurn2 (HTTP SSE).
- **Serial-only**: SonySerialControl, CecDisplay, MotoluxBlindTransmitter, TemperzoneUc8
  (Modbus RTU). *Note:* if any of these are deployed over serial-to-TCP gateways with a
  TcpClient, the relevant framer applies — same mechanism, decided at wiring time.
- **Send-only** (no response parsing): ScreenTechnicsConnect, MotoluxBlindTransmitter.

## Open questions — protocol docs needed

| Device | What's unconfirmed |
| --- | --- |
| ~~LG Commercial displays~~ | **Resolved 2026-06-12** from `OneDrive .../Documentation/LG/LG TV External Control (RS232 and IP).pdf` p.6: commands end CR, but acknowledgements end with literal ASCII `x` and no CR → `Delimiter("x")`. |
| ~~Sony/Aver VISCA~~ | **Resolved 2026-06-12** from `OneDrive .../Documentation/Sony/VISCA.pdf`: raw mode is `0xFF`-terminated (3–16 byte packets); over-IP mode is UDP with an 8-byte header (2 payload type, 2 length big-endian, 4 sequence) — no TCP framer needed. |
| ~~Lumens CL511~~ | **Resolved 2026-06-12** from `OneDrive .../Documentation/Lumens/CL511/RS157`: fixed 6-byte packets both directions (STX `0xA0` … ETX `0xAF`). The inventory's "6–9 byte response parsing" claim was wrong — the driver is send-only today. `FixedLength(6)` applies if/when response parsing is added (TCP port 9997). |
| ~~Lumens LC300~~ | **Resolved 2026-06-12** from `OneDrive .../Documentation/Lumens/LC 300/RS182`: the length byte counts Address→Parameters, so total frame = length + 4 (header, ext-header, length, end). But replies come in **three shapes**, so a canned framer doesn't fit — see `Lc300Framer` note below. |
| ~~Extron Sharelink Pro~~ | **Resolved 2026-06-12** from `OneDrive .../Documentation/Extron/slp_2500_68-3824-01_B.pdf` p.62: all device→host responses end CR/LF → `Delimiter("\r\n")`. (Commands host→device use CR alone; the PSWD command requires literal CR delimiters — outbound only, no framing impact.) |
| ~~Colorlight (Class B protocol)~~ | **Resolved 2026-06-12** from `OneDrive .../Documentation/Colorlight/Colorlight Device Control Protocol-ClassB_en.pdf`: typed fixed-size frames — see `ColorlightClassBFramer` note. |

### Lc300Framer — why LC300 needs a custom framer

Per RS182, the LC300 emits three distinct frame shapes on the same connection:

1. **Command replies** — `0x55 0xF0 <len> <addr> <action> <cmd cmd> <params> 0x0D`, where
   `len` counts Address→Parameters (total frame = len + 4). The ACK/NAK echoes the *received
   packet* including parameter bytes, which may be binary — so `0x0D` can appear mid-frame
   and a pure terminator framer is unsafe for this shape.
2. **Bare NAK** — on unparseable input the device returns "NAK code and End code only":
   `0x15 0x0D`, with no header or length. Breaks a pure length-prefix framer.
3. **Event notifications** — `0x23 <event code ×2> <params> 0x0D`, no length byte.

`Lc300Framer` dispatches on the first buffered byte: `0x55` → length-prefixed (len+4),
`0x23`/`0x15` → `0x0D`-terminated; anything else → skip to next recognisable header
(resync). This is exactly the case the open `IMessageFramer` interface exists for —
protocols that don't fit a single canned strategy get a small protocol-specific
implementation instead of being forced onto passthrough.

### ColorlightClassBFramer — typed fixed-size frames

Per the Class B protocol doc (TCP port 9999), there is **no universal length field**.
Host→sender command frames carry a big-endian 16-bit total length at bytes 2–3
(heartbeat `99 99 00 08`, detect `eb 00 00 09`, sender query `01 00 00 22`), but
**response frames do not follow this**: the 0xEA network-params response happens to have
`16 00` (22, little-endian) at that offset, while the 0xF1 sender-params response has
plain zeros there — its size is defined solely by its documented field table (a large
fixed structure, fields through address ~184+). Heartbeats are exchanged both directions
once per second, so the host receives 8-byte `0x99 0x99 …` frames interleaved with
responses; reading the echoed heartbeat's bytes 2–3 as a little-endian length gives 2048
and would stall a naive length-prefixed framer.

`ColorlightClassBFramer` therefore dispatches on the first byte against a per-type size
table transcribed from the doc (`0x99` heartbeat = 8; `0xEA` = 22; `0xF1` = size per
§5.2.2.2; further types added as the driver grows parsing). Unknown frame types: skip-scan
to the next known type byte (resync) with a logged warning. The driver currently parses
only heartbeat echoes, so the initial table is small; the doc has exact sizes for every
response frame when needed.

## Side findings from doc verification

**SonyVisca IP-header bug (out of scope here, should be fixed separately):** the spec defines
the over-IP sequence number as a 4-byte big-endian counter incremented per message (wrapping
to 0), but `SonyVisca.PayloadWithIpHeader` (Camera/SonyVisca.cs:71-75) sends
`0xFF 0xFF 0xFF <byte>` — i.e. a sequence of `0xFFFFFF00`–`0xFFFFFFFF` that wraps
non-monotonically every 256 messages. Strict cameras reply `ERROR 0x0F pp=01`
("abnormality in the sequence number"). The driver also never sends the RESET control
command (`0x01`) on connect, which the spec uses to zero the counter. The `SequenceHeader
[0xFF,0xFF,0xFF]` constant appears to be a misreading of the spec's 4-byte field.
`HandleResponse` is also still a TODO, and over-IP replies carry the 8-byte header that
parsing will need to skip.

## Non-goals

- No framing by default — passthrough preserves today's behaviour for every existing wiring.
- No driver-side buffering helpers as the primary mechanism (that path produced two private
  copies of `_gather` already).
- No change to UDP/REST/MQTT/SNMP transports.
- The fragile `CommandStringFormat`/encoding question (receive always decodes ASCII today) is
  adjacent but separate; revisit after framing lands.
