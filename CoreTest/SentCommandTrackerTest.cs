using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Core.Tests;

public class SentCommandTrackerTest
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SaveErrorWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NecReplyTimeout = TimeSpan.FromMilliseconds(1500);

    private static readonly SisCommand VerboseMode = new("\x1b3CV\r", "Vrb");
    private static readonly SisCommand Model = new("1I", "Inf01*");
    private static readonly SisCommand SignalStatus = new("\x1bLS\r", "Sig");
    private static readonly SisCommand InputHdcp = new("\x1bIHDCP\r", "HdcpI");
    private static readonly SisCommand OutputHdcp = new("\x1bOHDCP\r", "HdcpO");
    private static readonly SisCommand Tie = new("!", "In");
    private static readonly SisCommand Firmware = new("Q", "Ver01*");
    private static readonly SisCommand PartNumber = new("N", "Pno");
    private static readonly SisCommand UnitName = new("\x1bCN\r", "Ipn ");
    private static readonly SisCommand Mac = new("\x1bCH\r", "Iph ");
    private static readonly SisCommand Save = new("W9MF|", "Ims");
    private static readonly SisCommand DrawingTool = new("\x1bDRAW\r", "Draw");

    private readonly FakeTimeProvider _time = new();
    private readonly SentCommandTracker<SisCommand> _sis;

    public SentCommandTrackerTest()
    {
        _sis = new SentCommandTracker<SisCommand>(ReplyTimeout, _time);
    }

    private sealed record SisCommand(string Text, string ReplyTag);

    private sealed record TunnelFrame(string Address, string Text);

    private sealed record NecCommand(byte[] Bytes, string ReplyOpCode);

    private SentCommand<SisCommand>? Reply(string line) => _sis.TakeReply(sent => line.StartsWith(sent.ReplyTag));

    private void Send(SisCommand command, string triggerId, TimeSpan lateErrorWindow = default)
    {
        using (Trigger.Resume(triggerId))
            _sis.Add(command, lateErrorWindow);
    }

    [Fact]
    public void Replies_InSendOrder_EachMatchTheirCommandAndItsTrigger()
    {
        foreach (var command in new[] { VerboseMode, Model, SignalStatus, InputHdcp, OutputHdcp, Tie })
            Send(command, "84c08518");
        foreach (var command in new[] { Firmware, PartNumber, UnitName, Mac })
            Send(command, "03bde479");

        Assert.Null(Reply("(c) Copyright 2024, Extron Electronics, SW2 HD 4K PLUS, V1.03, 60-1603-01"));
        Assert.Null(Reply("Sun, 01 Nov 2026 10:50:35"));
        Assert.Equal(new SentCommand<SisCommand>(VerboseMode, "84c08518"), Reply("Vrb3"));
        Assert.Equal(new SentCommand<SisCommand>(Model, "84c08518"), Reply("Inf01*SW2 HD 4K PLUS"));
        Assert.Equal(new SentCommand<SisCommand>(SignalStatus, "84c08518"), Reply("Sig\"0 1*0\""));
        Assert.Equal(new SentCommand<SisCommand>(InputHdcp, "84c08518"), Reply("HdcpI0 2 "));
        Assert.Equal(new SentCommand<SisCommand>(OutputHdcp, "84c08518"), Reply("HdcpO0"));
        Assert.Equal(new SentCommand<SisCommand>(Tie, "84c08518"), Reply("In1 All"));
        Assert.Equal(new SentCommand<SisCommand>(Firmware, "03bde479"), Reply("Ver01*1.03"));
        Assert.Equal(new SentCommand<SisCommand>(PartNumber, "03bde479"), Reply("Pno60-1603-01"));
        Assert.Equal(new SentCommand<SisCommand>(UnitName, "03bde479"), Reply("Ipn ROOM-VID-SWI01"));
        Assert.Equal(new SentCommand<SisCommand>(Mac, "03bde479"), Reply("Iph 00-05-A6-00-00-01"));
        Assert.Null(_sis.TakeError());
    }

    [Fact]
    public void Reply_OfAnotherKind_LeavesTheOlderCommandAwaitingItsReply()
    {
        Send(Firmware, "03bde479");
        Send(Tie, "1a27650b");

        Assert.Equal(Tie, Reply("In1 All")?.Command);
        Assert.True(_sis.IsAwaitingReply(sent => sent == Firmware));
        Assert.Equal(Firmware, Reply("Ver01*1.03")?.Command);
    }

    [Fact]
    public void Replies_OfOneKind_GoToTheOldestCommandOfThatKind()
    {
        Send(Tie, "1a27650b");
        Send(Tie, "2e3f0ae1");

        Assert.Equal("1a27650b", Reply("In1 All")?.TriggerId);
        Assert.Equal("2e3f0ae1", Reply("In1 All")?.TriggerId);
    }

    [Fact]
    public void UnsolicitedReply_ConsumesNoCommand()
    {
        Send(SignalStatus, "1a27650b");

        Assert.Null(Reply("In2 All"));
        Assert.Equal(SignalStatus, Reply("Sig\"0 1*0\"")?.Command);
    }

    [Fact]
    public void BareError_GoesToTheOldestCommandAwaitingAReply()
    {
        Send(Firmware, "03bde479");
        Send(PartNumber, "03bde479");
        Send(UnitName, "03bde479");

        Assert.Equal(Firmware, _sis.TakeError()?.Command);
        Assert.Equal(PartNumber, Reply("Pno60-1603-01")?.Command);
        Assert.Equal(UnitName, _sis.TakeError()?.Command);
    }

    [Fact]
    public void BareError_WithNothingOutstanding_IsUnmatched()
    {
        Assert.Null(_sis.TakeError());
    }

    [Fact]
    public void BareError_GoesToTheOldestCommandThatCanReceiveIt()
    {
        var tunnels = new SentCommandTracker<TunnelFrame>(ReplyTimeout, _time);
        var online = new TunnelFrame("192.0.2.26", "{192.0.2.26:\x1bDNUM\r}\r");
        var offline = new TunnelFrame("192.0.2.20", "{192.0.2.20:\x1bDNUM\r}\r");
        HashSet<string> offlineAddresses = ["192.0.2.20"];
        tunnels.Add(online);
        tunnels.Add(offline);

        Assert.Equal(offline, tunnels.TakeError(sent => offlineAddresses.Contains(sent.Address))?.Command);
        Assert.Null(tunnels.TakeError(sent => offlineAddresses.Contains(sent.Address)));
        Assert.Equal(online, tunnels.TakeReply(sent => "{192.0.2.26}1539".StartsWith($"{{{sent.Address}}}"))?.Command);
    }

    [Fact]
    public void LateError_AfterLaterRepliesGoesToTheCommandThatAllowsIt()
    {
        Send(Save, "5d30414a", SaveErrorWindow);
        Assert.Equal(Save, Reply("Ims9")?.Command);
        Send(DrawingTool, "07a0db8e");
        Assert.Equal(DrawingTool, Reply("Draw02")?.Command);

        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(new SentCommand<SisCommand>(Save, "5d30414a"), _sis.TakeError());
        Assert.Null(_sis.TakeError());
    }

    [Fact]
    public void BareError_WhileALaterCommandAwaitsItsReply_GoesToThatCommandBeforeTheLateErrorWindow()
    {
        Send(Save, "5d30414a", SaveErrorWindow);
        Reply("Ims9");
        Send(DrawingTool, "07a0db8e");

        Assert.Equal(DrawingTool, _sis.TakeError()?.Command);
        Assert.Equal(Save, _sis.TakeError()?.Command);
    }

    [Fact]
    public void CommandInItsLateErrorWindow_TakesNoSecondReply()
    {
        Send(Save, "5d30414a", SaveErrorWindow);
        Reply("Ims9");

        Assert.Null(Reply("Ims9"));
        Assert.False(_sis.IsAwaitingReply(sent => sent == Save));
    }

    [Fact]
    public void LateErrorWindow_ClosesWithoutReportingAMiss()
    {
        Send(Save, "5d30414a", SaveErrorWindow);
        Reply("Ims9");

        _time.Advance(SaveErrorWindow);

        Assert.Null(_sis.TakeError());
        Assert.Empty(_sis.TakeMissed());
    }

    [Fact]
    public void UnansweredCommand_IsReportedMissedOnceItsTimeoutPasses()
    {
        Send(Firmware, "03bde479");

        _time.Advance(ReplyTimeout - TimeSpan.FromTicks(1));
        Assert.Empty(_sis.TakeMissed());
        Assert.True(_sis.IsAwaitingReply(sent => sent == Firmware));

        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(new[] { new SentCommand<SisCommand>(Firmware, "03bde479") }, _sis.TakeMissed());
        Assert.Empty(_sis.TakeMissed());
    }

    [Fact]
    public void MissedCommand_TakesNoReplyOrError()
    {
        Send(Firmware, "03bde479");
        _time.Advance(ReplyTimeout);
        Send(PartNumber, "1a27650b");

        Assert.False(_sis.IsAwaitingReply(sent => sent == Firmware));
        Assert.Null(Reply("Ver01*1.03"));
        Assert.Equal(PartNumber, _sis.TakeError()?.Command);
        Assert.Equal(new[] { Firmware }, _sis.TakeMissed().Select(missed => missed.Command));
    }

    [Fact]
    public void AnotherControllersReplies_AreIgnored()
    {
        var nec = new SentCommandTracker<NecCommand>(NecReplyTimeout, _time);
        var powerRead = new NecCommand(
            [0x01, 0x30, 0x2A, 0x30, 0x41, 0x30, 0x36, 0x02, 0x30, 0x31, 0x44, 0x36, 0x03, 0x1F, 0x0D], "D6");
        byte[] powerReply =
        [
            0x01, 0x30, 0x30, 0x41, 0x42, 0x31, 0x32, 0x02, 0x30, 0x32, 0x30, 0x30, 0x44, 0x36, 0x30, 0x30, 0x30, 0x30,
            0x30, 0x34, 0x30, 0x30, 0x30, 0x34, 0x03, 0x71, 0x0D
        ];
        SentCommand<NecCommand>? NecReply(byte[] reply) =>
            nec.TakeReply(sent => sent.ReplyOpCode == Encoding.ASCII.GetString(reply, 12, 2));

        Assert.Null(NecReply(powerReply));
        _time.Advance(TimeSpan.FromMilliseconds(2500));
        using (Trigger.Resume("ce2ab667"))
            nec.Add(powerRead);

        Assert.Equal(new SentCommand<NecCommand>(powerRead, "ce2ab667"), NecReply(powerReply));
        Assert.Null(NecReply(powerReply));
        Assert.Null(nec.TakeError());
    }

    [Fact]
    public void Clear_ForgetsEveryCommandWithoutReportingAMiss()
    {
        Send(Firmware, "03bde479");
        Send(Save, "5d30414a", SaveErrorWindow);
        Reply("Ims9");

        _sis.Clear();
        _time.Advance(ReplyTimeout);

        Assert.Null(_sis.TakeError());
        Assert.Empty(_sis.TakeMissed());
    }

    [Fact]
    public void MissedCommand_PrunedByALaterSend_IsStillReportedMissed()
    {
        Send(Firmware, "03bde479");
        _time.Advance(ReplyTimeout);
        Send(PartNumber, "1a27650b");

        Assert.Equal(new[] { new SentCommand<SisCommand>(Firmware, "03bde479") }, _sis.TakeMissed());
        Assert.Equal(PartNumber, Reply("Pno60-1603-01")?.Command);
    }

    [Fact]
    public void Send_OverMaxCommands_DropsTheOldestCommand()
    {
        var tracker = new SentCommandTracker<int>(ReplyTimeout, _time);
        for (var command = 0; command < SentCommandTracker<int>.MaxCommands; command++)
            using (Trigger.Resume("84c08518"))
                Assert.Null(tracker.Add(command));

        Assert.Equal(new SentCommand<int>(0, "84c08518"), tracker.Add(SentCommandTracker<int>.MaxCommands));
        Assert.Equal(1, tracker.TakeError()?.Command);
        Assert.Null(tracker.TakeReply(sent => sent == 0));
    }

    [Fact]
    public void Send_OverMaxCommands_DropsTheOldestMissedCommandFirst()
    {
        var tracker = new SentCommandTracker<int>(ReplyTimeout, _time);
        tracker.Add(-1);
        _time.Advance(ReplyTimeout);
        for (var command = 0; command < SentCommandTracker<int>.MaxCommands - 1; command++)
            Assert.Null(tracker.Add(command));

        Assert.Equal(-1, tracker.Add(SentCommandTracker<int>.MaxCommands - 1)?.Command);
        Assert.Empty(tracker.TakeMissed());
        Assert.Equal(0, tracker.TakeError()?.Command);
    }

    [Fact]
    public void ClosedLateErrorWindow_IsPrunedWithoutCountingTowardMaxCommands()
    {
        var tracker = new SentCommandTracker<int>(ReplyTimeout, _time);
        tracker.Add(-1, SaveErrorWindow);
        tracker.TakeReply(sent => sent == -1);
        _time.Advance(SaveErrorWindow);

        for (var command = 0; command < SentCommandTracker<int>.MaxCommands; command++)
            Assert.Null(tracker.Add(command));
        Assert.Empty(tracker.TakeMissed());
    }

    [Fact]
    public void MissedCommands_NeverTaken_StayWithinMaxCommands()
    {
        var tracker = new SentCommandTracker<int>(ReplyTimeout, _time);
        var dropped = new List<int>();
        const int commandCount = SentCommandTracker<int>.MaxCommands * 3;
        for (var command = 0; command < commandCount; command++)
        {
            if (tracker.Add(command) is { } oldest)
                dropped.Add(oldest.Command);
            _time.Advance(ReplyTimeout);
        }

        var missed = tracker.TakeMissed().Select(sent => sent.Command).ToList();
        Assert.Equal(Enumerable.Range(commandCount - SentCommandTracker<int>.MaxCommands, SentCommandTracker<int>.MaxCommands), missed);
        Assert.Equal(Enumerable.Range(0, commandCount - SentCommandTracker<int>.MaxCommands), dropped);
    }

    [Fact]
    public async Task ConcurrentSendsRepliesAndErrors_TakeEveryCommandExactlyOnce()
    {
        const int commandCount = 20000;
        var workers = Environment.ProcessorCount * 2;
        var tracker = new SentCommandTracker<int>(ReplyTimeout, _time);
        var taken = new ConcurrentBag<int>();

        await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            for (var command = worker; command < commandCount; command += workers)
            {
                if (tracker.Add(command) is { } dropped)
                    taken.Add(dropped.Command);
                var take = command % 2 == 0 ? tracker.TakeReply(sent => sent == command) : tracker.TakeError();
                if (take != null)
                    taken.Add(take.Command);
            }
        })));
        while (tracker.TakeError() is { } remaining)
            taken.Add(remaining.Command);

        Assert.Equal(Enumerable.Range(0, commandCount), taken.Order());
    }
}
