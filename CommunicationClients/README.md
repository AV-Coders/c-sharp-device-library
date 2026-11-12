# AVCoders.CommunicationClients

Transport clients for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library) — the concrete `CommunicationClient` implementations that drivers talk to hardware through. Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.CommunicationClients
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.CommunicationClients). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Transports

- `AvCodersTcpClient`
  - Reads are decoded as Latin-1 (one character per byte, the encoding `Bytes.FromString` sends), or as the `\xNN` dump when `CommandStringFormat` isn't `Ascii`.
  - While connected, the connection is checked every second, so a dropped connection is retried within about a second. After a failed attempt it waits 1 s, doubling up to 20 s; a good connection starts it at 1 s again.
  - An attempt that hasn't connected within 5 s is ended and reported as the `connection` issue "The connection attempt to host:port timed out". Its exception goes in `Errors` and is logged at Debug, not Error. Every other failed attempt, refused and unreachable included, logs its exception as an Error. A failed attempt's socket is closed at once.
  - A failed write closes that connection once and keeps the message at the head of the send queue, so it goes out first, in order, after the reconnect. A queued message whose write fails is timestamped again, so the queue timeout counts from the failure; the messages queued behind it keep their own timestamps. The SSH client does the same.
  - `Reconnect()` closes the open connection, if there is one, and starts a connection attempt at once with the backoff back at 1 s. While an attempt is already running it only resets the backoff.
- `AvCodersUdpClient`
  - Retries opening its socket every 5 s until it opens, whether the host is an IP address or a host name, and reports each failed attempt as the `connection` issue. The failure is logged when it first happens and when its reason changes, not on every retry. A host name that resolves to no addresses is reported as "The host name was not found".
  - Receives block until a datagram arrives. Datagrams are decoded as Latin-1, or as the `\xNN` dump when `CommandStringFormat` isn't `Ascii`.
  - A failed send keeps the datagram at the head of the send queue and reopens the socket, as the TCP client does, and a queued datagram whose send fails is timestamped again. A failed direct send reopens it on a task, so the caller doesn't wait on a host name lookup.
  - A device whose port is closed (ICMP port unreachable, seen on the next receive) is reported as the `connection` issue, with no state change and no new socket. The next datagram received resolves it.
- `AvCodersSshClient`
  - After a failed connection attempt it waits 60 s before the next one. When the device rejects the credentials it waits 5 minutes instead, and the `connection` issue says so: "SSH authentication to host was rejected for user X; the next attempt is in 5 minutes".
  - `Connect()` and `Reconnect()` start a connection attempt at once, ending either wait and resetting the short-session backoff. `Disconnect()` ends the wait and stays disconnected.
  - Each write and its flush hold one lock, so messages from senders on different threads, and from the send queue, reach the shell stream whole and in the order they were written.
  - The receive worker reads when the shell stream signals data (`DataReceived`) or that it closed (`Closed`), instead of checking every 10 ms. An idle session wakes it no times a second instead of about 100. Lines are still read one at a time, in order, on that worker, and a closed shell still sets Error at once.
- `AvCodersMqttClient`
- `AvCodersRestClient`
- `AvCodersMulticastClient`
  - Binds on its connection worker, retrying with a 1–20 s backoff. A failed bind, a dead socket and a group that isn't an IP address (logged with the value as given) are reported as the `connection` issue; the constructor doesn't throw. A dead socket is rebound at once; one that dies again before it has stayed bound through a 30 s check is rebound after the bind backoff.
  - An IPv6 group binds to `IPv6Any`, and the TTL sets its multicast hop limit.
  - Text is sent and received as Latin-1, one character per byte, as the TCP client does; `Bytes.FromString` sends a character above U+00FF as `?`. Received datagrams are the `\xNN` dump instead when `CommandStringFormat` isn't `Ascii`. The request handlers are raised only for a datagram that was sent.
