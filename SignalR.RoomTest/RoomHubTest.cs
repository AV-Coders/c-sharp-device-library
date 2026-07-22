using System.Collections.Concurrent;
using AVCoders.Core;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Room.Tests;

public class RoomHubTest
{
    private readonly TestDevice _device;
    private readonly RoomManager _manager;
    private readonly string _groupName;
    private readonly RoomHubTestHarness _harness;

    public RoomHubTest()
    {
        _groupName = $"hub-room-{Guid.NewGuid()}";
        _device = new TestDevice(_groupName);
        _manager = new RoomManager(_device);
        RoomHub.RegisterRoomManager(_groupName, _manager);
        _harness = RoomHubTestHarness.CreateHub();
    }

    [Fact]
    public async Task JoinGroup_AddsCallerToGroupAndSendsCurrentPowerState()
    {
        _device.SetPowerStateForTest(PowerState.On);

        await _harness.Hub.JoinGroup(_groupName);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), _groupName, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnPowerStateChanged(PowerState.On), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_SendsPropertiesSnapshot()
    {
        _manager.SetProperty("status", "in-meeting");
        _manager.SetProperty("occupancy", "4");

        await _harness.Hub.JoinGroup(_groupName);

        _harness.CallerMock.Verify(c => c.OnPropertiesSnapshot(
            It.Is<Dictionary<string, string>>(d =>
                d.Count == 2 &&
                d["status"] == "in-meeting" &&
                d["occupancy"] == "4")), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_SendsEmptyPropertiesSnapshotWhenNoneSet()
    {
        await _harness.Hub.JoinGroup(_groupName);

        _harness.CallerMock.Verify(c => c.OnPropertiesSnapshot(
            It.Is<Dictionary<string, string>>(d => d.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_StillJoinsButSendsNoState()
    {
        var unknown = $"missing-{Guid.NewGuid()}";

        await _harness.Hub.JoinGroup(unknown);

        _harness.GroupsMock.Verify(g => g.AddToGroupAsync(
            It.IsAny<string>(), unknown, It.IsAny<CancellationToken>()), Times.Once);
        _harness.CallerMock.Verify(c => c.OnPowerStateChanged(It.IsAny<PowerState>()), Times.Never);
        _harness.CallerMock.Verify(c => c.OnPropertiesSnapshot(It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public void GetGroups_ReturnsRegisteredGroupName()
    {
        var groups = _harness.Hub.GetGroups();

        Assert.Contains(_groupName, groups);
    }

    [Fact]
    public void PowerOn_ForwardsToManager()
    {
        _harness.Hub.PowerOn(_groupName);

        WaitFor(() => _device.PowerOnCallCount == 1);
        Assert.Equal(1, _device.PowerOnCallCount);
    }

    [Fact]
    public void PowerOn_UnknownGroup_DoesNotChangeKnownDevice()
    {
        _harness.Hub.PowerOn($"missing-{Guid.NewGuid()}");

        Assert.Equal(0, _device.PowerOnCallCount);
    }

    [Fact]
    public void PowerOff_ForwardsToManager()
    {
        _harness.Hub.PowerOff(_groupName);

        WaitFor(() => _device.PowerOffCallCount == 1);
        Assert.Equal(1, _device.PowerOffCallCount);
    }

    [Fact]
    public void PowerOff_UnknownGroup_DoesNotChangeKnownDevice()
    {
        _harness.Hub.PowerOff($"missing-{Guid.NewGuid()}");

        Assert.Equal(0, _device.PowerOffCallCount);
    }

    [Fact]
    public void RegisterRoomManager_ReplacesExistingRegistration()
    {
        var replacementDevice = new TestDevice($"replacement-{Guid.NewGuid()}");
        var replacement = new RoomManager(replacementDevice);
        RoomHub.RegisterRoomManager(_groupName, replacement);

        _harness.Hub.PowerOn(_groupName);

        WaitFor(() => replacementDevice.PowerOnCallCount == 1);
        Assert.Equal(1, replacementDevice.PowerOnCallCount);
        Assert.Equal(0, _device.PowerOnCallCount);
    }

    [Fact]
    public void PowerOnWithArgs_ForwardsArgsToManagerEvent()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOnRequested += handler.Object;
        var args = new Dictionary<string, string> { ["reason"] = "scheduled" };

        _harness.Hub.PowerOnWithArgs(_groupName, args);

        WaitFor(() => handler.Invocations.Count > 0);
        handler.Verify(h => h.Invoke(It.Is<Dictionary<string, string>>(
            d => d.Count == 1 && d["reason"] == "scheduled")), Times.Once);
    }

    [Fact]
    public void PowerOnWithArgs_DoesNotPowerDevice()
    {
        var raised = new ManualResetEventSlim();
        _manager.OnPowerOnRequested += _ => raised.Set();

        _harness.Hub.PowerOnWithArgs(_groupName, new Dictionary<string, string>());

        Assert.True(raised.Wait(2000), "OnPowerOnRequested was not raised within 2s");
        Assert.Equal(0, _device.PowerOnCallCount);
    }

    [Fact]
    public void PowerOnWithArgs_NullArgs_RaisesEventWithEmptyDictionary()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOnRequested += handler.Object;

        _harness.Hub.PowerOnWithArgs(_groupName, null!);

        WaitFor(() => handler.Invocations.Count > 0);
        handler.Verify(h => h.Invoke(It.Is<Dictionary<string, string>>(d => d.Count == 0)), Times.Once);
    }

    [Fact]
    public void PowerOnWithArgs_UnknownGroup_DoesNotRaiseEvent()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOnRequested += handler.Object;

        _harness.Hub.PowerOnWithArgs($"missing-{Guid.NewGuid()}", new Dictionary<string, string>());

        handler.Verify(h => h.Invoke(It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public void PowerOffWithArgs_ForwardsArgsToManagerEvent()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOffRequested += handler.Object;
        var args = new Dictionary<string, string> { ["reason"] = "idle" };

        _harness.Hub.PowerOffWithArgs(_groupName, args);

        WaitFor(() => handler.Invocations.Count > 0);
        handler.Verify(h => h.Invoke(It.Is<Dictionary<string, string>>(
            d => d.Count == 1 && d["reason"] == "idle")), Times.Once);
    }

    [Fact]
    public void PowerOffWithArgs_DoesNotPowerDevice()
    {
        var raised = new ManualResetEventSlim();
        _manager.OnPowerOffRequested += _ => raised.Set();

        _harness.Hub.PowerOffWithArgs(_groupName, new Dictionary<string, string>());

        Assert.True(raised.Wait(2000), "OnPowerOffRequested was not raised within 2s");
        Assert.Equal(0, _device.PowerOffCallCount);
    }

    [Fact]
    public void PowerOffWithArgs_NullArgs_RaisesEventWithEmptyDictionary()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOffRequested += handler.Object;

        _harness.Hub.PowerOffWithArgs(_groupName, null!);

        WaitFor(() => handler.Invocations.Count > 0);
        handler.Verify(h => h.Invoke(It.Is<Dictionary<string, string>>(d => d.Count == 0)), Times.Once);
    }

    [Fact]
    public void PowerOffWithArgs_UnknownGroup_DoesNotRaiseEvent()
    {
        var handler = new Mock<Action<Dictionary<string, string>>>();
        _manager.OnPowerOffRequested += handler.Object;

        _harness.Hub.PowerOffWithArgs($"missing-{Guid.NewGuid()}", new Dictionary<string, string>());

        handler.Verify(h => h.Invoke(It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Fact]
    public void Command_WhenWorkThrows_IsLoggedAndDoesNotCrash()
    {
        // A device/subscriber fault must not vanish on the dropped Task (M1): the
        // fire-and-forget dispatch captures it as a backend Error log.
        var captured = new ConcurrentQueue<CapturedLog>();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = new CapturingLoggerFactory(captured);
        try
        {
            _manager.OnPowerOnRequested += _ => throw new InvalidOperationException("device fault");

            var exception = Record.Exception(() =>
                _harness.Hub.PowerOnWithArgs(_groupName, new Dictionary<string, string>()));

            Assert.Null(exception); // fire-and-forget: nothing surfaces to the caller
            WaitFor(() => captured.Any(e =>
                e.Level == LogLevel.Error &&
                e.Exception is InvalidOperationException &&
                e.Message.Contains(_groupName)));
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }

    private sealed record CapturedLog(LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLoggerFactory(ConcurrentQueue<CapturedLog> events) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(events);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<CapturedLog> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                events.Enqueue(new CapturedLog(logLevel, exception, formatter(state, exception)));
        }
    }

    private static void WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        Assert.True(SpinWait.SpinUntil(predicate, timeoutMs),
            $"Condition not met within {timeoutMs}ms");
    }
}
