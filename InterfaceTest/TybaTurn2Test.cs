using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AvCoders.Interface.Tests;

public sealed class FakeTyba : IDisposable
{
    private const int Port = 55555;
    private const string StreamResponse =
        "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n\r\n";

    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<System.Net.Sockets.TcpClient> _clients = [];
    private volatile NetworkStream? _eventStream;
    private volatile bool _streamClosed;
    private int _streamRequests;

    public FakeTyba()
    {
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _ = Accept();
    }

    public ConcurrentQueue<string> Puts { get; } = new();

    public int PutStatus { get; set; } = 200;

    public string PutReason { get; set; } = "OK";

    public bool DropPuts { get; set; }

    public int StreamRequests => Volatile.Read(ref _streamRequests);

    public bool StreamClosed => _streamClosed;

    public async Task Send(params string[] lines)
    {
        await Wait.UntilAsync(() => _eventStream != null, "The event stream was never opened");
        await _eventStream!.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + "\n"))));
        await _eventStream.FlushAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var client in _clients)
            client.Dispose();
        _stop.Dispose();
    }

    private async Task Accept()
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _clients.Add(client);
            _ = Serve(client);
        }
    }

    private async Task Serve(System.Net.Sockets.TcpClient client)
    {
        var stream = client.GetStream();
        var (method, path, body) = await ReadRequest(stream);
        if (method == "GET")
        {
            Interlocked.Increment(ref _streamRequests);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(StreamResponse));
            _eventStream = stream;
            try
            {
                while (await stream.ReadAsync(new byte[1]) > 0)
                {
                }
            }
            finally
            {
                _streamClosed = true;
            }
            return;
        }
        Puts.Enqueue($"{method} {path} {body}");
        if (!DropPuts)
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {PutStatus} {PutReason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        client.Close();
    }

    private static async Task<(string Method, string Path, string Body)> ReadRequest(NetworkStream stream)
    {
        var head = new StringBuilder();
        var buffer = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            await stream.ReadExactlyAsync(buffer);
            head.Append((char)buffer[0]);
        }
        var lines = head.ToString().Split("\r\n");
        var contentLength = lines.FirstOrDefault(line =>
            line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var body = new byte[contentLength == null
            ? 0
            : int.Parse(contentLength["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture)];
        await stream.ReadExactlyAsync(body);
        var requestLine = lines[0].Split(' ');
        return (requestLine[0], requestLine[1], Encoding.UTF8.GetString(body));
    }
}

public sealed class TybaTurn2Test : IDisposable
{
    private const string Address = "127.0.0.1";
    private readonly FakeTyba _server = new();
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly List<TybaTurn2> _tybas = [];
    private readonly ConcurrentQueue<CommunicationState> _states = new();

    private TybaTurn2 Create(string address = Address)
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            var tyba = new TybaTurn2(address, "Tyba");
            tyba.CommunicationStateHandlers += state => _states.Enqueue(state);
            _tybas.Add(tyba);
            return tyba;
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    public void Dispose()
    {
        foreach (var tyba in _tybas)
            tyba.Dispose();
        _server.Dispose();
    }

    private static string Json(int value, string modifiedBy = "Panel") =>
        $"{{\"modifiedBy\": \"{modifiedBy}\", \"state\": {{\"value\": {value}}}}}";

    private static string Data(int value, string modifiedBy = "Panel") => $"data: {Json(value, modifiedBy)}";

    private List<string> Messages() => _logs.Logs.Select(log => log.Message).ToList();

    [Fact]
    public async Task Heartbeat_ReportsCommunicationOkay()
    {
        Create();

        await _server.Send(": heartbeat");

        await Wait.UntilAsync(() => _states.Contains(CommunicationState.Okay), "The heartbeat was not reported");
    }

    [Fact]
    public async Task Dispose_StopsTheEventStream()
    {
        var tyba = Create();
        await Wait.UntilAsync(() => _server.StreamRequests == 1, "The event stream was never opened");

        tyba.Dispose();

        await Wait.UntilAsync(() => _server.StreamClosed, "The event stream was left open");
        Assert.DoesNotContain(tyba, LogBaseRegistry.GetAll());
        Assert.DoesNotContain(Messages(), message => message.StartsWith("ThreadWorker", StringComparison.Ordinal));
        Assert.Empty(tyba.Errors);
    }

    [Fact]
    public async Task EventThatFailsToParse_ReportsAnErrorAndTheStreamContinues()
    {
        var tyba = Create();
        var handler = new Mock<IntChangeHandler>();
        tyba.LightLevelChangeHandlers += handler.Object;

        await _server.Send("event: light/1", "data: {not json", "event: light/2", Data(30));

        await Wait.UntilAsync(() => handler.Invocations.Count == 1, "The event after the bad one was not handled");
        handler.Verify(x => x.Invoke(2, 30), Times.Once);
        Assert.Contains(CommunicationState.Error, _states);
        Assert.Contains(tyba.Errors, e => e.Message == "Unable to process data: {not json");
        Assert.Equal(1, _server.StreamRequests);
    }

    [Fact]
    public async Task EventWithoutANumericIndex_IsLoggedAndSkipped()
    {
        var tyba = Create();
        var handler = new Mock<IntChangeHandler>();
        tyba.LightLevelChangeHandlers += handler.Object;

        await _server.Send("event: light/x", Data(5), "event: light", Data(6), "event: light/2", Data(30));

        await Wait.UntilAsync(() => handler.Invocations.Count == 1, "The event after the bad ones was not handled");
        handler.Verify(x => x.Invoke(2, 30), Times.Once);
        Assert.Contains("Event light/x has no channel index", Messages());
        Assert.Contains("Event light has no channel index", Messages());
        Assert.DoesNotContain(CommunicationState.Error, _states);
    }

    [Fact]
    public async Task DataContainingAnEventField_IsProcessedAsData()
    {
        var tyba = Create();
        var handler = new Mock<IntChangeHandler>();
        tyba.LightLevelChangeHandlers += handler.Object;

        await _server.Send("event: light/1", Data(30, modifiedBy: "event: panel"));

        await Wait.UntilAsync(() => handler.Invocations.Count == 1, "The data line was not handled");
        handler.Verify(x => x.Invoke(1, 30), Times.Once);
    }

    [Fact]
    public async Task FieldsWithoutASpace_AreRecognised()
    {
        var tyba = Create();
        var handler = new Mock<IntChangeHandler>();
        tyba.ShadeChangeHandlers += handler.Object;

        await _server.Send("event:shade/3", $"data:{Json(40)}");

        await Wait.UntilAsync(() => handler.Invocations.Count == 1, "The event was not handled");
        handler.Verify(x => x.Invoke(3, 40), Times.Once);
    }

    [Fact]
    public void FeedbackAbove100_IsNotSentAndIsLogged()
    {
        var tyba = Create();

        tyba.ShadeFeedback(2, 101);
        tyba.ClimateTargetFeedback(100.5);

        Assert.Contains("shade 2 value 101 is above 100, not sent", Messages());
        Assert.Contains("temperature 1 value 100.5 is above 100, not sent", Messages());
        Assert.Empty(_server.Puts);
    }

    [Fact]
    public async Task TemperatureFeedback_IsFormattedWithTheInvariantCulture()
    {
        var tyba = Create();
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            tyba.ClimateTargetFeedback(21.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        await Wait.UntilAsync(() => !_server.Puts.IsEmpty, "The feedback was not sent");
        Assert.Equal("PUT /api/v1.0/control/channels/temperature/1/1/state {\"value\": 21.5}",
            Assert.Single(_server.Puts));
    }

    [Fact]
    public async Task FailedPut_ReportsAnError()
    {
        _server.DropPuts = true;
        var tyba = Create();

        tyba.ShadeFeedback(2, 40);

        await Wait.UntilAsync(() => _states.Contains(CommunicationState.Error), "The failed PUT was not reported");
        Assert.Contains(tyba.Errors, e => e.Exception is HttpRequestException);
    }

    [Fact]
    public async Task RejectedPut_IsLoggedAsAnErrorAndReportsAnError()
    {
        _server.PutStatus = 404;
        _server.PutReason = "Not Found";
        var tyba = Create();

        tyba.ShadeFeedback(9, 40);

        await Wait.UntilAsync(() => _states.Contains(CommunicationState.Error), "The rejected PUT was not reported");
        Assert.Contains($"Response 404: Not Found from http://{Address}:55555/api/v1.0/control/channels/shade/9/1/state",
            Messages());
    }

    [Fact]
    public void InvalidAddress_IsReportedAndNothingIsSent()
    {
        var tyba = Create("bad host");

        tyba.ShadeFeedback(1, 40);

        Assert.Contains(tyba.GetOngoingIssues(), i => i.Key == "address");
        Assert.Contains(
            "Payload {\"value\": 40} for control/channels/shade/1/1/state not sent, bad host is not a valid address",
            Messages());
        Assert.Equal(0, _server.StreamRequests);
    }
}
