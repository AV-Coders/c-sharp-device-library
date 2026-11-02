using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Moq;

namespace AVCoders.Power.Tests;

public abstract class ServerEdgePduFixture
{
    protected const string StatusPath = "/status.xml";
    protected const string StatusBody =
        "<response><pot0>,,0.3,0.0,0.0,0.0,0.0,0.0,0.0,0.0,1,0,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,0,5,5,5,5,5,5,5,0.3,0,</pot0></response>";
    protected const string OnlyTheFirstOutletOnBody =
        "<response><pot0>,,0.3,0.0,0.0,0.0,0.0,0.0,0.0,0.0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,5,5,5,5,5,5,5,0.3,0,</pot0></response>";
    protected const string SuccessBody = "Success!";

    protected readonly Mock<RestComms> RestClient = new("host", (ushort)1, "test");
    protected readonly ConcurrentQueue<string> Requests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new();
    private readonly ConcurrentDictionary<string, (string Body, HttpStatusCode Status)> _replies = new();
    private readonly ConcurrentDictionary<string, bool> _unanswered = new();

    protected ServerEdgePduFixture()
    {
        RestClient.Setup(client => client.Get(It.IsAny<Uri>()))
            .Returns<Uri?>(async uri =>
            {
                var path = uri!.OriginalString;
                Requests.Enqueue(path);
                if (_holds.TryRemove(path, out var hold))
                    await hold.Task;
                if (_replies.TryGetValue(path, out var reply))
                    Respond(reply.Body, reply.Status);
                if (_unanswered.ContainsKey(path))
                    SetConnection(ConnectionState.Error);
            });
    }

    protected ServerEdgePdu CreatePdu() =>
        new(RestClient.Object, "test", ServerEdgePdu.DefaultUser, ServerEdgePdu.DefaultPassword, 8);

    protected void DoNotAnswer(string path) => _unanswered[path] = true;