- `AvCodersTcpServer`
  - `Connect()` listens and accepts, `Disconnect()` stops listening, drops the clients and sets `DesiredConnectionState` to Disconnected, and `Reconnect()` restarts accepting and keeps the clients.
  - A failed bind or a dead listener is reported as the `connection` issue, and the listener is rebound at once with a 1–20 s backoff. `Disconnected` is set as soon as the last client leaves.
  - Each write to a client times out after 5 s; a client whose write fails is dropped and the other clients still get the send.
  - Text is sent and received as Latin-1, one character per byte, as the TCP client does; `Bytes.FromString` sends a character above U+00FF as `?`. Reads are the `\xNN` dump instead when `CommandStringFormat` isn't `Ascii`.
- `AvCodersSnmpV3Client`
  - `Get`, `Set` and `Walk` return an `SnmpReply`: the variables, plus the RFC 3416 `ErrorStatus` and `ErrorIndex` as received. A reply with a non-zero error status counts as an answer: the client stays Connected, raises no `connection` issue, and returns no variables with the status and index, which the traffic log shows as `error status GenError (5), index 1`. A timeout, an unreachable host or rejected credentials returns no variables, sets Error and reports the `connection` issue.
- `AvCodersWakeOnLan`
  - `Wake` returns at once and sends the three rounds of packets to ports 7 and 9, 300 ms apart, on a task. An invalid MAC is logged with the value as given and nothing is sent.

## Why a client isn't connected

Every client has `ConnectionReason` beside `ConnectionState` (see the `AVCoders.Core` README). `ConnectionState` keeps its values and when they're set. The reason is set on each failed attempt, with the `connection` issue's text as its `Description`, and cleared when the client connects.

| Category | Set when |
|---|---|
| `TimedOut` | A socket timeout, the TCP or MQTT 5 s connect timeout, an SSH operation timeout, or an SNMP or REST request timeout |
| `Refused` | The connection is refused, or a UDP or SNMP datagram gets ICMP port unreachable back (`ConnectionReset` on Windows, `ConnectionRefused` on Linux) |
| `HostNotFound` | The host name doesn't resolve, or resolves to no addresses |
| `Unreachable` | The host or the network is unreachable |
| `CredentialsRejected` | SSH authentication is rejected; a REST request gets 401 or 403; an SNMPv3 agent reports an unknown user, a wrong auth or privacy password, or an unsupported security level; an MQTT broker answers BadUserNameOrPassword or NotAuthorized |
| `ClosedByDevice` | The connection is reset, or the SSH server ends the session ("An established connection was aborted by the server.") |
| `Requested` | `Disconnect()` was called. `Connect()` clears it |
| `Other` | Anything else, with the raw text |

