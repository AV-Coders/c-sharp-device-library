# AVCoders.Camera

PTZ camera and auto-tracking drivers for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library). Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.Camera
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.Camera). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Drivers

- `SonyVisca`
- `AverVisca` — implements `ITrackingCamera`
- `CrestronIvCam` (IV-CAM-P12, IV-CAM-P20)
- `CrestronIvCamI12` (IV-CAM-I12) — implements `ITrackingCamera`
- `LumensCL510`
- `LumensCL511`
- `AutomateVX` (1 Beyond)

## Usage

Drivers derive from `CameraBase` and talk to hardware through a transport from `AVCoders.CommunicationClients`. See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring, logging and tracing setup.

`SonyVisca` and the drivers built on it (`AverVisca`, `CrestronIvCam`, `CrestronIvCamI12`) poll in the background from construction. `Dispose()` stops the poll and unsubscribes the driver from its client.

### IV-CAM-I12 multi-camera switching

`CrestronIvCamI12` switches its own USB output between itself (camera 1) and up to four companion cameras (2–5) on the same TCP connection (port 5500). The companions get no commands. `SetCompanionIp`, `ClearCompanionIps`, `SelectCamera` and `SetIntelligentSwitching` drive it; `ActiveCamera`, `IntelligentSwitching` and `CompanionIps` report it. Each poll refreshes them, and `QueryStatus()` (on every `SonyVisca`-based driver) runs the poll inquiries immediately. While intelligent switching is on, the active camera is also checked every second. Selecting a camera by hand turns intelligent switching off until it's turned back on. A companion slot reads `null` until the camera has reported it and `""` when the camera reports it empty. `SetCompanionIp` returns `false` when it rejects the slot or the address. Requires camera firmware 2.1.02 or later.

### Cameras that can't reply

If the camera is wired one-way (transmit only), set `DeviceSendsResponses = false` on the driver. Drivers that normally wait for replies (`SonyVisca`, `AverVisca`) then stop polling and update power, preset, tracking and communication state as each command is handed to the transport. With replies on, the communication state becomes `Okay` only on a reply from the camera. `LumensCL510` and `LumensCL511` never read replies, so they report `false` from construction and logs a warning if asked to change.

Assumed feedback is only as good as the transport: if a client queues or drops bytes while it is disconnected, the driver cannot tell and still reports the command as done.

### How `SonyVisca` matches replies

With IP headers (VISCA over IP), every reply echoes the sequence number of the message it answers, and the driver matches on that.

Without IP headers (serial, or raw TCP such as the IV-CAM's port 5500) there is no sequence number, so the driver keeps every command and inquiry it sent, in send order, and matches each reply to the one it answers. This follows the Sony VISCA protocol (`Documentation\Sony\VISCA.pdf` p5): a command gets an ACK and an inquiry doesn't; the completion or error carries the socket number the ACK named; socket 0 is for inquiries and requests that use no socket; and the camera has two command buffers, so a completion can arrive after later traffic.

- An ACK goes to the oldest command still waiting for one, which then sits in the socket the ACK named.
- An inquiry reply goes to the oldest waiting inquiry whose reply has that length: 4 bytes for power, 5 for pan/tilt status and 10 for the version (VISCA.pdf p25–26). A lost power reply therefore can't hand the pan/tilt or version reply to the wrong inquiry. A reply of a length no waiting inquiry expects is logged as a warning with its raw bytes and answers nothing.
- A completion or error in socket 1 or 2 goes to the command in that socket, however much traffic came in between. A completion with no ACK before it goes to the oldest command still waiting for its ACK.
- A socket 0 error goes to the oldest inquiry or command still waiting for a reply. A command's syntax error is reported against that command, as before. Only a metadata inquiry's own syntax error (version, pan/tilt status) shows `Not supported`.
- Inquiries go one at a time: the next is sent once the camera answers the one before, or after 1 s without an answer. Commands go straight out. Each poll drops inquiries still queued from the last one, and a connection change drops the whole queue. An IV-CAM-I12 silently drops inquiries when more than two await a reply *(unverified: bench observation, no log kept)*.
- An inquiry or ACK that hasn't come back within 1 s is dropped, logged at Debug, and takes no later reply.
- A connection change forgets every request still waiting, so nothing sent on the old connection takes a reply on the new one.
- Inquiries with the same reply length are still matched by order alone: power and the IV-CAM-I12 tracking state are both 4 bytes. The tracking reply length comes from the driver's existing parsing *(unverified)*.

### A camera that stops answering

Both modes count polls that got no reply at all from the camera. After 3 in a row, communication goes to `Error`, which raises the Critical `communication` issue, and the event list and a Warning log line show the raw `No reply to <bytes>` for the last poll's inquiries. The next reply sets communication back to `Okay`. Polling carries on unchanged. While communication is in `Error`, the version and pan/tilt status read issues aren't raised as well.

### Pan/tilt status

While the camera is On, `SonyVisca` asks for the pan/tilt status each poll and decodes it per the Sony manual's PAN-TILT STATUS CODE table (`Documentation\Sony\50531551M.pdf` p107). A defective pan or tilt mechanism, a pan or tilt position that can't be detected, a failed pan/tilt operation and a failed initialisation each raise a Major issue (`pan-mechanism`, `tilt-mechanism`, `pan-position`, `tilt-position`, `pan-tilt-operation`, `pan-tilt-initialisation`). Each clears on the next status reply without it. A reply with no fault shows `Pan/Tilt Status: Normal: <raw reply>`; a reply with a fault removes that Detail while the issue stands. A camera that rejects the inquiry with a syntax error shows `Pan/Tilt Status: Not supported: <raw>` and isn't asked again until it reconnects.
