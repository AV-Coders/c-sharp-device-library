# Magewell Director Plus: driver research and proposal

Research only. No library source, csproj or test file was changed.

- **Bench unit:** 10.1.3.49
- **Probed:** 19 October 2026, 12:40 to 13:12 AEDT
- **Software on the unit:** 3.3.1106. The user updated it from 3.3.1063 during the session (device log: `Firmware upgrade from 3.3.1063 to 3.3.1106`, 12:50 AEDT). Every finding below was captured on 3.3.1106.
- **Sources:** Magewell Director API docs (V1.0, version history to 3.3), read in full from the published PDF and spot-checked against the HTML pages; Director Plus Web UI User Manual (firmware 3.3, 20 May 2026); the bench unit, including the JavaScript of its own web UI; this repo.

## Summary

- The documented API is plain HTTP on port 8080 (also proxied on port 80): `GET` or `POST /V1.0/<group>/<action>?<query>`. It has no authentication. Commands reply with a small JSON envelope, not an empty body.
- The documented API cannot read recording state, streaming state, fade-to-black, freeze, output selections, identity or health. The web UI's own API (`POST /mwapi/...` on port 80) can, also without authentication. Its `/mwapi/statemachine` call returns almost all live state plus per-client change flags in one ~7.5 kB reply. The web UI polls it every second.
- There is no push channel for state. The web UI's WebSocket answered only `pong` while scenes were switched under it.
- Login (`Admin`/`Admin`, SHA-256 of the password, JWT cookie) is needed only for the `/api/...` admin endpoints. A driver doesn't need them.
- Proposed fit: one concrete `MagewellDirectorPlus : Recorder` in `AVCoders.MediaPlayer`, beside `LumensLc300` and `ExtronSmp351`. It would use one `AvCodersRestClient` on port 80 and poll with a `ThreadWorker`. Mixer channels would be `VolumeControl` subclasses, so they plug into `SignalR.Volume`. No new abstraction and no new package.

## 1. Device identity

From `POST /mwapi/device/get-device-info` (no login) and `POST /api/system/device-info` (login):

| Field | Value |
| --- | --- |
| Product | Director Plus (`productId` 1321, `compileProductId` 0x529, family "Director") |
| Device name | Director-SC6-LG-COMMS-RACK |
| Serial | A529260506027 |
| Hardware | A |
| Software (`appVersion`, `/api` `firmware-ver`) | 3.3.1106 |
| Firmware (`firmwareVersion`) | 1.1. The web UI dashboard shows this as "Firmware" and 3.3.1106 as "Software". The update log and the API docs' "firmware 3.3" both refer to the software number. |
| Time zone | Asia/Shanghai (UTC+8), NTP `ntp1.aliyun.com`. This is the factory setting, not local time. |
| Storage | Internal 128000 MB, 102303 MB free, no SD card |
| Health at probe time | Core temperature 43.7 °C, fan 3840 rpm, CPU 13.7 %, GPU 7 %, power draw 9.34 W, memory 8.1 GB total |
| Capabilities flag block (`/api`) | NTP, time zone, Wi-Fi station and AP, online upgrade, Control Hub (`support-sc-control`); no IPv6, no USB NCM |

What the unit reports about its connectors (`POST /mwapi/device/get-signal-info`). Nothing was connected to any port on the bench.

| Connector | `type` | Reported |
| --- | --- | --- |
| HDMI 1, HDMI 2 inputs | 6, 7 | `connected:false`, frame rate 0 |
| HDMI 3, HDMI 4 inputs | 100, 101 | `connected:false` |
| WEBCAM 1, WEBCAM 2 (USB) | 8, 9 | `connected:false` |
| Phone Camera #1 (network, Director Utility app) | 4 | `connected:false`, configured 1920x1080p 30 fps H.264, id `uuidabcdefghijklmnopqrstuvwxyz` |
| HDMI Out 1, HDMI Out 2 | 102, 103 | `connected:false`, `extraDisplayedMode:0` |
| USB Out | 104 | `connected:false`, `extraDisplayedMode:0` |

Production content on the unit (near factory default):

| Item | Content |
| --- | --- |
| Shows | 1: "Default Show (16:9)", 1920x1080, 60 fps, id 1789241795281 |
| Scenes | 7: PICTURE (on Program and Preview), HDMI 1, HDMI 2, HDMI 3, HDMI 4, WEBCAM 1, Phone Camera #1 |
| Stream destinations | 9 placeholders (YouTube, Twitch, Facebook Live, NDI, RTMP, SRT Caller, SRT Listener, RTSP, Restream), none configured |
| Audio mixer channels | PROGRAM, MONITOR, MIC 1, MIC 2, HDMI 1 to 4, BGM, Phone Camera |
| GFX, shortcuts, BGM playlist, replay events, PTZ devices | none |
| Album | empty before probing; one test recording after (section 7) |

## 2. Transport and session model

### Endpoints

| Path | Port | Auth | Method | Notes |
| --- | --- | --- | --- | --- |
| `/V1.0/*` (documented API) | 8080 (`Connection: close`, no `Server` header) and 80 (nginx 1.26.0, keep-alive) | none | `GET` or `POST`; parameters in the query string | verified-live on both ports for getters and commands |
| `/mwapi/*` (web UI) | 80 | none for every read made; its writes were not tried | `POST` only (`GET` returns `{"status":2,"message":"unsupported method"}`); body optional, any content type | undocumented |
| `/api/*` (web UI admin) | 80 | cookie from `/api/user/login` | `POST` with `Content-Type: application/json` (form-encoded returns HTTP 405) | without the cookie: `{ "status": 36, "code": "AuthFailed" }` |
| `ws://<ip>/socket/socket` | 80 | none | WebSocket | see "Push channel" below |
| HTTPS | 443 closed | — | — | off by default; enabled from System > Security with an uploaded certificate, then reboot |

Open TCP ports seen: 80, 8080, 1935, 8081. Closed: 22, 23, 443, 554, 8443.

### Reply envelopes

| Call kind | Success | Failure |
| --- | --- | --- |
| `/V1.0` command | HTTP 200, `{"status":200,"reason":""}` | HTTP status equals the body status: 400 missing parameter, 403 refused (unknown scene, unknown audio type, unconfigured stream, index 0), 404 unknown action |
| `/V1.0` getter | HTTP 200, `{..., "message":"SUCCESS","status":0}` | HTTP 200 with a non-zero `status`, e.g. `{"status":13,"message":"item not exist"}` |
| `/V1.0/<Wrong-case-group>/...` | — | HTTP 200, `{"status":10,"message":"unsupported API"}` |
| `/mwapi`, `/api` | `"status": 0` | non-zero `status` from the web UI's `MW_STATUS_*` table (e.g. 7 InvalidArgument, 36 AuthFailed, 37 NotLogin) |

The docs call the API "connectionless interfaces without response data". In practice every call returns JSON, and a driver must check both the HTTP code and the body `status`. Success is 200 for commands and 0 for getters.

### Login and session (only for `/api`)

The web UI sends the SHA-256 hex digest of the password, not the password:

```
POST /api/user/login HTTP/1.1
Content-Type: application/json

{"username":"Admin","password":"c1c224b03cd9bc7b6a86d77f5dace40191766c485cd55dc48caf9ac873335d6f"}

HTTP/1.1 200 OK
Set-Cookie: sid-A529260506027=<JWT>;path=/

{ "status": 0, "code": "Success", "sid": "<JWT>", "group": "Admin", "strong-password": true, "expires": -1 }
```