- An SSH session that fails after it connected (the stream's `ErrorOccurred`, such as the Navigator dropping the session) sets the reason without raising the `connection` issue, and the exception is still logged as an Error. Reconnecting clears it.
- A REST 401 or 403 sets `CredentialsRejected` and raises the `connection` issue "host:port rejected the credentials for /path: 401 Unauthorized", which escalates like any other connection failure. The client stays Connected. The next 2xx response clears the reason and resolves the issue.
- `AvCodersModbusRtuClient` mirrors its transport's reason.
- The TCP, SSH and UDP clients set `DesiredConnectionState` to Connected in their constructors, where they start connecting, so it no longer reads Unknown. `Connect()` isn't called, so the send queue and the workers are left as they are.

## Reconnect on the next send a person makes

- The TCP, SSH and UDP clients' and `AvCodersTcpServer`'s `Disconnect()` records `Actor.Current` as `DisconnectedBy` and turns `ReconnectOnSend` on when there is one (a browser, MCP or a panel) and off when there isn't (a driver or the program). Both are set before `DesiredConnectionState` changes, so the state and reason handlers already see them.
- A `Disconnect()` while the client is already disconnected on request keeps `DisconnectedBy` and `ReconnectOnSend` as they are.
- While the client is disconnected on request with `ReconnectOnSend` on, a send made under an `Actor`, directly or through its driver, calls `Connect()` and then goes out. `ReconnectingForSend` is true from then until the client is connected, or disconnected again.
- Each send made under an `Actor` while `ReconnectingForSend` is true is queued until the client connects, and is sent if that's within 15 s, or within the queue timeout when that is longer. Every other queued send, polls included, keeps the queue timeout (5 s by default, `SetQueueTimeout`).
- The UDP client opens its socket inside `Connect()`, so the send goes out at once when the socket opens. When it doesn't, the send is queued as above and the socket is retried every 5 s.
- A `Connect()` while `ReconnectingForSend` is true keeps the send queue. Any other `Connect()` clears it, as before.
- A send with no `Actor` (a poll or any other `ThreadWorker` pass, a command a driver sends from a reply or a timer) is dropped with a Debug log, as is every send while `ReconnectOnSend` is off.
- `Connect()`, `Disconnect()`, `SetReconnectOnSend` and a person's send while disconnected on request run one at a time on each client. A second caller waits until the first has finished; a TCP or UDP disconnect takes milliseconds, an SSH disconnect can take a second or two while SSH.NET waits for the server to close the channel, and a UDP connect to a host name waits for DNS. Sends while connected and polls never wait.
- `SetReconnectOnSend(bool)` changes it. `Connect()` turns it off and clears `DisconnectedBy`, and the next `Disconnect()` sets both again. Each change of `ReconnectOnSend` is logged at Information and added to `Events`. `ReconnectOnSendHandlers` is raised on each change of `ReconnectOnSend` or `DisconnectedBy`. The reconnect is logged at Information with the sender's actor and `DisconnectedBy`.
- `AvCodersTcpServer` has no peers to send to while it's disconnected, so that send calls `Connect()`, which starts it listening again for its peers to reconnect, and is dropped with an Information log.
- `AvCodersMulticastClient` never sets it.

## Usage

A driver is decoupled from *how* it is connected — you construct a transport and hand it to the driver:

```csharp
var comms = new AvCodersTcpClient("192.168.1.50", 4352, "Projector", CommandStringFormat.Ascii);
```

## Trigger ids, queues and headers

- `RequestHandlers` / `RequestByteHandlers` (tx) are raised when the bytes are written, not when a send is queued.
- The TCP, SSH and UDP offline send queues store the sender's `Trigger.Id` with each payload, and send it, or log its timeout or overflow drop, under `Trigger.Resume(id)`.
- The UDP send queue drains once the client has a socket. Only sends younger than the queue timeout (5 s by default, set with `SetQueueTimeout`) go out; older ones are dropped.
- Each SSH line, `AvCodersTcpServer` read and MQTT message received outside a `ThreadWorker` pass starts a new trigger id.
- `AvCodersRestClient`, and every other `HttpClient` the drivers create, use a `SocketsHttpHandler` with `ActivityHeadersPropagator = null`: requests carry no `traceparent`, `tracestate`, `baggage` or `Correlation-Context` headers, including when sent from inside an ASP.NET Core request (an MCP call or a Blazor event).
- `AvCodersMqttClient` connects and reconnects from one `ThreadWorker`. While not connected, each pass makes one `ConnectAsync` attempt, and the next pass starts 3 s after that attempt ends. An attempt that hasn't connected within 5 s, including one to a broker that accepts the TCP connection and never answers the CONNECT, is ended and reported as the `connection` issue "The connection attempt to host:port timed out". While connected, each pass only sets `ConnectionState` to Connected. A failed attempt, a dropped connection or a publish while disconnected doesn't start another loop. A broker that rejects the CONNECT (e.g. bad credentials) is reported as the `connection` issue.
- The MQTT worker doesn't inherit the actor or trigger id of the code that created the client. Each pass starts its own trigger id, and MQTTnet's receive and keep-alive loops carry the id of the pass that connected them.
- `AvCodersMqttClient` raises `RequestHandlers` once `PublishAsync` completes. A publish while disconnected is logged and dropped, not queued. Topics subscribed while disconnected are subscribed on connect. On each connect every subscribed topic's SUBSCRIBE is sent before `ConnectionState` becomes Connected, so a publish from a `ConnectionStateHandlers` Connected handler reaches the broker after them. A message reaches the handlers of every subscribed topic filter it matches, `+` and `#` wildcards included. Failed publishes and subscribes are logged, and so is a subscription the broker refuses in its SUBACK (e.g. NotAuthorized).

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring examples.
