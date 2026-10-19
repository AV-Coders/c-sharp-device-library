# AVCoders.CommunicationClients

Transport clients for the [AV Coders device library](https://github.com/AV-Coders/c-sharp-device-library) — the concrete `CommunicationClient` implementations that drivers talk to hardware through. Built on `AVCoders.Core`. Targets **.NET 8.0 and .NET 10.0**.

## Install

```bash
dotnet add package AVCoders.CommunicationClients
```

Published to [nuget.org](https://www.nuget.org/packages/AVCoders.CommunicationClients). See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for details.

## Transports

- `AvCodersTcpClient`
- `AvCodersUdpClient`
  - Retries opening its socket every 5 s until it opens, whether the host is an IP address or a host name, and reports each failed attempt as the `connection` issue.
- `AvCodersSshClient`
- `AvCodersMqttClient`
- `AvCodersRestClient`
- `AvCodersMulticastClient`
- `AvCodersTcpServer`
- `AvCodersSnmpV3Client`
- `AvCodersWakeOnLan`

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
- `AvCodersMqttClient` connects and reconnects from one `ThreadWorker`. While not connected, each pass makes one `ConnectAsync` attempt, and the next pass starts 3 s after that attempt ends. One attempt against an unreachable broker can take up to MQTTnet's default connect timeout of 100 s. While connected, each pass only sets `ConnectionState` to Connected. A failed attempt, a dropped connection or a publish while disconnected doesn't start another loop. A broker that rejects the CONNECT (e.g. bad credentials) is reported as the `connection` issue.
- The MQTT worker doesn't inherit the actor or trigger id of the code that created the client. Each pass starts its own trigger id, and MQTTnet's receive and keep-alive loops carry the id of the pass that connected them.
- `AvCodersMqttClient` raises `RequestHandlers` once `PublishAsync` completes. A publish while disconnected is logged and dropped, not queued. Topics subscribed while disconnected are subscribed on connect. Failed publishes and subscribes are logged, and so is a subscription the broker refuses in its SUBACK (e.g. NotAuthorized).

See the [repository README](https://github.com/AV-Coders/c-sharp-device-library) for full wiring examples.
