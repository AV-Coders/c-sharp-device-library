# AVCoders.Display

Display, projector and LED-wall drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Display
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Display). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `PjLink`
- `SamsungMdc`
- `SonySerialControl`, `SonySimpleIpControl`, `SonyRest`
- `NecUhdExternalControl`
- `PhilipsSICP`
- `LGCommercial`
- `CecDisplay`
- `NovaStarH5`
- `ColorlightDeviceControlProtocolClassB`

## Usage

Wire a driver to a transport (from `AVCoders.CommunicationClients`) and subscribe to its state-change events:

```csharp
var comms   = new AvCodersTcpClient("192.168.1.50", 4352, "Projector", CommandStringFormat.Ascii);
var display = new PjLink(comms, "Projector", defaultInput);

display.OnPowerStateChanged += state => Console.WriteLine($"Power: {state}");
display.PowerOn();
```

`PjLink` discovers the projector's inputs from `INST`/`INNM` on class-2 projectors and rebuilds `SupportedInputs` (raising `OnSupportedInputsChanged`) once the first complete name pass lands. Until then the constructor map applies (default `31→Hdmi1`, `32→Hdmi2`, ...), so pass an explicit `inputMap` when the projector's HDMI codes differ (e.g. Epson uses 32/33/36) and the input must be right from the very first command.

`CecDisplay` follows each power poll with Give Physical Address, Give OSD Name, Give Device Vendor ID and Get CEC Version, for whichever the TV hasn't answered yet, and shows the answers as Details. Each request waits 500 ms after the power poll or the request before it, then goes out only when no command is waiting. The wait doesn't hold up commands, so power, input and key commands always go out first. A connection change drops any requests still waiting. A Feature Abort for unrecognised opcode (0x00), cannot provide source (0x02) or invalid operand (0x03) shows `Not supported: <frame>` and stops that request. Any other reason counts as no answer, including not in correct mode to respond (0x01) and refused (0x04), which HDMI 1.3a uses for a passing state (CEC 13.8.2, page CEC-35; CEC 12.3, page CEC-21, suggests retrying). The reason codes are in HDMI 1.3a, CEC Table 26 (page CEC-79), where 0x05 and above are reserved. After 3 polls with no real answer to Give Physical Address or Give Device Vendor ID, it raises a Minor read issue and asks again every 10th poll. The driver asks from the unregistered address 15, which a TV may ignore unless the reply is broadcast (CEC 12.2, page CEC-20). Set OSD Name and CEC Version are directed replies, so after 3 polls with no answer to those it raises no issue, shows `No answer (the TV may ignore requests from address 15): <last reply>` as the Detail, and keeps asking every 10th poll.

`SamsungMdc` and `LGCommercial` read their identity and health Details while the display is Off, in the same poll as the power poll. Samsung sends them straight after the power poll, judging power from the previous poll's reply as it always has; while On it polls input, volume and mute as before and reads nothing. LG polls power, input, volume and mute every poll whatever the power state, and sends the reads after the mute poll while Off. Which reads need the display On is fixed in code from the protocol manuals, and a read the manual says nothing about is read while Off. Until a read has run, its Detail shows `Not read yet: read while the display is off`. Identity reads go out on each Off poll until answered, health reads on the first Off poll and every 5th after. A NAK (Samsung) or NG (LG) shows `Not supported: <raw reply>` and stops that read until the display reconnects; 3 missed or unreadable replies in a row raise a Minor read issue, and the first good reply resolves it.

Both drivers track their reads with `SentCommandTracker` and match each reply to the read it answers: Samsung by command byte, LG by reply character. A read reply, NAK or NG that no read of ours is waiting for, such as another controller's, is logged at Debug and counts neither for nor against a read. Power, input, volume and mute replies still update the state whoever asked. When LG reconnects it restarts the poll worker, so the first poll goes out straight away and never runs alongside another.

Every `SamsungMdc` read is read while Off: none of their entries in the Samsung *MDC Protocol v15.1c* (`MultipleDisplayControl(MDC)+Protocol_v15.1c.pdf`) gives a power-state condition.

| Read | Query | PDF page |
| --- | --- | --- |
| Serial number | `0x0B` | 36 |
| Software version | `0x0E` | 38 |
| Model name | `0x8A` | 169 |
| Display status: lamp, temperature, brightness sensor, signal, fan | `0x0D` | 37 |
| Panel on time | `0x83` | 162 |

