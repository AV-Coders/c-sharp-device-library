using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusInterfaceTest
{
    private readonly CBusSerialInterface _interface;
    private readonly ManualTimeProvider _time = new();
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly List<string> _sent = [];
    private readonly List<CBusGroupMessage> _groupMessages = [];
    private readonly List<CBusChannelLevel> _channelLevels = [];

    public CBusInterfaceTest()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(_sent.Add);
        _interface = new CBusSerialInterface(_mockClient.Object, _time);
        _interface.GroupMessageHandlers += _groupMessages.Add;
        _interface.ChannelLevelHandlers += _channelLevels.Add;
    }

    private void Receive(string text) => _mockClient.Object.ResponseByteHandlers!.Invoke(Encoding.ASCII.GetBytes(text));

    private async Task SetupPci()
    {
        Task setup = _interface.SetupPci();
        while (!setup.IsCompleted)
            _time.Advance(CBusSerialInterface.SetupStepDelay);
        await setup;
    }

    [Fact]
    public void SendPointToMultipointPayload_SendsUppercaseHexWithConfirmationAndNoChecksum()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);

        Assert.Equal(["\\0538007919r\r"], _sent);
    }

    [Fact]
    public void RequestChannelStatus_SendsThePointToPointStatusRequest()
    {
        _interface.RequestChannelStatus(0x74, 0x01);

        Assert.Equal(["\\0674002A0101r\r"], _sent);
    }

    [Fact]
    public void SecondCommand_WaitsForTheConfirmation()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);

        Assert.Equal(["\\0538007919r\r"], _sent);
        Assert.Equal(1, _interface.QueuedCommands);

        Receive("r.");

        Assert.Equal(["\\0538007919r\r", "\\053800011Ar\r"], _sent);
        Assert.Equal(0, _interface.QueuedCommands);
    }

    [Fact]
    public void Confirmation_WithNothingQueued_LetsTheNextCommandGoStraightOut()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        Receive("r.");
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x19]);

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void MissingConfirmation_SendsTheNextCommandAfterTheTimeout()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x1A]);

        _time.Advance(CBusSerialInterface.ConfirmationTimeout);

        Assert.Equal(["\\0538007919r\r", "\\053800011Ar\r"], _sent);
    }

    [Fact]
    public void FullQueue_IsCleared()
    {
        for (int i = 0; i <= CBusSerialInterface.MaxQueuedCommands + 1; i++)
            _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);

        Assert.Equal(1, _interface.QueuedCommands);
    }

    [Fact]
    public async Task SetupPci_SendsTheSetupStringsInOrder()
    {
        await SetupPci();

        Assert.Equal(["~~~\r", "~~~p\r", "@A330005100p\r", "@A3410051p\r", "@A3420007p\r", "@A32100FF00p\r"], _sent);
    }

    [Fact]
    public async Task SetupPci_ClearsTheQueue()
    {
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x19]);
        _interface.SendPointToMultipointPayload(0x38, [0x01, 0x19]);

        await SetupPci();
        _interface.SendPointToMultipointPayload(0x38, [0x79, 0x1A]);

        Assert.Equal(0, _interface.QueuedCommands);
        Assert.Equal("\\053800791Ar\r", _sent.Last());
    }

    [Fact]
    public void MonitoredOn_RaisesAGroupMessage()
    {
        Receive("0553380079C532\r\n");

        Assert.Equal([new CBusGroupMessage(0x53, 0x38, 0x79, 0xC5, 0xFF)], _groupMessages);
    }

    [Fact]
    public void MonitoredOff_RaisesAGroupMessage()
    {
        Receive("0552380001D898\r\n");

        Assert.Equal([new CBusGroupMessage(0x52, 0x38, 0x01, 0xD8, 0x00)], _groupMessages);
    }

    [Fact]
    public void MonitoredRamp_RaisesTheTargetLevel()
    {
        Receive("05533800121980E5\r\n");

        Assert.Equal([new CBusGroupMessage(0x53, 0x38, 0x12, 0x19, 0x80)], _groupMessages);
    }

    [Fact]
    public void MessageWithTwoCommands_RaisesBoth()
    {
        Receive("05533800791901196A\r\n");

        Assert.Equal(
            [new CBusGroupMessage(0x53, 0x38, 0x79, 0x19, 0xFF), new CBusGroupMessage(0x53, 0x38, 0x01, 0x19, 0x00)],
            _groupMessages);
    }

    [Fact]
    public void MessageWithOnAndRamp_RaisesBoth()
    {
        Receive("0553380079190A1A40AA\r\n");

        Assert.Equal(
            [new CBusGroupMessage(0x53, 0x38, 0x79, 0x19, 0xFF), new CBusGroupMessage(0x53, 0x38, 0x0A, 0x1A, 0x40)],
            _groupMessages);
    }

    [Fact]
    public void MessageSplitAcrossReads_IsJoined()
    {
        Receive("0553380");
        Receive("079C532\r\n");

        Assert.Single(_groupMessages);
    }

    [Fact]
    public void ConfirmationCharacter_DiscardsThePartialLine()
    {
        Receive("05533r.80079C532\r\n");

        Assert.Empty(_groupMessages);
    }

    [Fact]
    public void SetupPci_WaitsBeforeTheNextSetupString()
    {
        _ = _interface.SetupPci();

        Assert.Equal(["~~~\r"], _sent);
    }

    [Fact]
    public void NoiseOfAFullLineWithoutALineFeed_IsDiscarded()
    {
        Receive(new string('0', CBusSerialInterface.MaxLineLength) + "0553380079C532\r\n");

        Assert.Equal([new CBusGroupMessage(0x53, 0x38, 0x79, 0xC5, 0xFF)], _groupMessages);
    }

    [Fact]
    public void StatusReply_RaisesTheChannelLevel()
    {
        Receive("867400008201806A\r\n");

        Assert.Equal([new CBusChannelLevel(0x74, 0x01, 0x80)], _channelLevels);
    }
}

public class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly object _timersLock = new();

    public DateTimeOffset UtcNow = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timersLock)
            _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        var end = UtcNow + duration;
        while (true)
        {
            ManualTimer? next;
            lock (_timersLock)
                next = _timers.Where(t => t.Due != null && t.Due <= end).OrderBy(t => t.Due).FirstOrDefault();
            if (next == null)
                break;
            UtcNow = next.Due!.Value;
            next.Fire();
        }
        UtcNow = end;
    }

    private class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.UtcNow + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan ? null : owner.UtcNow + _period;
            callback(state);
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Due = null;
            return ValueTask.CompletedTask;
        }
    }
}
