using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using AVCoders.Core;
using Microsoft.Extensions.Logging;
using Moq;

namespace AVCoders.Power.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class ServerEdgePduTest : IDisposable
{
    private const string StatusPath = "/status.xml";
    private const string StatusBody =
        "<response><pot0>,,0.3,0.0,0.0,0.0,0.0,0.0,0.0,0.0,1,0,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,0,5,5,5,5,5,5,5,0.3,0,</pot0></response>";
    private const string OnlyTheFirstOutletOnBody =
        "<response><pot0>,,0.3,0.0,0.0,0.0,0.0,0.0,0.0,0.0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,5,5,5,5,5,5,5,0.3,0,</pot0></response>";
    private const string SuccessBody = "Success!";

    private readonly Mock<RestComms> _restComms;
    private readonly ServerEdgePdu _serverEdgePdu;
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();
    private readonly ConcurrentDictionary<string, (string Body, HttpStatusCode Status)> _replies = new();
    private readonly ConcurrentDictionary<string, bool> _unanswered = new();
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _logs = new();
    private readonly ILoggerFactory _originalLoggerFactory = LogBase.LoggerFactory;

    public ServerEdgePduTest()
    {
        LogBase.LoggerFactory = new CapturingLoggerFactory(_logs);
        _restComms = new Mock<RestComms>("host", (ushort)1, "test");
        _restComms.Setup(client => client.Get(It.IsAny<Uri>()))
            .Returns<Uri?>(async uri =>
            {
                var path = uri!.OriginalString;
                _requests.Enqueue(path);
                if (_holds.TryRemove(path, out var hold))
                    await hold.Task;
                if (_replies.TryGetValue(path, out var reply))
                    Respond(reply.Body, reply.Status);
                if (_unanswered.ContainsKey(path))
                    SetConnection(ConnectionState.Error);
            });
        _serverEdgePdu = new ServerEdgePdu(_restComms.Object, "test", ServerEdgePdu.DefaultUser,
            ServerEdgePdu.DefaultPassword, 8);
    }

    public void Dispose()
    {
        _serverEdgePdu.Dispose();
        LogBase.LoggerFactory = _originalLoggerFactory;
    }

