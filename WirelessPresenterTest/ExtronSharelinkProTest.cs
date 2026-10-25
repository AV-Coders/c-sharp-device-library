using System.Collections.Concurrent;
using System.Diagnostics;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Moq;
using WirelessPresenter;

namespace WirelessPresenterTest;

public class ExtronSharelinkProTest : IDisposable
{
    private readonly ExtronSharelinkPro _sharelink;
    private readonly Mock<SshClient> _mockClient = TestFactory.CreateSshClient();
    private readonly CapturingLoggerFactory _logs = new();
    private const string EscapeHeader = "\x1b";
    private static readonly TimeSpan NotifyWait = ExtronSharelinkPro.ConnectedUsersNotifyDelay + TimeSpan.FromMilliseconds(500);
    private readonly ExtronSharelinkUser _user = new ExtronSharelinkUser("421380106", string.Empty, string.Empty,
        string.Empty, false, string.Empty, string.Empty);
    private static readonly string[] Initialisation =
        [$"{EscapeHeader}3CV\r", $"{EscapeHeader}0ECHO\r", $"{EscapeHeader}QSHAR\r", $"{EscapeHeader}KSHAR\r"];
    private static readonly TimeSpan NoFurtherSendsWait = TimeSpan.FromSeconds(1.5);

    public ExtronSharelinkProTest()
    {
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = _logs;
        try
        {
            _sharelink = new ExtronSharelinkPro(_mockClient.Object, "Test Sharelink");
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }

    public void Dispose() => _sharelink.Dispose();

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        public readonly ConcurrentQueue<(LogLevel Level, string Message)> Logs = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Logs.Enqueue((logLevel, formatter(state, exception)));
    }

