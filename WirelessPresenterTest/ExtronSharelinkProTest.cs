using System.Collections.Concurrent;
using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using WirelessPresenter;

namespace WirelessPresenterTest;

public class ExtronSharelinkProTest : IDisposable
{
    internal const string EscapeHeader = "\x1b";
    internal static readonly string VerboseMode = $"{EscapeHeader}3CV\r";
    private static readonly string UserStatus = $"{EscapeHeader}KSHAR\r";
    private static readonly string[] Initialisation =
        [VerboseMode, $"{EscapeHeader}0ECHO\r", $"{EscapeHeader}QSHAR\r", UserStatus];
    private static readonly TimeSpan ConnectStep = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NotifyStep = TimeSpan.FromMilliseconds(250);
    private const string FirstUser = "SharL827494424*Ac-16*1663926294*22F76,airplay,casting*1*0*0*5000*10000*69*621123328*Ac-16\n";
    private const string SecondUser = "84234268*AC-NUCExtreme*1068064792*windows,extron,none*1*5000*0*5000*10000*46*623238272*AC-NUCExtreme\n";
    private const int FailingInitialisationStateChanges = 2;
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<SshClient> _mockClient = TestFactory.CreateSshClient();
    private readonly ExtronSharelinkPro _sharelink;
    private static readonly ExtronSharelinkUser User = new("421380106", string.Empty, string.Empty,
        string.Empty, false, string.Empty, string.Empty);

    public ExtronSharelinkProTest()
    {
        _sharelink = new ExtronSharelinkPro(_mockClient.Object, "Test Sharelink", _time);
    }

    public void Dispose() => _sharelink.Dispose();

    internal static void SetConnection(Mock<SshClient> client, ConnectionState state) =>
        typeof(CommunicationClient).GetProperty(nameof(CommunicationClient.ConnectionState))!
            .SetValue(client.Object, state);

    internal static List<string> SentCommands(Mock<SshClient> client) => client.Invocations
        .Where(invocation => invocation.Method.Name == nameof(CommunicationClient.Send) &&
                             invocation.Arguments[0] is string)
        .Select(invocation => (string)invocation.Arguments[0])
        .ToList();