- The documented factory default (`Admin`/`Admin`) was accepted on the first and only attempt.
- The cookie name carries the serial number. It has no `Expires`, and the body says `"expires": -1`. The token is an HS256 JWT, issuer `mw`, whose payload names the user and group.
- `POST /api/user/logout` revokes it server-side: the same cookie afterwards returns `{ "status": 36, "code": "AuthFailed" }`.
- The web UI sends users back to the login page on status 37 (not logged in) or 36 (auth failed).
- `AvCodersRestClient` already keeps cookies (`UseCookies = true`), so `/api` would need no client change. The proposed driver doesn't use `/api`.

### Push channel

There is none for state:

- The web UI opens `ws://<ip>/socket/socket` (`wss` on 443 when HTTPS is on). It sends `{"eventType":4001,"info":{"client":"<id>"}}` and `{"register":"<15 chars>"}`, then `ping` every 5 s, which the unit answers with `pong`.
- Two listening windows got nothing but `pong`: 12 s idle, then 20 s spanning two scene switches. The UI code uses the socket for preview video signalling (`webrtcProgram`, `forceI`) *(inferred from the UI code)*.
- For state, the web UI polls `POST /mwapi/statemachine` with `{"client":"<id>"}` every 1000 ms (`APP_POLLING_INTERVAL`).

### `/mwapi/statemachine`

One call returns, among ~150 fields:

- Program and Preview scene ids: `lastProgramSceneId`, `lastPreviewSceneId`
- `ftbEnable`, `isSceneFreezed`, `vfaEnable`
- quick-switch mode `switchMode` (0 = on)
- scene playlist `autoPlayStatus`
- recording: `recordStatus` (recording flag, seconds recording, per-recorder free space and time remaining) and `recordError`
- streaming: `streamingStatus`, `streamingError`
- `programGfxIds`, `previewGfxIds`
- current show: `showId`, `showName`, `frameWidth`, `frameHeight`, `frameRate`
- BGM status, clip playback status, replay status
- Program meter: `programVolume.left`, `programVolume.right`, `programVolume.muted`
- touchscreen `brightness`
- `firmwareUpdating`
- about 70 `...Changed` flags

The `...Changed` flags are tracked per `client` string and cleared when read:

- Observed: after a scene switch the next read had `currentSceneChanged`, `bgmListChanged`, `shortcutFunctionParamsChanged`, `videoPlayStatusChanged`, `liveSoonStatusChanged` and `resourceOfCurrentShowChanged` set to `true`. The read after that had none.
- A new client string starts with no flags set.
- Audio level and state changes set no flag, so the mixer has to be polled separately.
- `sceneListChanged`, `showListChanged`, `streamServersChanged` and `gfxListChanged` exist but were not seen to fire. Nothing that would raise them was changed.

### Timing (bench, wired LAN)

| Call | Reply size | Time |
| --- | --- | --- |
| `GET /V1.0/scene/getSceneList` | 967 B | 10 to 12 ms |
| `GET /V1.0/volume/getConfig` | 3383 B | 16 ms |
| `POST /mwapi/statemachine` | 7495 B | 30 ms |
| `POST /mwapi/device/get-summary-info` | 686 B | 37 ms |
| `POST /mwapi/device/get-signal-info` | 2145 B | 38 ms |
| `/V1.0` command | 26 B | 10 to 35 ms |
| Scene switch to `isPGM` visible in `getSceneList` | — | under 1 s (next read after a 1 s pause) |

### Behaviour across the update

- Before the update (3.3.1063), only HTTP status codes were captured: the web root (a different web UI bundle, `app.5c19c405.js`, now `app.1a5b5b77.js`) and `/V1.0/scene/getSceneList` on 8080 returning 200 without auth. Both behave the same on 3.3.1106. Nothing else was captured on 3.3.1063, so no other comparison is possible.
- During the update, the documented API kept answering 200 from 12:40 until at least 12:49:59 AEDT. The device log has the flash starting at 12:47 and finishing at 12:50. The unit then refused connections (12:52:21) while it rebooted, and was back by 12:54.
- The statemachine's `firmwareUpdating` flag and `POST /mwapi/upgrade/get-device-state` (`state`, `step`, `step-progress`, `is-rebooting`) expose an update in progress *(fields read idle only)*.

### Proposed poll set

| Interval | Call | Gives |
| --- | --- | --- |
| 1 s | `POST /mwapi/statemachine` `{"client":"<driver instance id>"}` | everything in the list above, plus change flags |
| 2 s | `GET /V1.0/volume/getConfig` | mixer levels, on/off/AFV, solo, scope (no change flag exists) |
| on start, on reconnect, on the matching flag | `GET /V1.0/scene/getSceneList`, `/V1.0/show/getShowList`, `/V1.0/stream/getStreamServersList`, `/V1.0/gfx/getGFXList` | names and ids for the ids the statemachine reports |
| 30 s | `POST /mwapi/device/get-summary-info` | temperature, fan, storage, CPU, memory, power, with the unit's own `...Warning` booleans |
| 30 s | `POST /mwapi/device/get-signal-info` | input presence, format, frame rate; output content (see section 6) |
| on start | `POST /mwapi/device/get-device-info` | identity for `SetDetail` |

## 3. Capability map

Status values:

- **verified-live**: sent to the bench unit and the effect or reply observed. The exchange is in section 4.
- **documented-only**: in Magewell's docs, not exercised on the unit (usually because nothing of that kind exists on the bench unit).
- **documented-but-failed-live**: in the docs, sent, and the unit didn't behave as documented.
- **sent-unconfirmed**: sent and accepted (HTTP 200), but no readback could confirm the effect.

Documented endpoints are `GET` or `POST http://<ip>:8080/V1.0/<group>/<action>?<query>`. Group names are lower case and differ from the doc folder names: `auto-play`, `graphics` and `replay`, not `Auto-play`, `Html_Graphics` and `Instant_Replay`. "Toggles when omitted" means the boolean parameter is optional and leaving it out flips the current state.

### Scenes and switching

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| List scenes with Program/Preview flags | `scene/getSceneList` | itself: `uuid`, `name`, `isPGM`, `isPreview`, `privateGfx` | verified-live |
| Switch to a scene | `scene/switchByUUID?uuid=&switchEffect=0\|1` (0 cut, 1 fade, omitted = Switch Settings default). With quick switch on it goes straight to Program (Preview follows); with quick switch off it loads Preview only. | `getSceneList`; statemachine `lastProgramSceneId`, `lastPreviewSceneId` | verified-live |
| Same, addressed by name or position | `scene/switchByName?name=` (URL-encoded), `scene/switchByIndex?index=` (1-based; 0 returns 403) | as above | verified-live |
| Step through scenes | `scene/first`, `last`, `next`, `previous` | as above | documented-only |
| Take Preview to Program | `scene/previewToPGM?switchEffect=`. Preview stays on the taken scene; it doesn't swap. | as above | verified-live |
| Quick switch | `scene/setQuickSwitch?state=On\|Off` (toggles when omitted) | `/mwapi/scene/get-scene-switch-config` `switchSceneMode`, statemachine `switchMode` (0 on, 1 off) | verified-live |
| Transition type | `scene/setTransitionAnimation?name=cut\|fade\|dip\|wipe\|dve\|stinger\|3d` | `get-scene-switch-config` `sceneAnimationType` (0 cut, 1 fade) | verified-live (cut, fade) |
| Transition duration | `scene/setTransitionDuration?duration=<ms>` | `get-scene-switch-config` `sceneAnimationDuration` | verified-live |
| Transition patterns and stinger | `scene/select3DPattern?pattern=0-7`, `selectDvePattern?pattern=0-18`, `selectStingerFile?index=1-4` | `get-scene-switch-config` `transitionParam` | documented-only |
| Transition the PGM scene's layers in or out | `scene/transition?action=0\|1` (toggles when omitted) | none | documented-only |
| Freeze Program | `scene/freeze`, `unfreeze`; `toggleFreeze`, `freezeToggle` | statemachine `isSceneFreezed` | verified-live (freeze, unfreeze) |
| Fade to black | `scene/toggleOnFTB`, `toggleOffFTB`; `toggleFTB`, `ftbToggle` | statemachine `ftbEnable` | verified-live (on, off) |
| Fade-to-black duration | `scene/setFTBDuration?duration=<ms>` | `get-scene-switch-config` `ftbAnimationDuration` | verified-live |
| Clip playback in the current scene | `scene/playVideo`, `pauseVideo`, `videoPlayAction`, `seekVideo?progress=<ms>`, `seekVideoForward\|Backward?offset=<ms>`, `seekVideoByProgress?progress=0-1`, `seekVideoToStart`, `seekVideoToEnd` | statemachine `programVideoPlayStatus` | documented-only |
| Slideshow in the current scene | `scene/slideSourceControl?pause=`, `slideSourceNext`, `slideSourcePrevious` | none | documented-only |
| Scene playlist (Plus only) | `auto-play/enter`, `exit`, `play`, `pause`, `togglePlay`, `next`, `previous`, `switch?index=` | statemachine `autoPlayStatus` | documented-only |
| VFA mode (Plus only) | `vfa/enable?enable=`, `vfa/switch` | statemachine `vfaEnable` | documented-only |

