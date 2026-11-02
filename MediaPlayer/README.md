# AVCoders.MediaPlayer

Media player, recorder and IPTV drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.MediaPlayer
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.MediaPlayer). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `LumensLc300`
- `ExtronSmp351`. It sets verbose mode 3 (`Esc 3CV`) 500 ms after connecting, so every reply is tagged and the SMP
  also reports front panel and other sessions' changes (SMP 300 Series User Guide 68-2238-01 P, pp. 108 and 116)
  - Every command it sends is tracked in send order: verbose mode, the `I` poll each second, the `56I`/`57I`/`58I`
    USB polls, record, stop, pause, chapter marker and the metadata reads. A tagged reply (`Vrb3`, `Inf*`, `Inf56`,
    `RcdrY1`, `RcdrB`, `Bld` and so on) answers the oldest command waiting for that tag (record commands: p. 123).
    A bare `E` code answers the oldest command still waiting for a reply, because the SMP answers in order
    (error codes: p. 109)
  - A tagged line that answers nothing, such as a verbose-mode-3 `RcdrY1`, `RcdrN1*usbrear` or `RecStart05` notice,
    still updates state and doesn't take another command's reply
  - An error to record, stop, pause or the marker raises a momentary `error-reply` issue, "The SMP returned an
    error: <code> to <command>". An error to verbose mode or a poll is logged as a Warning with the code and the
    command, and an error with no command waiting as a Warning with the raw code; neither raises an issue. Any of
    these commands with no reply within 1 s, checked at the next poll, is logged at Debug
  - Tracked commands are forgotten when the connection changes. Past 256 commands waiting, the oldest is dropped
    with a Warning
  - Metadata reads are one per poll after verbose mode: firmware (`*Q`), part number (`N`), model (`1I`),
    serial (`99I`), MAC (`98I`), then temperature (`Esc 20STAT`) and alarms (`39I`), which repeat every 10 polls
    (pp. 114 and 116). An `E10`, `E13` or `E14` to a read shows `Not supported: ` and the code in its detail, with
    no issue, and the read isn't sent again until the SMP reconnects. Any other code, a garbled alarm list or no
    reply within 1 s is a miss. A missed identity read is asked again. The third miss in a row raises a Minor issue
    for that read carrying the last raw reply or "no reply", and the first good reply resolves it
  - The alarm list is read in the manual's `[name:alarm_name],[level:alarm_level]` form (p. 116). Each active alarm
    raises its own issue, with its severity from the manual's alarm priorities (p. 88), matched in any case:
    Notify: Major; Display, Log: Minor. A Disabled alarm shows in the Alarms detail only and raises no issue. A level
    the manual doesn't list raises a Minor issue that carries the raw level. The level words a real SMP sends are
    unverified
  - Free space and record time remaining show per destination from the `I` reply, worked out again only when the
    reply changes. An `I` reply too short to give the recording state is logged as a Warning with the raw reply.
    The USB polls show each drive's partitions. A USB status that can't be read is logged as a Warning and shown
    raw in its detail, with no issue
  - No SMP has been on a bench, so no fixture in the SMP tests is a bench capture: they are invented or from the
    manual
- `MagewellDirectorPlus`
- `TriplePlay`
- `VitecHttp`, `VitecServer`
- `ExterityTci`
- `AppleTvCec` — power, key and channel sends go out in order with the CEC frame spacing; the calls return at once and don't wait out the spacing

## Usage

Drivers talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.
