using System.Collections.Concurrent;
using System.Net;
using AVCoders.Core;
using AVCoders.Display;
using AVCoders.SignalR.Display;
using AVCoders.SignalR.Room;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Audit.Tests;

public class HubAuditFilterTest
{
    private const string PanelAddress = "::ffff:10.0.0.21";
    private const string PanelIp = "10.0.0.21";
    private const string PanelUserAgent = "Mozilla/5.0 (Linux; Android 9; TSW-1070)";
    private const string TriggerIdPattern = "^[0-9a-f]{8}$";
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(2);
    private readonly HubAuditFilter _filter = new();
    private readonly string _groupName = $"audit-room-{Guid.NewGuid()}";
    private readonly string _connectionId = $"conn-{Guid.NewGuid()}";
    private readonly FeatureCollection _features = new();
    private readonly Mock<HubCallerContext> _context = new();

    public HubAuditFilterTest()
    {
        _context.Setup(c => c.ConnectionId).Returns(_connectionId);
        _context.Setup(c => c.Features).Returns(_features);
    }

    [Fact]
    public async Task StateChangingCall_RecordsTargetActionAndDetail()
    {
        ConnectFromPanel();

        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.SetInput), _groupName, Input.Hdmi2), Next);

        var entry = Assert.Single(Recorded());
        Assert.Equal(_groupName, entry.Target);
        Assert.Equal("Display.SetInput", entry.Action);
        Assert.Equal("input=Hdmi2", entry.Detail);
        Assert.Equal(new Actor(ActorKind.Panel, PanelIp, PanelUserAgent, entry.Actor!.HostName, _connectionId), entry.Actor);
    }

    [Fact]
    public async Task StateChangingCall_SetsActorInsideNext()
    {
        ConnectFromPanel();
        Actor? actorInNext = null;

        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.PowerOn), _groupName), _ =>
        {
            actorInNext = Actor.Current;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.Equal(ActorKind.Panel, actorInNext!.Kind);
        Assert.Equal(PanelIp, actorInNext.Ip);
        Assert.Equal(PanelUserAgent, actorInNext.UserAgent);
        Assert.Equal(_connectionId, actorInNext.ConnectionId);
        Assert.Null(Actor.Current);
    }

    [Fact]
    public async Task JoinGroup_RecordsNothing()
    {
        ConnectFromPanel();
        var nextCalled = false;

        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.JoinGroup), _groupName), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(nextCalled);
        Assert.Empty(Recorded());
    }

    [Fact]
    public async Task GetMethod_RecordsNothing()
    {
        ConnectFromPanel();
        var nextCalled = false;

        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.GetGroups)), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });

        Assert.True(nextCalled);
        Assert.Empty(Recorded());
    }

    [Fact]
    public async Task PowerOnWithArgs_RecordsKeysNotValues()
    {
        ConnectFromPanel();
        var args = new Dictionary<string, string> { ["presenter"] = "Jane Citizen", ["mode"] = "hybrid" };

        await _filter.InvokeMethodAsync(Invocation(new RoomHub(), nameof(RoomHub.PowerOnWithArgs), _groupName, args), Next);

        var entry = Assert.Single(Recorded());
        Assert.Equal("Room.PowerOnWithArgs", entry.Action);
        Assert.Equal("args=[presenter, mode]", entry.Detail);
    }

    [Fact]
    public async Task NoHttpContext_RecordsEmptyIdentity()
    {
        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.SetVolume), _groupName, 40), Next);

        var entry = Assert.Single(Recorded());
        Assert.Equal("volume=40", entry.Detail);
        Assert.Equal(new Actor(ActorKind.Panel, string.Empty, string.Empty, null, _connectionId), entry.Actor);
    }

    [Fact]
    public async Task CallWithoutGroup_TargetsTheHub()
    {
        ConnectFromPanel();

        await _filter.InvokeMethodAsync(Invocation(new ProgramHub(), nameof(ProgramHub.Mute)), Next);

        var entry = Assert.Single(Recorded());
        Assert.Equal("Program", entry.Target);
        Assert.Equal("Program.Mute", entry.Action);
        Assert.Equal(string.Empty, entry.Detail);
    }

    [Fact]
    public async Task PinArgument_IsHidden()
    {
        ConnectFromPanel();

        await _filter.InvokeMethodAsync(Invocation(new ProgramHub(), nameof(ProgramHub.SavePreset), "Lobby Camera", 3, "1234"), Next);

        var entry = Assert.Single(Recorded());
        Assert.Equal("Lobby Camera", entry.Target);
        Assert.Equal("index=3 pin=[redacted]", entry.Detail);
    }

    [Fact]
    public async Task CheckPin_IsRecordedWithThePinHidden()
    {
        ConnectFromPanel();

        await _filter.InvokeMethodAsync(Invocation(new ProgramHub(), nameof(ProgramHub.CheckPin), "1234"), Next);

        var entry = Assert.Single(Recorded());
        Assert.Equal("Program", entry.Target);
        Assert.Equal("Program.CheckPin", entry.Action);
        Assert.Equal("pin=[redacted]", entry.Detail);
    }

    [Fact]
    public async Task EachCall_StartsItsOwnTriggerIdInsideNext()
    {
        ConnectFromPanel();
        var idsInNext = new List<string?>();

        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.JoinGroup), _groupName), _ =>
        {
            idsInNext.Add(Trigger.Id);
            return ValueTask.FromResult<object?>(null);
        });
        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.GetGroups)), _ =>
        {
            idsInNext.Add(Trigger.Id);
            return ValueTask.FromResult<object?>(null);
        });

        Assert.Equal(2, idsInNext.Count);
        Assert.All(idsInNext, id => Assert.Matches(TriggerIdPattern, id));
        Assert.NotEqual(idsInNext[0], idsInNext[1]);
        Assert.Null(Trigger.Id);
    }

    [Fact]
    public async Task StateChangingCall_RecordsTheTriggerIdSeenInsideNext()
    {
        ConnectFromPanel();
        string? idInNext = null;

        await _filter.InvokeMethodAsync(Invocation(new DisplayHub(), nameof(DisplayHub.PowerOn), _groupName), _ =>
        {
            idInNext = Trigger.Id;
            return ValueTask.FromResult<object?>(null);
        });

        var entry = Assert.Single(Recorded());
        Assert.Matches(TriggerIdPattern, idInNext);
        Assert.Equal(idInNext, entry.TriggerId);
    }

    [Fact]
    public async Task HubDispatch_CarriesActorAndTriggerIdToTheManagerAndTheLogScope()
    {
        ConnectFromPanel();
        var logs = new ConcurrentQueue<CapturedLog>();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = new ScopeCapturingLoggerFactory(logs);
        try
        {
            var manager = new RoomManager(new Mock<DeviceBase>(_groupName, CommunicationClient.None).Object);
            RoomHub.RegisterRoomManager(_groupName, manager);
            var handled = new TaskCompletionSource<(Actor? Actor, string? TriggerId)>();
            manager.OnPowerOnRequested += _ => handled.TrySetResult((Actor.Current, Trigger.Id));

            await _filter.InvokeMethodAsync(
                Invocation(new RoomHub(), nameof(RoomHub.PowerOnWithArgs), _groupName, new Dictionary<string, string>()),
                InvokeHubMethod);

            var (actor, triggerId) = await handled.Task.WaitAsync(DispatchTimeout);
            var entry = Assert.Single(Recorded());
            Assert.Matches(TriggerIdPattern, triggerId);
            Assert.Equal(entry.Actor, actor);
            Assert.Equal(entry.TriggerId, triggerId);
            var log = Assert.Single(logs, l => l.Message.StartsWith($"Powering on {_groupName}"));
            Assert.Equal(actor!.ToString(), log.Scope[LogBase.ActorProperty]);
            Assert.Equal(triggerId, log.Scope[LogBase.TriggerIdProperty]);
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }

    private void ConnectFromPanel()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(PanelAddress);
        httpContext.Request.Headers.UserAgent = PanelUserAgent;
        _features.Set(Mock.Of<IHttpContextFeature>(f => f.HttpContext == httpContext));
    }

    private HubInvocationContext Invocation(Hub hub, string methodName, params object?[] arguments) =>
        new(_context.Object, Mock.Of<IServiceProvider>(), hub, hub.GetType().GetMethod(methodName)!, arguments);

    private static ValueTask<object?> Next(HubInvocationContext invocationContext) => ValueTask.FromResult<object?>(null);

    private static ValueTask<object?> InvokeHubMethod(HubInvocationContext invocationContext) =>
        ValueTask.FromResult(invocationContext.HubMethod.Invoke(invocationContext.Hub, invocationContext.HubMethodArguments.ToArray()));

    private List<AuditEntry> Recorded() =>
        AVCoders.Core.Audit.GetEntries().Where(e => e.Actor?.ConnectionId == _connectionId).ToList();

    private sealed record CapturedLog(string Message, Dictionary<string, object?> Scope);

    private sealed class ScopeCapturingLoggerFactory(ConcurrentQueue<CapturedLog> logs) : ILoggerFactory
    {
        private readonly LoggerExternalScopeProvider _scopes = new();

        public ILogger CreateLogger(string categoryName) => new ScopeCapturingLogger(logs, _scopes);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class ScopeCapturingLogger(ConcurrentQueue<CapturedLog> logs, IExternalScopeProvider scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object?>();
            scopes.ForEachScope((scopeState, properties) =>
            {
                if (scopeState is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var pair in pairs)
                        properties[pair.Key] = pair.Value;
            }, scope);
            logs.Enqueue(new CapturedLog(formatter(state, exception), scope));
        }
    }
}