### Shows

A show is the top-level production (canvas size, frame rate, its own scenes).

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| List shows | `show/getShowList` | itself: `showId` (64-bit), `name`, `inCurrent`, `frameWidth`, `frameHeight`, `frameRate` (x1000), `orientation` | verified-live |
| Load a show | `show/switchById?id=`, `switchByIndex?index=` (0-based per the docs), `switchByName?name=`, `first`, `last`, `next`, `previous` | `getShowList` `inCurrent`; statemachine `showId` | documented-only (one show on the unit) |

### Streaming

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| List stream destinations | `stream/getStreamServersList` | itself: `id` (64-bit), `name`, `type` (1 YouTube, 2 Twitch, 3 Facebook, 4 RTMP, 5 SRT Caller, 6 NDI, 8 SRT Listener, 9 RTSP, and 11 Restream, which isn't in the docs), `isConfigured` | verified-live |
| Start or stop one destination | `stream/actionById?id=&start=true\|false` (toggles when omitted), `actionByIndex?index=`, `actionByName?name=`; `controlYouTubeLive=true` also starts or ends the YouTube broadcast | statemachine `streamingStatus`, `streamingError`; `/mwapi/streamServer/get-streaming-status` `isStreaming` | documented-only. Start on the unconfigured NDI destination returned 403 and nothing started; stop returned 200. No destination is configured, so a successful start couldn't be tried. |
| Stop every destination | `stream/stopAll?controlYouTubeLive=` | as above | documented-only |

### Recording

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| Start, stop recording | `record/start`, `record/stop` (stop while stopped returns 200) | statemachine `recordStatus.recording`, `timeRecording`, per-recorder `freeSize` (MB) and `timeRemaining` (s); `recordError` | verified-live |
| Toggle recording | `record/action` | as above | documented-only |
| Still of the Program output | `record/screenshot` | Album | documented-only |

### Audio mixer

Each channel is addressed by a numeric `type`. The docs' type tables disagree with each other and with the unit (section 6); the unit's own `volume/getConfig` is the authority. Live types on 3.3.1106: PROGRAM 3, MONITOR 1, MIC 1 512 (0x0200), MIC 2 514 (0x0202), HDMI 1 1792 (0x0700), HDMI 2 2048 (0x0800), HDMI 3 2050 (0x0802), HDMI 4 2051 (0x0803), BGM 1539 (0x0603), Phone Camera 266 (0x010A).

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| Mixer state for every channel | `volume/getConfig` | itself: per channel `type`, `name`, `label`, `volume` (dB), `muted` (0 always on, 1 always off, 2 AFV), `solo`, `scope`, `supportAFV`, `meter` (L/R dB) | verified-live |
| Level | `volume/setVolume?type=&volume=` (integer dB, -40 to 10; out-of-range values are clamped, not rejected) | `getConfig` `volume` | verified-live |
| On / off / audio-follow-video (this is the mute) | `volume/setState?type=&state=0\|1\|2` | `getConfig` `muted` | verified-live |
| Solo to the monitor | `volume/solo?type=` (toggles) | `getConfig` `solo` | verified-live |
| Timed fades (Plus only) | `volume/fadeOut?type=&duration=0-1000` ramps the level to -40 dB; `volume/fadeIn?type=&volume=&duration=` ramps to the given dB. The on/off/AFV state is untouched. | `getConfig` `volume` | verified-live |
| Mix the mic into the monitor | `volume/monitorMicInput?on=` | `getConfig` `monitorMicSound` | verified-live |
| Monitor output devices | `volume/getMonitorDevices` (Headphone Jack, Bluetooth Device) | itself | verified-live |
| Pick the monitor device | `volume/setMonitorDevice?deviceId=&deviceType=&cardNo=&devNo=` | `getConfig` `monitorAudioDevice` | documented-only |
| Share settings across scenes | `volume/setScope?type=&scope=0\|1` | `getConfig` `scope` | documented-only |
| Mix the mic to USB-C Out | `volume/outputMicToUsb?on=` | none | documented-only |
| Stream / USB input audio by name | `volume/setStreamAudioVolume?name=&volume=`, `setStreamAudioState?name=&state=` | `getConfig` | documented-only |

### Outputs (Plus only)

Each physical output shows one of: 0 Mirror (the touchscreen), 1 Program, 2 Loop HDMI 1, 3 Loop HDMI 2, 4 Multi-view, 5 Preview, 6 Loop HDMI 3, 7 Loop HDMI 4, 8 Loop App (`uuid`), 9 Loop custom source such as a stream or phone camera (`uuid`), 10 Loop Webcam 1, 11 Loop Webcam 2.

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| Sources the outputs can show | `system/getOutSettingsSourceList` | itself (the choices, as a bare JSON array with no status envelope) | verified-live |
| HDMI OUT 1 content | `system/setHDMI1Mode?mode=&uuid=` | `/mwapi/device/get-signal-info` `extraDisplayedMode` on type 102 (the web UI's I/O page shows this as "display content") | sent-unconfirmed. `mode=1` and then `mode=0` both returned 200; `extraDisplayedMode` read 0 before, between and after, with no display attached. |
| HDMI OUT 2 content | `system/setHDMI2Mode?mode=&uuid=` | as above, type 103 | documented-only |
| USB OUT display content | `system/setDPMode?mode=&uuid=` | as above, type 104 *(assumed)* | documented-only |
| USB OUT UVC (webcam) content | `system/setUSBCMode?mode=&uuid=` | none | documented-only |

### Device

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| Identity | `/mwapi/device/get-device-info` (undocumented) | itself | verified-live |
| Health | `/mwapi/device/get-summary-info` (undocumented) | itself: `coreTemp`, `fanSpeed`, `storageSize`, `storageFreeSize`, `cpu`, `gpuUsage`, `memInfo`, `powerConsumption`, `upTime`, `hasSDCard`, `battery`, with `...Warning` booleans | verified-live |
| Input signal and output content | `/mwapi/device/get-signal-info` (undocumented) | itself | verified-live (read) |
| Touchscreen brightness | `system/setBrightness?brightness=0\|0.1-1.0` | statemachine `brightness` | documented-only |
| Screenshot of the touchscreen | `system/screenshot` | none | documented-only |
| Reboot, power off | `system/reboot`, `system/shutdown` | none | documented-only, not sent |

### Graphics and overlays

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| List GFX | `gfx/getGFXList` | itself: `uuid`, `name`, `gfxSceneType` (returned an empty list) | verified-live |
| Show or hide one GFX | `gfx/actionByUUID?uuid=&on=` (toggles when omitted), `actionByName?name=`, `actionByIndex?index=` | statemachine `programGfxIds` | documented-only (no GFX on the unit) |
| Hide all GFX | `gfx/clear` | statemachine `programGfxIds` | documented-only |
| Scoreboards (general, baseball, soccer, tennis), timer, stopwatch | `gfx/scoreboard/*` (47 actions), `gfx/tennis/*`, `gfx/countdown/*`, `gfx/stopwatch/*`; `uuid` and `sceneId` optional, defaulting to the one on Program | statemachine `scoreboard`, `countdown`, `stopwatch` | documented-only |
| Webpage source on Program | `graphics/upAction`, `downAction`, `leftAction`, `rightAction`, `pageUpAction`, `pageDownAction`, `homeAction`, `endAction`, `goBack`, `goForward`, `reload`, `reset`, `doScale?scale=0.25-5` | statemachine `htmlControlStatus` | documented-only |
| App source on Program (Plus only) | `app/zoom?zoomSize=0.5-1.5`, `enlarge`, `reduce`, `reset`, `goBack` | none | documented-only |

### Background music

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| Playlist | `bgm/getPlayList` | itself: `title`, `singer`, `duration`, `filePath`, `fileMD5`, `notExist` (returned an empty list) | verified-live |
| Transport | `bgm/play`, `pause`, `playAction`, `next`, `previous`, `first`, `last`, `playByIndex?index=`, `playByFile?filePath=`, `seekByProgress?progress=0-1` | statemachine `bgmStatus` | documented-only |

### Replay

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| Saved events and tags | `replay/getEventList`, `replay/getAllTags` | itself (both empty) | verified-live |
| Replay control | `enterReplayMode`, `exitReplayMode`, `replayEvent`, `replayEvents`, `replayEventsLast`, `replayAllEvents`, `replayFromSecondsAgo`, `play`, `pause`, `playAction`, `seek`, `seekByProgress`, `forward`, `backward`, `nextFrame`, `previousFrame`, `rewindToStart`, `setSpeed`, `selectLens`, `muteAudio`, `muteMic`, `stingerTransition` | statemachine `instantReplayStatus` | documented-only |
| Create and edit events | `addEvent?duration=3-60`, `exportEvent`, `addTag`, `removeTag`, `updateEventName`, `updateEventTag`, `updateRating`, `updateTeamName` | `getEventList` | documented-only |
| Delete events | `removeEvents`, `removeEventsByTime`, `removeAllEvents` | `getEventList` | documented-only, not sent |

### PTZ (proxied to cameras in the current show)

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| Cameras the Director can steer | `ptz/getDeviceList` | itself: `current`, per device `id`, `layerName`, `controlType` (100 VISCA UDP, 101 NDI, 102 phone camera, 103 UVC, 104 OBSBOT NDI), `isSupportPtz` (returned an empty list) | verified-live |
| Pick the camera the next PTZ commands go to | `ptz/selectedControl?id=` | `getDeviceList` `current` | documented-only |
| Pan/tilt, zoom, focus | `moveUp`, `moveDown`, `moveLeft`, `moveRight`, `moveUpLeft`, `moveUpRight`, `moveDownLeft`, `moveDownRight` (`speed=0-1`), `moveStop`, `moveHome`, `zoomIn`, `zoomOut`, `zoomStop`, `zoomExact?zoomValue=0-1`, `focusExact?focusValue=0-1`, `autoFocus` | none | documented-only |
| Presets | `goToPreset?position=1-9`, `storePreset?position=1-9`, `setMovingSpeed?speed=0-1` | statemachine `ptzMovingSpeed` | documented-only |
| Phone camera (Director Utility app) | `getPhoneCameraConfig` and `phoneCamera*` (exposure, ISO, white balance, focus, torch, stabilisation, mic, record, lens) | `getPhoneCameraConfig` returned `{"status":13,"message":"item not exist"}` with no phone connected | documented-only |
| OBSBOT cameras | `AIHumanTrackingAction`, `startAIHumanTracking`, `stopAIHumanTracking`, `setHumanTrackingMode\|Speed\|Type`, `recordAction`, `startRecording`, `stopRecording`, `reset`, `wakeUp` | statemachine `obsbot` | documented-only |

### Shortcuts

| Capability | Request | State readable from | Status |
| --- | --- | --- | --- |
| List the user's macro buttons | `shortcuts/getConfig` (returned an empty list) | itself: `id` (64-bit), `name`, `functions` | verified-live |
| Run one | `shortcuts/action?id=` | none | documented-only |

## 4. Verified exchanges

All times AEDT. Requests were sent with curl from the bench PC. Restores are listed in section 7.

### Command envelope and errors

```
GET /V1.0/scene/switchByUUID?uuid=5e9a9fb3-3d5a-4889-b7ef-18b7ac2c829b&switchEffect=0   (port 8080)
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Connection: close
Content-Length: 26

{"status":200,"reason":""}

GET /V1.0/scene/switchByUUID?uuid=00000000-0000-0000-0000-000000000000&switchEffect=0
HTTP 403  {"status":403,"reason":"The server understood the request but refused to execute it."}

GET /V1.0/scene/notAThing
HTTP 404  {"status":404,"reason":"Unprovided API."}

GET /V1.0/scene/switchByUUID
HTTP 400  {"status":400,"reason":"The request contains a syntax error."}

GET /V1.0/Scene/getSceneList
HTTP 200  {"status":10,"message":"unsupported API"}

POST /V1.0/show/getShowList           HTTP 200, same body as GET
GET  http://10.1.3.49/V1.0/show/getShowList (port 80)   HTTP 200, Server: nginx/1.26.0, Connection: keep-alive, same body
```

### Scenes

```
GET /V1.0/scene/getSceneList
{"sceneList":[{"isPGM":true,"isPreview":true,"name":"PICTURE","privateGfx":{"gfxSceneType":0},"uuid":"da38f7ef-a133-4813-80da-9134ed0e2c31"},{"isPGM":false,"isPreview":false,"name":"HDMI 1","privateGfx":{"gfxSceneType":0},"uuid":"5e9a9fb3-3d5a-4889-b7ef-18b7ac2c829b"},{"isPGM":false,"isPreview":false,"name":"HDMI 2","privateGfx":{"gfxSceneType":0},"uuid":"207a774d-8c00-4210-a36a-6c471f763fd9"},{"isPGM":false,"isPreview":false,"name":"HDMI 3","privateGfx":{"gfxSceneType":0},"uuid":"b5f16d24-c069-4dcc-9f4f-01e49d572879"},{"isPGM":false,"isPreview":false,"name":"HDMI 4","privateGfx":{"gfxSceneType":0},"uuid":"77482569-9fe9-45d9-967d-f3bdb8493b53"},{"isPGM":false,"isPreview":false,"name":"WEBCAM 1","privateGfx":{"gfxSceneType":0},"uuid":"500411c7-502a-4e6d-a6a2-9a8c97b17e77"},{"isPGM":false,"isPreview":false,"name":"Phone Camera #1","privateGfx":{"gfxSceneType":0},"uuid":"5d6c0c2a-10aa-4f99-8b5a-f5721035dcb8"}],"totalCount":7,"message":"SUCCESS","status":0}
```

Quick switch on (factory setting), 12:58:59:

```
GET /V1.0/scene/switchByUUID?uuid=5e9a9fb3-...&switchEffect=0    -> {"status":200,"reason":""}
getSceneList one second later: HDMI 1 isPGM:true isPreview:true, PICTURE false/false
POST /mwapi/device/get-device-status: "lastPreviewSceneId": "5e9a9fb3-...", "lastProgramSceneId": "5e9a9fb3-..."
```

By position and name:

```
GET /V1.0/scene/switchByIndex?index=2&switchEffect=0           -> 200; Program = HDMI 1 (the second scene)
GET /V1.0/scene/switchByName?name=HDMI%203&switchEffect=1      -> 200; Program = HDMI 3
GET /V1.0/scene/switchByIndex?index=0&switchEffect=0           -> 403; Program unchanged
```

Quick switch off:

```
GET /V1.0/scene/setQuickSwitch?state=Off        -> 200; get-scene-switch-config "switchSceneMode": 1
GET /V1.0/scene/switchByUUID?uuid=5e9a9fb3-...  -> 200; PICTURE isPGM:true isPreview:false, HDMI 1 isPGM:false isPreview:true
GET /V1.0/scene/previewToPGM?switchEffect=0     -> 200; HDMI 1 isPGM:true isPreview:true
GET /V1.0/scene/setQuickSwitch?state=On         -> 200; "switchSceneMode": 0
```

### Fade to black, freeze, transition settings (read back through `/mwapi`)

```
GET /V1.0/scene/toggleOnFTB      -> {"status":200,"reason":""}; statemachine "ftbEnable": true (1.5 s later, and on the next read)
GET /V1.0/scene/toggleOffFTB     -> 200; "ftbEnable": false
GET /V1.0/scene/freeze           -> 200; "isSceneFreezed": true
GET /V1.0/scene/unfreeze         -> 200; "isSceneFreezed": false

POST /mwapi/scene/get-scene-switch-config
{ "ftbAnimationDuration": 1000, "sceneAnimationDuration": 200, "sceneAnimationType": 1, "switchSceneMode": 0, "transitionParam": { ... } }
GET /V1.0/scene/setTransitionDuration?duration=500    -> "sceneAnimationDuration": 500
GET /V1.0/scene/setFTBDuration?duration=500           -> "ftbAnimationDuration": 500
GET /V1.0/scene/setTransitionAnimation?name=cut       -> "sceneAnimationType": 0
GET /V1.0/scene/setTransitionAnimation?name=fade      -> "sceneAnimationType": 1
```

### Audio

```
GET /V1.0/volume/getConfig   (one channel of ten shown)
{"audioSourceId":1792,"channelProcess":0,"meter":[-100.0,-100.0],"audioInfo":{"beInMultiScenes":0,"boost":false,"direction":0,"isGFX":false,"label":"","muted":2,"name":"HDMI 1","preview":false,"scope":0,"solo":0,"sourceType":6,"supportAFV":true,"type":1792,"uuid":"eco0","volume":0,"volumeRecovery":0}}
... "micAudioDevice":{"audioSourceId":513,...,"name":"Mic",...},"monitorAudioDevice":{"audioSourceId":0,...},"monitorMicSound":true,"message":"SUCCESS","status":0}

GET /V1.0/volume/setVolume?type=1792&volume=-10     -> 200; getConfig "volume":-10
GET /V1.0/volume/setVolume?type=1792&volume=20      -> 200; getConfig "volume":10   (clamped)
GET /V1.0/volume/setVolume?type=1792&volume=-41     -> 200; getConfig "volume":-40  (clamped)
GET /V1.0/volume/setVolume?type=9999&volume=0       -> 403
GET /V1.0/volume/setState?type=1792&state=1         -> 200; "muted":1
GET /V1.0/volume/setState?type=1792&state=0         -> 200; "muted":0
GET /V1.0/volume/solo?type=1792                     -> 200; "solo":1; sent again -> "solo":0
GET /V1.0/volume/fadeOut?type=1792&duration=500     -> 200; "volume":-40, "muted":2 unchanged
GET /V1.0/volume/fadeIn?type=1792&volume=-5&duration=500 -> 200; "volume":-5
GET /V1.0/volume/monitorMicInput?on=false           -> 200; "monitorMicSound":false
GET /V1.0/volume/getMonitorDevices
{"monitorAudioDeivceList":[{"audioSourceId":0,"cardNo":0,"devNo":0,"deviceType":0,"direction":0,"id":0,"isMic":true,"name":"Headphone Jack","sourceType":0},{"audioSourceId":1,"cardNo":0,"devNo":0,"deviceType":0,"direction":0,"id":1,"isMic":true,"name":"Bluetooth Device","sourceType":0}],"message":"SUCCESS","status":0}
```

Level and state changes on HDMI 1 and PROGRAM set no statemachine `...Changed` flag; `programVolume` in the statemachine stayed `{ "left": -100, "right": -100, "muted": 0 }`.

### Recording (13:00:46 to 13:00:49)

```
statemachine before:  "recordError": [ ], "recordStatus": { "recording": false, "timeRecording": 0, "record": [ { "enable": true, "recording": false, "storageType": 2, "freeSize": 101779.615744, "timeRemaining": 129419 }, { "enable": false, ... } x3 ] }
GET /V1.0/record/start   -> {"status":200,"reason":""}
+1 s:  "recordStatus": { "recording": true, "timeRecording": 1 ...
+3 s:  "recordStatus": { "recording": true, "timeRecording": 3 ...
GET /V1.0/record/stop    -> {"status":200,"reason":""}
+1 s:  "recordStatus": { "recording": false, "timeRecording": 0 ...
GET /V1.0/record/stop (already stopped) -> 200
```

### Streaming

```
GET /V1.0/stream/getStreamServersList
{"serverList":[{"id":1789241795389,"isConfigured":false,"name":"YouTube","type":1},{"id":1789241795390,"isConfigured":false,"name":"Twitch","type":2},{"id":1789241795391,"isConfigured":false,"name":"Facebook Live","type":3},{"id":1789241795392,"isConfigured":false,"name":"NDI","type":6},{"id":1789241795393,"isConfigured":false,"name":"RTMP","type":4},{"id":1789241795394,"isConfigured":false,"name":"SRT Caller","type":5},{"id":1789241795395,"isConfigured":false,"name":"SRT Listener","type":8},{"id":1789241795396,"isConfigured":false,"name":"RTSP","type":9},{"id":1789241795397,"isConfigured":false,"name":"Restream","type":11}],"totalCount":9,"message":"SUCCESS","status":0}

GET /V1.0/stream/actionById?id=1789241795392&start=true    -> 403 {"status":403,"reason":"The server understood the request but refused to execute it."}
statemachine: "streamStatusChanged": false, "streamingStatus": [ ], "streamingError": [ ]
POST /mwapi/streamServer/get-streaming-status -> { "status": 0, "isStreaming": false, "streamServersChanged": false, "streamingStatus": [ ] }
GET /V1.0/stream/actionById?id=1789241795392&start=false   -> 200
```

### Shows, outputs, lists

```
GET /V1.0/show/getShowList
{"showList":[{"frameHeight":1080,"frameRate":60000,"frameWidth":1920,"inCurrent":true,"name":"Default Show (16:9)","orientation":0,"showId":1789241795281}],"totalCount":1,"message":"SUCCESS","status":0}

GET /V1.0/system/getOutSettingsSourceList
[{"mode":1},{"mode":5},{"mode":4},{"mode":0},{"mode":2},{"mode":3},{"mode":6},{"mode":7},{"mode":10},{"mode":11},{"mode":9,"name":"Phone Camera #1","uuid":"uuidabcdefghijklmnopqrstuvwxyz"}]

GET /V1.0/system/setHDMI1Mode?mode=1    -> 200; get-signal-info HDMI Out 1 "extraDisplayedMode": 0 (unchanged)
GET /V1.0/system/setHDMI1Mode?mode=0    -> 200; "extraDisplayedMode": 0

GET /V1.0/gfx/getGFXList            {"gfxList":[],"totalCount":0,"message":"SUCCESS","status":0}
GET /V1.0/bgm/getPlayList           {"bgmList":[],"message":"SUCCESS","status":0}
GET /V1.0/shortcuts/getConfig       {"list":[],"totalCount":0,"message":"SUCCESS","status":0}
GET /V1.0/ptz/getDeviceList         {"status":0,"message":"SUCCESS","current":"","deviceList":[]}
GET /V1.0/ptz/getPhoneCameraConfig  {"status":13,"message":"item not exist"}
GET /V1.0/replay/getAllTags         {"data":[],"message":"SUCCESS","status":0}
GET /V1.0/replay/getEventList       {"status":0,"message":"SUCCESS","eventList":[]}
```

### Identity, health, signal (undocumented, no login)

```
POST /mwapi/device/get-device-info
{ "status": 0, "deviceName": "Director-SC6-LG-COMMS-RACK", "familyName": "Director", "productName": "Director Plus", "serialNumber": "A529260506027", "hardwareVersion": "A", "firmwareVersion": "1.1", "appVersion": "3.3.1106", "deviceType": 1, "timeZone": "Asia\/Shanghai", "timeZoneOffset": 28800000, "productId": 1321, "compileProductId": "0x529" }

POST /mwapi/device/get-summary-info
{ "status": 0, "upTime": 261362, "fanRpm": 0, "fanSpeed": "3840 rpm", "fanSpeedWarning": false, "coreTemp": "43.7", "coreTempWarning": false, "storageSize": 128000, "storageFreeSize": 102303.903, "battery": { "isCharging": true, "leftBatteryLevel": -50, "rightBatteryLevel": -50 }, "hasSDCard": false, "headphoneLinked": false, "builtIn4GState": 0, "builtIn4GStrengthLevel": 4, "bluetoothLinked": false, "cpu": 13.69, "cpuWarning": false, "gpuUsage": 7, "gpuWarning": false, "memInfo": { "avail": 5250420, "total": 8109528, "warning": false }, "fd": { "total": 1024, "limit": 2147483647, "inUse": 473, "warning": false }, "dmc": "4%", "rga": "3%, 3%, 0%", "powerConsumption": "9.34 w" }

POST /mwapi/device/get-signal-info   (two of ten entries)
{ "audioFormat": 0, "audioSampleRate": 0, "bound": false, "connected": false, "droped": 0, "extraDisplayedMode": 0, "frameRate": 0.0, "quality": 0, "type": 6, "videoFrameRateRealTime": 0.0 },
{ "audioFormat": 0, "audioSampleRate": 0, "bound": false, "connected": false, "droped": 0, "extraDisplayedMode": 0, "frameRate": 0.0, "name": "HDMI Out 1", "quality": 0, "type": 102, "videoFrameRateRealTime": 0.0 }

POST /mwapi/device/get-device-status   (subset of the statemachine)
{ "status": 0, ..., "ftbEnable": false, "isSceneFreezed": false, "showId": 1789241795281, "showName": "Default Show (16:9)", "switchMode": 0, "lastPreviewSceneId": "da38f7ef-...", "lastProgramSceneId": "da38f7ef-...", "previewGfxIds": [ ], "programGfxIds": [ ], "streamingStatus": [ ], "recordStatus": { ... }, ... }

POST /mwapi/statemachine  {"client":"1792375100002"}   (excerpt of 7495 bytes)
{ "status": 0, "message": "SUCCESS", "autoPlayStatus": { "isAutoplayMode": false, ... }, "vfaEnable": false, "ftbEnable": false, "isSceneFreezed": false, ..., "currentSceneChanged": false, ..., "firmwareUpdating": false, ..., "streamStatusChanged": false, "streamingStatus": [ ], "streamingError": [ ], ..., "showId": 1789241795281, "showName": "Default Show (16:9)", "frameRate": 60000, "frameWidth": 1920, "frameHeight": 1080, "switchMode": 0, "lastPreviewSceneId": "da38f7ef-...", "lastProgramSceneId": "da38f7ef-...", "recordError": [ ], "recordStatus": { ... }, "programGfxIds": [ ], ..., "brightness": 0.900293231010437, ..., "programVolume": { "left": -100, "right": -100, "muted": 0 }, ... }
```

### WebSocket, 12:58:56 to 12:59:16 (scene switches at 12:58:59 and 12:59:06)

```
12:58:56.669 CONNECTED ws://10.1.3.49/socket/socket
12:58:56.719 SENT {"eventType":4001,"info":{"client":"1792375100001"}}
12:58:56.720 SENT {"register":"abcdefghijklmnp"}
12:59:01.802 SENT ping
12:59:01.842 RECV pong
12:59:06.905 SENT ping
12:59:06.909 RECV pong
12:59:11.973 SENT ping
12:59:11.976 RECV pong
12:59:16.689 END (20 s limit)
```

## 5. Library fit

### Where it goes

| Option | For | Against |
| --- | --- | --- |
| **`AVCoders.MediaPlayer`, `MagewellDirectorPlus : Recorder` (recommended)** | Closest existing peers are there: `LumensLc300` (lecture-capture recorder with layouts and source selection) and `ExtronSmp351` (streaming recorder) are both `Recorder`s. Recording state is readable live, so `TransportState` reports actual state and `DesiredTransportState` works as on those drivers. A room program can drive a Director and an SMP 351 through the same `Recorder` reference. No new package. | `Recorder` forces `Pause()` and `AddMarker()`, which the Director can't do. Both would log a "not supported" warning, the gap the planned capability system is meant to close. The package name doesn't say "switcher". |
| `AVCoders.MediaPlayer`, `: DeviceBase` | No unsupported members. | Loses the shared `Record()`/`Stop()`/`TransportState` surface for no gain. |
| `AVCoders.Matrix`, `: VideoMatrix` | — | Switching is by scene, not input-to-output routing. Outputs carry modes (Program, Preview, Multi-view, Mirror), not inputs. `GetInputs()`/`GetOutputs()` would be forced. |
| New package (e.g. `AVCoders.Production`) | Clear home if a second production switcher (ATEM, Roland, vMix, Pearl) arrives. | New csproj, test project and publish entry for one driver. Worth revisiting only when that second switcher is real. |

Director Mini and Director One share most of this API (Plus-only groups: `app`, `auto-play`, `vfa`, output modes, fades, HDMI 3/4). Per the house rules there is no shared `MagewellDirector` base until one of those is actually deployed. Then the shared calls move down and the Plus-only members stay on `MagewellDirectorPlus`.

### Comms client

- Use one `AvCodersRestClient("<ip>", 80, "http")`. Port 80 serves both `/V1.0` and `/mwapi` with keep-alive; port 8080 serves only `/V1.0` and closes the connection after every reply.
- `/V1.0` calls go through `Get(Uri)`. `/mwapi` calls go through `Post(Uri, "{}", "application/json")`.
- Replies are routed in an `HttpResponseHandlers` subscriber on `response.RequestMessage.RequestUri.AbsolutePath`, the pattern `ThorRf11IqPdu` already uses. `ResponseHandlers` alone can't tell which request a body answers.
- A request timeout of about 3 s suits a 1 s poll (the client default is 10 s).

### Existing surface it implements

| Member | Maps to |
| --- | --- |
| `Recorder.Record()` / `DoRecord` | `/V1.0/record/start` |
| `Recorder.Stop()` / `DoStop` | `/V1.0/record/stop` |
| `Recorder.Pause()` / `DoPause` | not supported: logs a warning |
| `Recorder.AddMarker()` | not supported: logs a warning |
| `MediaPlayer.TransportState` | statemachine `recordStatus.recording` → `Recording` / `Stopped` |
| `MediaPlayer.MediaState` | `get-summary-info` `hasSDCard` → `Inserted` / `Ejected` |
| `DeviceBase.CommunicationState` | `Okay` on each statemachine reply with `status` 0; `Error` when the client's `ConnectionState` goes to `Error` |
| `DeviceBase.PowerOn/PowerOff` | empty, as on `ExtronSmp351`, `ExtronSharelinkPro` and `AutomateVX`. `system/shutdown` isn't used: nothing on the network can power the unit back on. |
| `VolumeControl` (one per mixer channel) | `volume/setVolume`, `volume/setState`; state from `volume/getConfig` |
| `LogBase.SetDetail` | Model, Serial, Software, Firmware, Hardware, Device name, Temperature, Fan, Storage free, Power draw, from `get-device-info` and `get-summary-info` |

### Model-specific members (concrete class only)

Scenes and Program/Preview, quick switch, transition, fade to black, freeze, shows, stream destinations and live streams, output content, GFX, shortcuts, and recording time remaining.

### Mixer channels

- Each channel is a `MagewellDirectorAudioChannel : VolumeControl`. The driver creates them for the fixed type ids of the Plus and updates them from its `getConfig` poll, so the state stays in the driver.
- They go into `SignalR.Volume`'s `VolumeManager`, which takes a fixed `List<VolumeControl>` at construction, unchanged.
- Level maps linearly between the device's integer dB range (-40 to +10) and `VolumeControl`'s 0 to 100 %: 2 % per dB, 0 dB = 80 %.
- Mute is `setState` 1. Unmute restores the last non-off state the poll saw: AFV (2) for inputs that report `supportAFV`, always-on (0) otherwise. The three-way state is also exposed as `AudioState`.

### Issues

The keys follow the `logbase-issues` skill:

| Key | Raised when | Kind |
| --- | --- | --- |
| `communication` | `CommunicationState` goes to `Error` (base class) | ongoing, Critical |
| `unanswered-poll` | a statemachine poll fails; `escalateAfter: 3`, resolved on each good reply | momentary |
| `recording-state` | desired transport state differs from the reported one after a poll | ongoing, Major |
| `recording-error` | `recordError` non-empty | ongoing, Critical |
| `recording-space` | enabled recorder's `timeRemaining` below a named threshold | ongoing, Major |
| `streaming-error` | `streamingError` non-empty | ongoing, Major |
| `command-refused` | a `/V1.0` command returns 403 (e.g. stream destination not configured) | momentary |
| `temperature`, `fan`, `cpu`, `memory` | `coreTempWarning`, `fanSpeedWarning`, `cpuWarning`, `memInfo.warning` true | ongoing; Major for temperature and fan, Minor for the others |
| `firmware-update` | statemachine `firmwareUpdating` true | ongoing, Minor |

Every log call is wrapped in `using (PushProperties("<Method>"))`.

### Missing from the library

None of these blocks the driver:

- **No dB↔percent helper outside `AVCoders.Dsp`.** `Fader` lives in the Dsp package. The channel class carries its own two-line linear map rather than taking a package dependency.
- **`Recorder` has no capability flags.** `Pause`/`AddMarker` stay as logged no-ops until the planned capability system lands.
- **No WebSocket client in `CommunicationClients`.** It isn't needed, because the Director's socket carries no state.

### Public surface sketch

```csharp
namespace AVCoders.MediaPlayer;

public enum MagewellAudioState
{
    AlwaysOn = 0,
    AlwaysOff = 1,
    AudioFollowVideo = 2
}

public enum MagewellSwitchEffect
{
    Cut = 0,
    Fade = 1
}

public enum MagewellTransitionType
{
    Cut,
    Fade,
    Dip,
    Wipe,
    Dve,
    Stinger,
    ThreeD
}

public enum MagewellOutput
{
    HdmiOut1,
    HdmiOut2,
    UsbOutDisplay,
    UsbOutUvc
}

public enum MagewellOutputContent
{
    Mirror = 0,
    Program = 1,
    LoopHdmi1 = 2,
    LoopHdmi2 = 3,
    MultiView = 4,
    Preview = 5,
    LoopHdmi3 = 6,
    LoopHdmi4 = 7,
    LoopApp = 8,
    LoopCustomSource = 9,
    LoopWebcam1 = 10,
    LoopWebcam2 = 11
}

public record MagewellScene(string Uuid, string Name);

public record MagewellShow(long Id, string Name, int Width, int Height, double FrameRate);

public record MagewellStreamDestination(long Id, string Name, int Type, bool IsConfigured);

public class MagewellDirectorAudioChannel : VolumeControl
{
    public const int MinDb = -40;
    public const int MaxDb = 10;

    public readonly int AudioType;

    public MagewellAudioState AudioState { get; }

    public event EventHandler<MagewellAudioState>? AudioStateChanged;

    public override void SetLevel(int percentage);
    public override void LevelUp(int amount);
    public override void LevelDown(int amount);
    public override void SetAudioMute(MuteState state);
    public override void ToggleAudioMute();
    public void SetAudioState(MagewellAudioState state);
}

public class MagewellDirectorPlus : Recorder
{
    public const ushort DefaultPort = 80;
    public const int ProgramAudio = 0x0003;
    public const int MonitorAudio = 0x0001;
    public const int Mic1Audio = 0x0200;
    public const int Mic2Audio = 0x0202;
    public const int Hdmi1Audio = 0x0700;
    public const int Hdmi2Audio = 0x0800;
    public const int Hdmi3Audio = 0x0802;
    public const int Hdmi4Audio = 0x0803;
    public const int BgmAudio = 0x0603;

    public readonly IReadOnlyList<MagewellDirectorAudioChannel> AudioChannels;

    public IReadOnlyList<MagewellScene> Scenes { get; }
    public string ProgramSceneUuid { get; }
    public string PreviewSceneUuid { get; }
    public bool QuickSwitch { get; }
    public bool FadeToBlack { get; }
    public bool Frozen { get; }
    public IReadOnlyList<MagewellShow> Shows { get; }
    public MagewellShow? CurrentShow { get; }
    public IReadOnlyList<MagewellStreamDestination> StreamDestinations { get; }
    public IReadOnlyList<long> LiveStreamIds { get; }
    public IReadOnlyList<string> ProgramGfxUuids { get; }
    public TimeSpan RecordingTimeRemaining { get; }

    public event EventHandler<IReadOnlyList<MagewellScene>>? ScenesChanged;
    public event EventHandler<string>? ProgramSceneChanged;
    public event EventHandler<string>? PreviewSceneChanged;
    public event EventHandler<bool>? QuickSwitchChanged;
    public event EventHandler<bool>? FadeToBlackChanged;
    public event EventHandler<bool>? FrozenChanged;
    public event EventHandler<IReadOnlyList<MagewellShow>>? ShowsChanged;
    public event EventHandler<MagewellShow?>? CurrentShowChanged;
    public event EventHandler<IReadOnlyList<MagewellStreamDestination>>? StreamDestinationsChanged;
    public event EventHandler<IReadOnlyList<long>>? LiveStreamsChanged;
    public event EventHandler<IReadOnlyList<string>>? ProgramGfxChanged;
    public event EventHandler<TimeSpan>? RecordingTimeRemainingChanged;

    public MagewellDirectorPlus(string name, RestComms comms);

    public void SwitchScene(string sceneUuid, MagewellSwitchEffect effect);
    public void TakePreview(MagewellSwitchEffect effect);
    public void SetQuickSwitch(bool on);
    public void SetTransitionType(MagewellTransitionType type);
    public void SetTransitionDuration(TimeSpan duration);
    public void SetFadeToBlack(bool on);
    public void SetFadeToBlackDuration(TimeSpan duration);
    public void SetFreeze(bool on);
    public void LoadShow(long showId);
    public void StartStream(long destinationId);
    public void StopStream(long destinationId);
    public void StopAllStreams();
    public void TakeScreenshot();
    public void SetOutputContent(MagewellOutput output, MagewellOutputContent content, string? sourceUuid = null);
    public void SetGfx(string gfxUuid, bool on);
    public void ClearGfx();
    public void RunShortcut(long shortcutId);

    public override void PowerOn();
    public override void PowerOff();
    public override void AddMarker();
    protected override void DoRecord();
    protected override void DoPause();
    protected override void DoStop();
}
```

The events are `EventHandler<T>`, following the planned migration. The members inherited from `Recorder`, `MediaPlayer` and `DeviceBase` keep their existing handler delegates. Scenes are switched by UUID only: names can repeat (the docs' own example list has two scenes called "HDMI 1") and positions shift when scenes are added. PTZ through the Director (`ptz/*`) is left out. A `MagewellDirectorCamera : CameraBase` per `getDeviceList` entry fits later. Every move would first send `ptz/selectedControl?id=`, because the Director steers one selected camera at a time.

Tests would follow `ThorRf11IqPduTest`/`AntricaEncoderTest`: a `Mock<RestComms>`, feeding the captured replies in section 4 through `HttpResponseHandlers`.

## 6. Quirks, surprises and open questions

### Quirks

- **No auth anywhere a driver needs.** `/V1.0` and `/mwapi` are open to anyone on the network, including scene, recording and streaming control. The `Admin` password guards only the `/api` admin pages.
- **The password is sent hashed.** It is SHA-256 hex, and the UI flags the default by comparing that hash. The login reply said `"strong-password": true` for the factory password.
- **Every call returns JSON**, despite the docs' "no response data". Success is `{"status":200}` for commands and `{"status":0}` for getters. Command errors come back as HTTP 4xx; getter errors come back as HTTP 200 with a non-zero `status`.
- **`getOutSettingsSourceList` returns a bare array** with no `status`.
- **Group names are case-sensitive and don't match the doc folder names.** A wrong-case group returns HTTP 200 `{"status":10,"message":"unsupported API"}`; an unknown action returns 404.
- **Ids exceed 32 bits.** `showId`, stream `id` and shortcut `id` (e.g. 1789241795281) are typed "Int" in the docs, so they need `long`. Scene UUIDs on this unit are real UUIDs; the docs' examples show 14-digit numbers.
- **Out-of-range levels are clamped,** not rejected: `volume=20` becomes 10 and `volume=-41` becomes -40.
- **`fadeOut` ramps the level to -40 dB rather than muting,** so after a fade-out the level reads -40 until something raises it.
- **Switching depends on quick switch.** `switchBy*` lands on Program with quick switch on (factory setting) and on Preview with it off. `previewToPGM` doesn't swap Preview back.
- **Statemachine change flags are per client string and clear when read.** Each driver instance needs its own client string, and a full fetch on start and after reconnect. Audio changes raise no flag.
- **The device clock is on Asia/Shanghai time** out of the box. `/mwapi` timestamps and log times are UTC+8 until someone sets the zone.
- **The API stays up during a firmware update** until the reboot. On this update that was about 5 minutes of normal replies, then about 2 minutes of refused connections.
- **The phone-camera source id is a placeholder string,** `uuidabcdefghijklmnopqrstuvwxyz`.

### The docs' audio type tables disagree

| Channel | Docs (setVolume / setState / fadeIn) | Docs (fadeOut / setScope) | Docs `getConfig` example | Unit (3.3.1106) |
| --- | --- | --- | --- | --- |
| Mic 1 | 0x0200 | 0x0200 | type 512 | 512 (0x0200) |
| Mic 2 | 0x0201 | 0x0202 | — | 514 (0x0202) |
| BGM | 0x0600 | 0x0600 | 1539 | 1539 (0x0603) |
| HDMI 3 / 4 | 0x0802 / 0x0803 | 0x0802 / 0x0803 | — | 2050 / 2051 (0x0802 / 0x0803) |

### Open questions

1. **HDMI OUT 1 content.** `setHDMI1Mode` returned 200 but `extraDisplayedMode` never moved off 0 with no display attached. Does the setting apply, and does `extraDisplayedMode` report the setting or only what a connected display shows? This needs a display on HDMI OUT 1.
2. **Streaming state shape.** The shape of a `streamingStatus` / `streamingError` entry while live is unknown. No destination is configured on the unit, so a successful stream couldn't be started.
3. **Flags not observed.** `sceneListChanged`, `showListChanged`, `streamServersChanged` and `gfxListChanged` weren't seen to fire; nothing that would raise them was changed.
4. **Unauthenticated `/mwapi` writes.** Only reads were made without a session. Writes weren't tried and aren't needed.
5. **Firmware stability of `/mwapi`.** It is undocumented. It answered the same way on 3.3.1106 throughout, but wasn't captured on 3.3.1063, so its stability across firmware is unproven. Everything with a documented equivalent should use `/V1.0`.
6. **Recorder slots.** `recordStatus.record` has four slots (`enable` true on the first only). Whether more than one can record at once, and to which storage, needs the touchscreen's recording settings.
7. **Package choice.** The recommendation is `MediaPlayer` with `Recorder`. A separate production-switcher package is the alternative if a second switcher is on the horizon.

## 7. Device state changed during probing

No reboot, reset, firmware, network, user or password change was made, and nothing was deleted. The firmware update was done by the user.

The times in this table are approximate (± 1 minute) unless they have seconds.

| Change | Time (AEDT) | Restored | Verified by |
| --- | --- | --- | --- |
| Program/Preview: PICTURE → HDMI 1 → PICTURE | 12:58:59, 12:59:06 | yes | `getSceneList`, statemachine |
| Fade to black on → off | 12:59:54, 12:59:56 | yes | statemachine `ftbEnable` false |
| Freeze on → off; Program PICTURE → HDMI 2 → PICTURE | 13:00 | yes | statemachine |
| HDMI 1 level 0 → -10 → 10 → -40 → 0; state AFV → off → on → AFV | 13:00 | yes | `getConfig` `volume` 0, `muted` 2 |
| Recording started and stopped (about 3.5 s) | 13:00:46, 13:00:49 | state yes | statemachine `recording` false |
| NDI stream start (refused, 403) and stop | 13:01:03, 13:01:07 | no change made | `streamingStatus` empty |
| HDMI OUT 1 content: mode 1, then mode 0 | 13:01 | **unconfirmed** | readback reported 0 throughout (section 6, question 1) |
| Program by index 2, by name HDMI 3, back to PICTURE | 13:02 | yes | `getSceneList` |
| Transition duration 200 → 500 → 200 ms; FTB duration 1000 → 500 → 1000 ms; transition cut → fade | 13:02 | yes | `get-scene-switch-config` |
| Quick switch on → off → on; HDMI 1 solo on → off; mic monitoring off → on | 13:02 | yes | `switchSceneMode` 0; `getConfig` |
| Quick switch off, HDMI 1 to Preview, take, quick switch on, Program PICTURE | 13:03 | yes | `switchSceneMode` 0, Program and Preview PICTURE |
| HDMI 1 fade out / fade in, then level 0 and AFV | 13:04 | yes | `getConfig` |
| Web session: logged in as Admin, then logged out | 12:56:26 to 13:05 | yes | the cookie returns AuthFailed |
| Fade to black on → off through port 80 | 13:05 | yes | `get-device-status` |
| HDMI 1 level -6 and off; PROGRAM level -3; then back to 0, AFV, 0 | 13:06 | yes | `getConfig` |

A final read-only comparison at 13:11:49 against the first post-update capture matched exactly for scenes, shows, stream destinations, GFX, shortcuts, output source list, monitor devices and the whole mixer configuration (meters excluded). The switch settings and statemachine state also matched: FTB off, not frozen, quick switch on, Program and Preview PICTURE, not recording, not streaming.

Left on the unit:

- **Test recording.** The test recording is the Album's only file: `Director-SC6-LG-COMMS-RACK_0001_PGM_REC1.mp4`, 11.8 MB, internal storage, `/storage/emulated/0/Director/Director/`. Deleting files was out of bounds, so it is still there; delete it from the web UI's Album page if it isn't wanted.
- **HDMI OUT 1 content.** It is now set to Mirror (mode 0). If it was set to anything else before 13:01, set it back from the touchscreen's output settings.