    private sealed class CapturingLoggerFactory(ConcurrentQueue<(LogLevel Level, string Message)> logs)
        : ILoggerFactory, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => logs.Enqueue((logLevel, formatter(state, exception)));
    }

    private void DoNotAnswer(string path) => _unanswered[path] = true;

    private void Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _restComms.Object.HttpResponseHandlers!.Invoke(new HttpResponseMessage(status) { Content = new StringContent(body) });

    private void ReplyTo(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _replies[path] = (body, status);

    private Task RequestsSent() => (Task)typeof(ServerEdgePdu)
        .GetField("_requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_serverEdgePdu)!;

    private void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(_restComms.Object, state);

    private TaskCompletionSource HoldNextRequest(string path)
    {
        var release = new TaskCompletionSource();
        _holds[path] = release;
        return release;
    }

    private bool HoldTaken(string path) => !_holds.ContainsKey(path);

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failureMessage);
            await Task.Delay(25);
        }
    }

    private async Task WaitForStartup()
    {
        await WaitUntilAsync(() => _requests.Count == 2, "The PDU never asked for its names and its status");
        await RequestsSent();
        _requests.Clear();
    }

    [Fact]
    public void HandleResponse_WithAStatusPage_SetsTheOutletStatesAndOkay()
    {
        Respond(StatusBody);

        Assert.Equal(PowerState.On, _serverEdgePdu.Outlets[0].PowerState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Equal(CommunicationState.Okay, _serverEdgePdu.CommunicationState);
    }

    [Fact]
    public void HandleResponse_WithAnUnparsablePage_SetsErrorAndAddsOneEvent()
    {
        Respond("<html><body>Wrong password");

        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        var error = Assert.Single(_serverEdgePdu.Events,
            e => e.Type == EventType.Error && e.Info.StartsWith("The response could not be used"));
        Assert.Contains("<html><body>Wrong password", error.Info);
    }

    [Fact]
    public void HandleResponse_WithAnHttpError_SetsError()
    {
        Respond("401 Unauthorized", HttpStatusCode.Unauthorized);

        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        var error = Assert.Single(_serverEdgePdu.Events,
            e => e.Type == EventType.Error && e.Info.StartsWith("The response could not be used"));
        Assert.Contains("HTTP 401", error.Info);
    }

    [Fact]
    public void HandleResponse_WithAStatusPageAfterAnError_SetsOkay()
    {
        Respond("<html><body>Wrong password");

        Respond(StatusBody);

        Assert.Equal(CommunicationState.Okay, _serverEdgePdu.CommunicationState);
    }

    [Fact]
    public void ConnectionError_SetsTheCommunicationStateToError()
    {
        SetConnection(ConnectionState.Error);

        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
    }

    [Fact]
    public async Task PowerOn_ReadsTheStatusFirst_SoAStaleCacheDoesNotReachTheMask()
    {
        await WaitForStartup();
        Respond(StatusBody);
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        ReplyTo("/ons.cgi?led=11000000", SuccessBody);

        _serverEdgePdu.Outlets[1].PowerOn();
        await RequestsSent();

        Assert.Equal(new[] { StatusPath, "/ons.cgi?led=11000000" }, _requests.ToArray());
        Assert.Equal(PowerState.On, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[2].PowerState);
    }

    [Fact]
    public async Task PowerOn_WhenTheStatusReadFails_SendsNothing()
    {
        await WaitForStartup();
        Respond(StatusBody);
        ReplyTo(StatusPath, "<html><body>Wrong password");

        _serverEdgePdu.Outlets[1].PowerOn();
        await RequestsSent();

        Assert.Equal(new[] { StatusPath }, _requests.ToArray());
        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Contains((LogLevel.Warning,
            "Not sending the power on command for outlet Unknown, the status page could not be read: " +
            "HTTP 200: <html><body>Wrong password"), _logs);
    }

    [Fact]
    public async Task PowerOn_WhenTheCommandFails_LeavesTheShownStateAsItWas()
    {
        await WaitForStartup();
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        ReplyTo("/ons.cgi?led=11000000", "500 Internal Server Error", HttpStatusCode.InternalServerError);

        _serverEdgePdu.Outlets[1].PowerOn();
        await RequestsSent();

        Assert.Equal(new[] { StatusPath, "/ons.cgi?led=11000000" }, _requests.ToArray());
        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Contains((LogLevel.Warning,
            "The power on command for outlet Unknown failed: HTTP 500: 500 Internal Server Error"), _logs);
    }

    [Fact]
    public async Task Reboot_WhenTheCommandGetsNoReply_LeavesTheShownStateAsItWas()
    {
        await WaitForStartup();
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        DoNotAnswer("/offon.cgi?led=01000000");

        _serverEdgePdu.Outlets[1].Reboot();
        await RequestsSent();

        Assert.Equal(new[] { StatusPath, "/offon.cgi?led=01000000" }, _requests.ToArray());
        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Contains((LogLevel.Warning, "The reboot command for outlet Unknown failed: no reply"), _logs);
    }

    [Fact]
    public async Task PduPowerOff_WhenTheCommandFails_LeavesTheShownStatesAsTheyWere()
    {
        await WaitForStartup();
        Respond(StatusBody);
        ReplyTo("/offs.cgi?led=11111111", "500 Internal Server Error", HttpStatusCode.InternalServerError);

        _serverEdgePdu.PowerOff();
        await RequestsSent();

        Assert.Equal(new[] { "/offs.cgi?led=11111111" }, _requests.ToArray());
        Assert.Equal(PowerState.On, _serverEdgePdu.Outlets[0].PowerState);
        Assert.Contains((LogLevel.Warning,
            "The power off command for all outlets failed: HTTP 500: 500 Internal Server Error"), _logs);
    }

    [Fact]
    public async Task TwoCommandsInARow_AreSentInOrder()
    {
        await WaitForStartup();
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        ReplyTo("/ons.cgi?led=11000000", SuccessBody);
        ReplyTo("/offs.cgi?led=01111111", SuccessBody);
        var firstStatusRead = HoldNextRequest(StatusPath);

        _serverEdgePdu.Outlets[1].PowerOn();
        await WaitUntilAsync(() => HoldTaken(StatusPath), "The first status read never started");
        _serverEdgePdu.Outlets[2].PowerOff();
        firstStatusRead.SetResult();
        await RequestsSent();

        Assert.Equal(new[] { StatusPath, "/ons.cgi?led=11000000", StatusPath, "/offs.cgi?led=01111111" },
            _requests.ToArray());
    }

    [Fact]
    public void Dispose_StopsThePollWorkerAndUnsubscribes()
    {
        var pollWorker = (ThreadWorker)typeof(ServerEdgePdu)
            .GetField("_pollWorker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_serverEdgePdu)!;
        Assert.True(pollWorker.IsRunning);

        _serverEdgePdu.Dispose();
        _serverEdgePdu.Dispose();

        Assert.False(pollWorker.IsRunning);
        Assert.Null(_restComms.Object.HttpResponseHandlers);
        Assert.Null(_restComms.Object.ConnectionStateHandlers);
        Assert.DoesNotContain(_serverEdgePdu, LogBaseRegistry.GetAll());
        Assert.DoesNotContain(_serverEdgePdu.Outlets[0], LogBaseRegistry.GetAll());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 6)]
    [InlineData(3, 9)]
    [InlineData(4, 12)]
    [InlineData(5, 15)]
    [InlineData(6, 18)]
    [InlineData(7, 21)]
    
    [InlineData(8, 1)]
    [InlineData(9, 4)]
    [InlineData(10, 7)]
    [InlineData(11, 10)]
    [InlineData(12, 13)]
    [InlineData(13, 16)]
    [InlineData(14, 19)]
    [InlineData(15, 22)]
    
    [InlineData(16, 2)]
    [InlineData(17, 5)]
    [InlineData(18, 8)]
    [InlineData(19, 11)]
    [InlineData(20, 14)]
    [InlineData(21, 17)]
    [InlineData(22, 20)]
    [InlineData(23, 23)]
    public void GetNameOutletIndex_ReturnsTheIndex(int outletIndex, int expectedIndex)
    {
        Assert.Equal(expectedIndex, _serverEdgePdu.GetNameIndex(outletIndex));
    }
}