    private static ThreadWorker Notifier(ExtronSharelinkPro sharelink) =>
        (ThreadWorker)typeof(ExtronSharelinkPro)
            .GetField("_connectedUserNotifier", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(sharelink)!;

    [Theory]
    [InlineData("SharQ1", PowerState.Off)]
    [InlineData("SharQ2", PowerState.On)]
    [InlineData("SharQ3", PowerState.On)]
    [InlineData("SharQ4", PowerState.On)]
    [InlineData("SharQ5", PowerState.On)]
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
        _mockClient.Object.ResponseHandlers!.Invoke(FirstUser);
        _mockClient.Object.ResponseHandlers!.Invoke(SecondUser);

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
    public async Task HandleResponse_NotifiesTheConnectedUsersOnceAfterTheLastChange()
    {
        var notifications = new ConcurrentQueue<int>();
        _sharelink.ConnectedUsersHandlers += notifications.Enqueue;
        _mockClient.Object.ResponseHandlers!.Invoke(FirstUser);
        _mockClient.Object.ResponseHandlers!.Invoke(SecondUser);

        await Wait.UntilAsync(_time, NotifyStep, () => !notifications.IsEmpty, "The connected users were never notified");
        await Wait.UntilAsync(() => !Notifier(_sharelink).IsRunning, "The notifier kept running after it notified");

        Assert.Equal(new[] { 2 }, notifications);
    }

    [Fact]
    public async Task HandleResponse_AfterANotification_NotifiesTheNextChange()
    {
        var notifications = new ConcurrentQueue<int>();
        _sharelink.ConnectedUsersHandlers += notifications.Enqueue;
        _mockClient.Object.ResponseHandlers!.Invoke(FirstUser);
        await Wait.UntilAsync(_time, NotifyStep, () => notifications.Count == 1, "The first change was never notified");

        _mockClient.Object.ResponseHandlers!.Invoke(SecondUser);
        await Wait.UntilAsync(_time, NotifyStep, () => notifications.Count == 2, "The later change was never notified");

        Assert.Equal(new[] { 1, 2 }, notifications);
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
    public async Task Constructor_WithAConnectedClient_Initialises()
    {
        var connectedClient = TestFactory.CreateSshClient();
        SetConnection(connectedClient, ConnectionState.Connected);

        using var sharelink = new ExtronSharelinkPro(connectedClient.Object, "Connected Sharelink", _time);

        await Wait.UntilAsync(_time, ConnectStep, () => SentCommands(connectedClient).Contains(UserStatus),
            "The Sharelink never initialised the connected client");
        Assert.Equal(Initialisation, SentCommands(connectedClient));
    }

    [Fact]
    public async Task HandleConnectionState_ReturnsAtOnceAndSendsTheInitialisationInOrder()
    {
        var connecting = Task.Run(() => SetConnection(_mockClient, ConnectionState.Connected));

        await Wait.UntilAsync(() => connecting.IsCompleted, "The connection handler blocked the client");
        Assert.DoesNotContain(UserStatus, SentCommands(_mockClient));
        await Wait.UntilAsync(_time, ConnectStep, () => SentCommands(_mockClient).Contains(UserStatus),
            "The initialisation never finished");
        Assert.Equal(Initialisation, SentCommands(_mockClient));
    }

    [Fact]
    public async Task HandleConnectionState_TwoConnectsInQuickSuccession_SendEachInitialisationInTurn()
    {
        SetConnection(_mockClient, ConnectionState.Connected);
        var reconnecting = Task.Run(() =>
        {
            SetConnection(_mockClient, ConnectionState.Disconnected);
            SetConnection(_mockClient, ConnectionState.Connected);
        });

        await Wait.UntilAsync(() => reconnecting.IsCompleted, "The second connect waited on the first initialisation");
        Assert.DoesNotContain(UserStatus, SentCommands(_mockClient));
        await Wait.UntilAsync(_time, ConnectStep, () => SentCommands(_mockClient).Count(command => command == UserStatus) == 2,
            "The second initialisation never finished");
        Assert.Equal([..Initialisation, ..Initialisation], SentCommands(_mockClient));
    }

    [Fact]
    public async Task HandleConnectionState_AfterAnInitialisationThrows_TheNextConnectStillInitialises()
    {
        var stateChanges = 0;
        _sharelink.CommunicationStateHandlers += _ =>
        {
            if (Interlocked.Increment(ref stateChanges) <= FailingInitialisationStateChanges)
                throw new InvalidOperationException("disposed control");
        };
        SetConnection(_mockClient, ConnectionState.Connected);
        SetConnection(_mockClient, ConnectionState.Disconnected);
        SetConnection(_mockClient, ConnectionState.Connected);

        await Wait.UntilAsync(_time, ConnectStep, () => SentCommands(_mockClient).Contains(UserStatus),
            "The second initialisation never finished");
        Assert.Equal([VerboseMode, ..Initialisation], SentCommands(_mockClient));
    }

    public static TheoryData<Action<ExtronSharelinkPro, ExtronSharelinkUser>, string> UserCommands => new()
    {
        { (sharelink, user) => sharelink.DisconnectUser(user), "D421380106SHAR" },
        { (sharelink, user) => sharelink.ApproveConnectionRequest(user), "C421380106*1SHAR" },
        { (sharelink, user) => sharelink.DenyConnectionRequest(user), "C421380106*0SHAR" },
        { (sharelink, user) => sharelink.ApproveShareRequest(user), "R421380106*1SHAR" },
        { (sharelink, user) => sharelink.DenyShareRequest(user), "R421380106*0SHAR" },
    };

    [Theory]
    [MemberData(nameof(UserCommands))]
    public void UserCommand_SendsTheCommand(Action<ExtronSharelinkPro, ExtronSharelinkUser> command, string expected)
    {
        command(_sharelink, User);
        _mockClient.Verify(x => x.Send($"{EscapeHeader}{expected}\r"));
    }

    [Fact]
    public void HandleResponse_RequestsUserStatus()
    {
        _mockClient.Object.ResponseHandlers!.Invoke("UserChg");
        _mockClient.Verify(x => x.Send(UserStatus));
    }
}

[Collection(LoggerFactoryCollection.Name)]
public class ExtronSharelinkProLogTest : IDisposable
{
    private readonly ILoggerFactory _originalLoggerFactory = LogBase.LoggerFactory;
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<SshClient> _mockClient = TestFactory.CreateSshClient();
    private readonly ExtronSharelinkPro _sharelink;
    private static readonly ExtronSharelinkUser User = new("421380106", string.Empty, string.Empty,
        string.Empty, false, string.Empty, string.Empty);

    public ExtronSharelinkProLogTest()
    {
        LogBase.LoggerFactory = _logs;
        _sharelink = new ExtronSharelinkPro(_mockClient.Object, "Test Sharelink", _time);
    }

    public void Dispose()
    {
        _sharelink.Dispose();
        LogBase.LoggerFactory = _originalLoggerFactory;
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
        _sharelink.DisconnectUser(User);

        Assert.Contains(_logs.Logs, log => log.Level == LogLevel.Trace && log.Message.Contains("D421380106SHAR"));
        Assert.DoesNotContain(_logs.Logs, log => log.Level == LogLevel.Information && log.Message.Contains("Sending"));
    }

    [Fact]
    public async Task Dispose_DuringTheInitialisation_SendsNothingMore()
    {
        ExtronSharelinkProTest.SetConnection(_mockClient, ConnectionState.Connected);
        ExtronSharelinkProTest.SetConnection(_mockClient, ConnectionState.Disconnected);
        ExtronSharelinkProTest.SetConnection(_mockClient, ConnectionState.Connected);
        await Wait.UntilAsync(() => ExtronSharelinkProTest.SentCommands(_mockClient).Contains(ExtronSharelinkProTest.VerboseMode),
            "The initialisation never started");

        _sharelink.Dispose();
        await Wait.UntilAsync(() => _logs.Logs.Any(log => log.Message.Contains("Stopped initialising, the Sharelink was disposed")),
            "The initialisation never stopped");

        Assert.Equal([ExtronSharelinkProTest.VerboseMode], ExtronSharelinkProTest.SentCommands(_mockClient));
    }
}