    private static void SetConnection(Mock<SshClient> client, ConnectionState state) =>
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, state);

    private static List<string> SentCommands(Mock<SshClient> client) => client.Invocations
        .Where(invocation => invocation.Method.Name == nameof(CommunicationClient.Send) &&
                             invocation.Arguments[0] is string)
        .Select(invocation => (string)invocation.Arguments[0])
        .ToList();

    [Theory]
    [InlineData("SharQ1", PowerState.Off)] // Standby
    [InlineData("SharQ2", PowerState.On)] // Connected
    [InlineData("SharQ3", PowerState.On)] // Expo
    [InlineData("SharQ4", PowerState.On)] // Expo Standby
    [InlineData("SharQ5", PowerState.On)] // Sharing
    public void HandleResponse_UpdatesPowerState(string response, PowerState expectedState)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        Assert.Equal(expectedState, _sharelink.PowerState);
    }

    [Fact]
    public void HandleResponse_RequestsConnectedUserInfo()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("SharK1\n");
        _mockClient.Verify(x => x.Send($"{EscapeHeader}L0SHAR\r"));
    }

    [Fact]
    public void HandleResponse_UpdatesTheConnectedUsersList()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("SharL421380106*Ac-16*1963993098*22F76,airplay,screen*1*0*0*10000*10000*0*622338880*Ac-16\n");
        Assert.Single(_sharelink.ConnectedUsers);
        var user = _sharelink.ConnectedUsers.First();
        Assert.Equal("421380106", user.ConnectionId);
        Assert.Equal("Ac-16", user.UserName);
        Assert.Equal("1963993098", user.StreamId);
        Assert.Equal("22F76,airplay,screen", user.Platform);
        Assert.True(user.ConnectionApproved);
        Assert.Equal("622338880", user.VideoPortId);
        Assert.Equal("Ac-16", user.DeviceName);
    }

    [Fact]
    public void HandleResponse_HandlesMoreThanOneUserResponse()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("SharL827494424*Ac-16*1663926294*22F76,airplay,casting*1*0*0*5000*10000*69*621123328*Ac-16\n");
        _mockClient.Object.ResponseHandlers!.Invoke("84234268*AC-NUCExtreme*1068064792*windows,extron,none*1*5000*0*5000*10000*46*623238272*AC-NUCExtreme\n");
        
        Assert.Equal(2, _sharelink.ConnectedUsers.Count);
        var user = _sharelink.ConnectedUsers[1];
        Assert.Equal("84234268", user.ConnectionId);
        Assert.Equal("AC-NUCExtreme", user.UserName);
        Assert.Equal("1068064792", user.StreamId);
        Assert.Equal("windows,extron,none", user.Platform);
        Assert.True(user.ConnectionApproved);
        Assert.Equal("623238272", user.VideoPortId);
        Assert.Equal("AC-NUCExtreme", user.DeviceName);
    }

    [Fact]
    public void HandleResponse_TriggersAConnectedUsersEvent()
    {
        Mock<IntHandler> mockHandler = new();
        _sharelink.ConnectedUsersHandlers += mockHandler.Object;
        _mockClient.Object.ResponseHandlers!.Invoke("SharL827494424*Ac-16*1663926294*22F76,airplay,casting*1*0*0*5000*10000*69*621123328*Ac-16\n");
        _mockClient.Object.ResponseHandlers!.Invoke("84234268*AC-NUCExtreme*1068064792*windows,extron,none*1*5000*0*5000*10000*46*623238272*AC-NUCExtreme\n");
        
        Thread.Sleep(TimeSpan.FromSeconds(3));
        mockHandler.Verify(x => x.Invoke(2));
    }

    [Fact]
    public void HandleResponse_NotifiesTheConnectedUsersOnceAfterTheLastChange()
    {
        var notifications = new BlockingCollection<int>();
        _sharelink.ConnectedUsersHandlers += notifications.Add;
        _mockClient.Object.ResponseHandlers!.Invoke("SharL827494424*Ac-16*1663926294*22F76,airplay,casting*1*0*0*5000*10000*69*621123328*Ac-16\n");
        _mockClient.Object.ResponseHandlers!.Invoke("84234268*AC-NUCExtreme*1068064792*windows,extron,none*1*5000*0*5000*10000*46*623238272*AC-NUCExtreme\n");

        Assert.True(notifications.TryTake(out var count, NotifyWait), "The connected users were never notified");
        Assert.Equal(2, count);
        Assert.False(notifications.TryTake(out _, NotifyWait), "The connected users were notified again without a change");
    }

    [Fact]
    public void HandleResponse_AfterANotification_NotifiesTheNextChange()
    {
        var notifications = new BlockingCollection<int>();
        _sharelink.ConnectedUsersHandlers += notifications.Add;
        _mockClient.Object.ResponseHandlers!.Invoke("SharL827494424*Ac-16*1663926294*22F76,airplay,casting*1*0*0*5000*10000*69*621123328*Ac-16\n");
        Assert.True(notifications.TryTake(out var first, NotifyWait), "The first change was never notified");

        _mockClient.Object.ResponseHandlers!.Invoke("84234268*AC-NUCExtreme*1068064792*windows,extron,none*1*5000*0*5000*10000*46*623238272*AC-NUCExtreme\n");

        Assert.True(notifications.TryTake(out var second, NotifyWait), "The later change was never notified");
        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact]
    public void HandleResponse_WithAnUnparsableShareState_LogsAndKeepsThePowerState()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("SharQx\n");

        Assert.Equal(PowerState.Unknown, _sharelink.PowerState);
        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning && log.Message.Contains("SharQx"));
    }

    [Theory]
    [InlineData("SharL421380106*Ac-16*1963993098*22F76,airplay,screen*1\n")]
    [InlineData("SharL421380106*Ac-16*1963993098*22F76,airplay,screen*7*0*0*10000*10000*0*622338880*Ac-16\n")]
    public void HandleResponse_WithAnUnusableUserReply_LogsAndIgnoresIt(string response)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);

        Assert.Empty(_sharelink.ConnectedUsers);
        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Warning && log.Message.Contains(response));
    }

    [Fact]
    public void SendCommand_LogsTheCommandAtVerbose()
    {
        _sharelink.DisconnectUser(_user);

        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Trace && log.Message.Contains("D421380106SHAR"));
        Assert.DoesNotContain(_logs.Logs, log => log.Level == LogLevel.Information && log.Message.Contains("Sending"));
    }

    [Fact]
    public void Dispose_StopsRespondingToTheClientAndDeregisters()
    {
        _sharelink.Dispose();

        _mockClient.Object.ResponseHandlers?.Invoke("SharQ2");

        Assert.Equal(PowerState.Unknown, _sharelink.PowerState);
        Assert.DoesNotContain<LogBase>(_sharelink, LogBaseRegistry.GetAll());
    }

    [Fact]
    public async Task Dispose_DuringTheInitialisation_SendsNothingMore()
    {
        SetConnection(_mockClient, ConnectionState.Connected);
        SetConnection(_mockClient, ConnectionState.Disconnected);
        SetConnection(_mockClient, ConnectionState.Connected);
        await Wait.UntilAsync(() => SentCommands(_mockClient).Contains($"{EscapeHeader}3CV\r"),
            "The initialisation never started");

        _sharelink.Dispose();
        await Task.Delay(NoFurtherSendsWait);

        Assert.Equal([$"{EscapeHeader}3CV\r"], SentCommands(_mockClient));
    }

    [Fact]
    public async Task Constructor_WithAConnectedClient_Initialises()
    {
        var connectedClient = TestFactory.CreateSshClient();
        SetConnection(connectedClient, ConnectionState.Connected);

        using var sharelink = new ExtronSharelinkPro(connectedClient.Object, "Connected Sharelink");

        await Wait.UntilAsync(() => SentCommands(connectedClient).Contains($"{EscapeHeader}KSHAR\r"),
            "The Sharelink never initialised the connected client");
        Assert.Equal(Initialisation, SentCommands(connectedClient));
    }

    [Fact]
    public async Task HandleConnectionState_ReturnsAtOnceAndSendsTheInitialisationInOrder()
    {
        var stopwatch = Stopwatch.StartNew();
        SetConnection(_mockClient, ConnectionState.Connected);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500, "The connection handler blocked the client");
        await Wait.UntilAsync(() => SentCommands(_mockClient).Contains($"{EscapeHeader}KSHAR\r"),
            "The initialisation never finished");
        Assert.Equal(Initialisation, SentCommands(_mockClient));
    }

    [Fact]
    public async Task HandleConnectionState_TwoConnectsInQuickSuccession_SendEachInitialisationInTurn()
    {
        SetConnection(_mockClient, ConnectionState.Connected);
        var stopwatch = Stopwatch.StartNew();
        SetConnection(_mockClient, ConnectionState.Disconnected);
        SetConnection(_mockClient, ConnectionState.Connected);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 500, "The second connect waited on the first initialisation");
        await Wait.UntilAsync(() => SentCommands(_mockClient).Count(command => command == $"{EscapeHeader}KSHAR\r") == 2,
            "The second initialisation never finished");
        Assert.Equal([..Initialisation, ..Initialisation], SentCommands(_mockClient));
    }

    [Fact]
    public async Task HandleConnectionState_AfterAnInitialisationThrows_TheNextConnectStillInitialises()
    {
        var throwing = true;
        _sharelink.CommunicationStateHandlers += _ =>
        {
            if (throwing)
                throw new InvalidOperationException("disposed control");
        };
        SetConnection(_mockClient, ConnectionState.Connected);
        await Wait.UntilAsync(() => _logs.Logs.Any(log => log.Message.Contains("Initialising the Sharelink failed")),
            "The first initialisation never failed");
        throwing = false;

        SetConnection(_mockClient, ConnectionState.Disconnected);
        SetConnection(_mockClient, ConnectionState.Connected);

        await Wait.UntilAsync(() => SentCommands(_mockClient).Contains($"{EscapeHeader}KSHAR\r"),
            "The second initialisation never finished");
        Assert.Equal([$"{EscapeHeader}3CV\r", ..Initialisation], SentCommands(_mockClient));
    }

    [Fact]
    public void DisconnectUser_SendsTheCommand()
    {
        _sharelink.DisconnectUser(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}D421380106SHAR\r"));
    }

    [Fact]
    public void ApproveConnectionRequest_SendsTheCommand()
    {
        _sharelink.ApproveConnectionRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}C421380106*1SHAR\r"));
    }

    [Fact]
    public void DenyConnectionRequest_SendsTheCommand()
    {
        _sharelink.DenyConnectionRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}C421380106*0SHAR\r"));
    }

    [Fact]
    public void ApproveShareRequest_SendsTheCommand()
    {
        _sharelink.ApproveShareRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}R421380106*1SHAR\r"));
    }

    [Fact]
    public void DenyShareRequest_SendsTheCommand()
    {
        _sharelink.DenyShareRequest(_user);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}R421380106*0SHAR\r"));
    }

    [Theory]
    [InlineData("UserChg")]
    public void HandleResponse_RequestsUserStatus(string response)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}KSHAR\r"));
    }
}