    protected void Respond(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        RestClient.Object.HttpResponseHandlers!.Invoke(new HttpResponseMessage(status) { Content = new StringContent(body) });

    protected void ReplyTo(string path, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _replies[path] = (body, status);

    protected static Task RequestsSent(ServerEdgePdu pdu) => (Task)typeof(ServerEdgePdu)
        .GetField("_requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pdu)!;

    protected void SetConnection(ConnectionState state) => typeof(CommunicationClient)
        .GetProperty(nameof(CommunicationClient.ConnectionState))!.SetValue(RestClient.Object, state);

    protected TaskCompletionSource HoldNextRequest(string path)
    {
        var release = new TaskCompletionSource();
        _holds[path] = release;
        return release;
    }

    protected bool HoldTaken(string path) => !_holds.ContainsKey(path);

    protected async Task WaitForStartup(ServerEdgePdu pdu)
    {
        await Wait.UntilAsync(() => Requests.Count == 2, "The PDU never asked for its names and its status");
        await RequestsSent(pdu);
        Requests.Clear();
    }
}

public class ServerEdgePduTest : ServerEdgePduFixture, IDisposable
{
    private readonly ServerEdgePdu _serverEdgePdu;

    public ServerEdgePduTest()
    {
        _serverEdgePdu = CreatePdu();
    }

    public void Dispose() => _serverEdgePdu.Dispose();

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
        await WaitForStartup(_serverEdgePdu);
        Respond(StatusBody);
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        ReplyTo("/ons.cgi?led=11000000", SuccessBody);

        _serverEdgePdu.Outlets[1].PowerOn();
        await RequestsSent(_serverEdgePdu);

        Assert.Equal(new[] { StatusPath, "/ons.cgi?led=11000000" }, Requests.ToArray());
        Assert.Equal(PowerState.On, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[2].PowerState);
    }

    [Fact]
    public async Task TwoCommandsInARow_AreSentInOrder()
    {
        await WaitForStartup(_serverEdgePdu);
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        ReplyTo("/ons.cgi?led=11000000", SuccessBody);
        ReplyTo("/offs.cgi?led=01111111", SuccessBody);
        var firstStatusRead = HoldNextRequest(StatusPath);

        _serverEdgePdu.Outlets[1].PowerOn();
        await Wait.UntilAsync(() => HoldTaken(StatusPath), "The first status read never started");
        _serverEdgePdu.Outlets[2].PowerOff();
        firstStatusRead.SetResult();
        await RequestsSent(_serverEdgePdu);

        Assert.Equal(new[] { StatusPath, "/ons.cgi?led=11000000", StatusPath, "/offs.cgi?led=01111111" },
            Requests.ToArray());
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
        Assert.Null(RestClient.Object.HttpResponseHandlers);
        Assert.Null(RestClient.Object.ConnectionStateHandlers);
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

[Collection(LoggerFactoryCollection.Name)]
public class ServerEdgePduLogTest : ServerEdgePduFixture, IDisposable
{
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly ILoggerFactory _originalLoggerFactory = LogBase.LoggerFactory;
    private readonly ServerEdgePdu _serverEdgePdu;

    public ServerEdgePduLogTest()
    {
        LogBase.LoggerFactory = _logs;
        _serverEdgePdu = CreatePdu();
    }

    public void Dispose()
    {
        _serverEdgePdu.Dispose();
        LogBase.LoggerFactory = _originalLoggerFactory;
    }

    [Fact]
    public async Task PowerOn_WhenTheStatusReadFails_SendsNothing()
    {
        await WaitForStartup(_serverEdgePdu);
        Respond(StatusBody);
        ReplyTo(StatusPath, "<html><body>Wrong password");

        _serverEdgePdu.Outlets[1].PowerOn();
        await RequestsSent(_serverEdgePdu);

        Assert.Equal(new[] { StatusPath }, Requests.ToArray());
        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning && log.Message ==
            "Not sending the power on command for outlet Unknown, the status page could not be read: " +
            "HTTP 200: <html><body>Wrong password");
    }

    [Fact]
    public async Task PowerOn_WhenTheCommandFails_LeavesTheShownStateAsItWas()
    {
        await WaitForStartup(_serverEdgePdu);
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        ReplyTo("/ons.cgi?led=11000000", "500 Internal Server Error", HttpStatusCode.InternalServerError);

        _serverEdgePdu.Outlets[1].PowerOn();
        await RequestsSent(_serverEdgePdu);

        Assert.Equal(new[] { StatusPath, "/ons.cgi?led=11000000" }, Requests.ToArray());
        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning && log.Message ==
            "The power on command for outlet Unknown failed: HTTP 500: 500 Internal Server Error");
    }

    [Fact]
    public async Task Reboot_WhenTheCommandGetsNoReply_LeavesTheShownStateAsItWas()
    {
        await WaitForStartup(_serverEdgePdu);
        ReplyTo(StatusPath, OnlyTheFirstOutletOnBody);
        DoNotAnswer("/offon.cgi?led=01000000");

        _serverEdgePdu.Outlets[1].Reboot();
        await RequestsSent(_serverEdgePdu);

        Assert.Equal(new[] { StatusPath, "/offon.cgi?led=01000000" }, Requests.ToArray());
        Assert.Equal(CommunicationState.Error, _serverEdgePdu.CommunicationState);
        Assert.Equal(PowerState.Off, _serverEdgePdu.Outlets[1].PowerState);
        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning && log.Message == "The reboot command for outlet Unknown failed: no reply");
    }

    [Fact]
    public async Task PduPowerOff_WhenTheCommandFails_LeavesTheShownStatesAsTheyWere()
    {
        await WaitForStartup(_serverEdgePdu);
        Respond(StatusBody);
        ReplyTo("/offs.cgi?led=11111111", "500 Internal Server Error", HttpStatusCode.InternalServerError);

        _serverEdgePdu.PowerOff();
        await RequestsSent(_serverEdgePdu);

        Assert.Equal(new[] { "/offs.cgi?led=11111111" }, Requests.ToArray());
        Assert.Equal(PowerState.On, _serverEdgePdu.Outlets[0].PowerState);
        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning && log.Message ==
            "The power off command for all outlets failed: HTTP 500: 500 Internal Server Error");
    }
}