The MD32C–MD55C user manual (`BN46-00098S-Eng(1).pdf`, PDF p.55) says MDC over RJ45 isn't available at all while that model is powered off. That covers every command, the power poll included, not a particular read, so it puts no read in the On list.

Every `LGCommercial` read is read while Off: serial number `fy`, software version `fz`, temperature `dn` and signal check `sv 02`. Neither LG manual lists these commands (`LG TV External Control (RS232 and IP).pdf`, command reference list p.5; `MFL71807902_03_Q_240507+MFL71807803_04_S_240507.pdf`, 50/55/65UR640S command reference list p.36), so nothing specifies On for them.

`NecUhdExternalControl` and `PhilipsSICP` read their identity and health Details the same way, while the display is Off and in the same poll as the power poll. NEC judges power from this poll's reply after its 1.5 s wait, as it always has, and sends the reads 1.5 s apart. Philips judges power from the previous poll's reply, as it always has, and sends the reads 3 s apart. While On, NEC polls input and volume and Philips polls input, volume and mute, as before, and neither reads anything. Until a read has run, its Detail shows `Not read yet: read while the display is off`. Identity reads go out on each Off poll until answered. NEC's self-diagnosis goes out on the first Off poll and every 4th after, Philips's temperature on the first and every 5th.

Both drivers track every command they send (polls, reads and control commands) in send order with `SentCommandTracker`, and match each reply to the command it answers: NEC by message type and op code, Philips by command code. A NEC NULL message, or a Philips NACK or NAV, goes to the oldest command still waiting. A rejected power, input, volume or mute command is reported as before: a NEC NULL is logged at Debug, a NEC error result is logged as a Warning with an Error event, and a Philips NACK or NAV raises a momentary `rejected-command` issue naming the command and the raw reply. A Philips NACK or NAV with no command waiting is logged as a Warning. Only a read's own rejection counts against that read. A Philips NAV, a NEC firmware error result, or 3 NEC NULL messages in a row show `Not supported: <raw reply>` and stop that read until the display reconnects. A Philips NACK, or a missed or unreadable reply, counts towards the Minor read issue raised on the 3rd in a row. A reply that answers none of our commands, such as another controller polling the same display, still updates power, input and volume as before but counts neither for nor against a read. A Details reply with no read of ours waiting is logged at Debug and dropped. A Philips command addressed to a group (group ID 1–254) gets no ACK or report (*The SICP Commands Document V2.10*, p.9), so it isn't tracked.

`NecUhdExternalControl` starts polling 1.5 s after its connection comes up, or 1.5 s after construction when the client is already connected (a serial port). Starts requested within that 1.5 s give one poll. `PhilipsSICP` starts the same way, 1 s after its connection comes up or after construction.

Every `NecUhdExternalControl` read is read while Off. The only standby condition in the NEC *External Control* manual (`uhd-external_control.pdf`) is that Proof of Play can't be read while the display is Off (p.124), and the driver doesn't read Proof of Play.

| Read | Command | PDF page |
| --- | --- | --- |
| Model name | `C217` | 77 |
| Serial number | `C216` | 75 |
| Firmware version | `CA02` type `00` | 87 |
| MAC address | `C220` `00` | 120 |
| Self-diagnosis: faults and no signal | `B1` | 73 |

The manual's MAC address read on p.120 is `C22A` `02`, with the reply `C32A`. The driver sends `C220` `00`.

Every `PhilipsSICP` read is read while Off: none of their entries in *The SICP Commands Document V2.10* (`The SICP Commands Document V2.10.pdf`) gives a power-state condition.

| Read | Get | PDF page |
| --- | --- | --- |
| Model number | `A1` `00` | 10 |
| Firmware version | `A1` `01` | 10 |
| Build date | `A1` `02` | 10 |
| Serial number | `15` | 12 |
| Temperature | `2F` | 11 |

A model information report (`A1`) doesn't say which field it answers (p.11), so a late reply can't be matched to its read. After an `A1` read goes unanswered, the other `A1` reads wait for the next poll. A value that is blank, or only NULs or spaces, shows the raw reply.

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for logging, tracing and the full architecture overview.
