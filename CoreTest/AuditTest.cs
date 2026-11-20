using Microsoft.Extensions.Time.Testing;

namespace AVCoders.Core.Tests;

public class AuditTest
{
    private static readonly Actor Browser = new(ActorKind.Browser, "10.1.2.3", "Firefox", "LAPTOP-12", "circuit-1");

    private static string UniqueTarget() => $"AuditTest-{Guid.NewGuid()}";

    [Fact]
    public void Record_CapturesTheCurrentActor()
    {
        var target = UniqueTarget();
        Actor.Current = Browser;

        Audit.Record(target, "Power", "On");

        var entry = Assert.Single(Audit.GetEntries(), e => e.Target == target);
        Assert.Equal(Browser, entry.Actor);
        Assert.Equal("Power", entry.Action);
        Assert.Equal("On", entry.Detail);
    }

    [Fact]
    public void Record_WithoutAnActor_RecordsNoActor()
    {
        var target = UniqueTarget();
        Actor.Current = null;

        Audit.Record(target, "Clear logs");

        var entry = Assert.Single(Audit.GetEntries(), e => e.Target == target);
        Assert.Null(entry.Actor);
        Assert.Equal(string.Empty, entry.Detail);
    }

    [Fact]
    public void Record_CapturesTheCurrentTriggerId()
    {
        var target = UniqueTarget();
        var triggerId = Trigger.Start();

        Audit.Record(target, "Power", "On");

        var entry = Assert.Single(Audit.GetEntries(), e => e.Target == target);
        Assert.Equal(triggerId, entry.TriggerId);
    }

    [Fact]
    public void Record_WithoutATrigger_RecordsNoTriggerIdAndStartsNone()
    {
        var target = UniqueTarget();

        Audit.Record(target, "Power", "Off");

        var entry = Assert.Single(Audit.GetEntries(), e => e.Target == target);
        Assert.Null(entry.TriggerId);
        Assert.Null(Trigger.Id);
    }

    [Fact]
    public void Record_PastTheLimit_DropsTheOldest()
    {
        const int extra = 5;
        var prefix = UniqueTarget();

        for (var i = 0; i < Audit.EntryLimit + extra; i++)
            Audit.Record($"{prefix}-{i}", "Route");

        var targets = Audit.GetEntries().Select(e => e.Target).ToList();
        Assert.Equal(Audit.EntryLimit, targets.Count);
        for (var i = 0; i < extra; i++)
            Assert.DoesNotContain($"{prefix}-{i}", targets);
        Assert.Contains($"{prefix}-{Audit.EntryLimit + extra - 1}", targets);
    }

    [Fact]
    public void GetEntries_ReturnsASnapshot()
    {
        var target = UniqueTarget();
        var before = Audit.GetEntries();

        Audit.Record(target, "Mute");

        Assert.DoesNotContain(before, e => e.Target == target);
        Assert.Contains(Audit.GetEntries(), e => e.Target == target);
    }

    [Fact]
    public void RecordOutcome_WithTheEntrysTriggerId_AddsTheOutcomeToTheEntry()
    {
        var target = UniqueTarget();
        var triggerId = Trigger.Start();
        Audit.Record(target, "Power", "On");

        Audit.RecordOutcome(triggerId, AuditOutcomeKind.NoReply, "Projector", "No reply to PWR ON");

        var outcome = Assert.Single(Assert.Single(Audit.GetEntries(), e => e.Target == target).Outcomes);
        Assert.Equal((AuditOutcomeKind.NoReply, "Projector", "No reply to PWR ON"), (outcome.Kind, outcome.Source, outcome.Detail));
    }

    [Fact]
    public void RecordOutcome_ShowsControlCharactersInTheDetail()
    {
        var target = UniqueTarget();
        var triggerId = Trigger.Start();
        Audit.Record(target, "Power", "On");

        Audit.RecordOutcome(triggerId, AuditOutcomeKind.Rejected, "Projector", "PWR ON\r\n answered ERR\u0007");

        Assert.Equal(@"PWR ON\x0D\x0A answered ERR\x07", Assert.Single(Assert.Single(Audit.GetEntries(), e => e.Target == target).Outcomes).Detail);
    }

    [Fact]
    public void RecordOutcome_KeepsTheEntrysOtherFields()
    {
        var target = UniqueTarget();
        Actor.Current = Browser;
        var triggerId = Trigger.Start();
        Audit.Record(target, "Power", "On");
        var recorded = Assert.Single(Audit.GetEntries(), e => e.Target == target);

        Audit.RecordOutcome(triggerId, AuditOutcomeKind.Rejected, "Projector", "PWR ON answered ERR");

        var updated = Assert.Single(Audit.GetEntries(), e => e.Target == target);
        Assert.Equal((recorded.Timestamp, recorded.Actor, recorded.TriggerId, recorded.Action, recorded.Detail),
            (updated.Timestamp, updated.Actor, updated.TriggerId, updated.Action, updated.Detail));
    }

    [Fact]
    public void RecordOutcome_RaisesUpdatedWithTheUpdatedEntry()
    {
        var target = UniqueTarget();
        var triggerId = Trigger.Start();
        Audit.Record(target, "Mute");
        AuditEntry? updated = null;
        AuditEntryHandler handler = entry =>
        {
            if (entry.Target == target)
                updated = entry;
        };
        Audit.Updated += handler;
        try
        {
            Audit.RecordOutcome(triggerId, AuditOutcomeKind.Dropped, "Dsp comms", "Cleared from the send queue by a disconnect request: mute");

            Assert.Equal(Assert.Single(Audit.GetEntries(), e => e.Target == target), updated);
        }
        finally
        {
            Audit.Updated -= handler;
        }
    }

    [Fact]
    public void RecordOutcome_ForATriggerWithNoEntry_RaisesNothing()
    {
        var triggerId = Trigger.Start();
        var raised = false;
        AuditEntryHandler handler = entry =>
        {
            if (entry.TriggerId == triggerId)
                raised = true;
        };
        Audit.Updated += handler;
        try
        {
            Audit.RecordOutcome(triggerId, AuditOutcomeKind.NoReply, "Projector", "No reply to a poll");

            Assert.False(raised);
        }
        finally
        {
            Audit.Updated -= handler;
        }
    }

    [Fact]
    public void RecordOutcome_WithNoTriggerId_LeavesEntriesWithNoTriggerIdAlone()
    {
        var target = UniqueTarget();
        Audit.Record(target, "Power", "Off");

        Audit.RecordOutcome(null, AuditOutcomeKind.NoReply, "Projector", "No reply to PWR OFF");

        Assert.Empty(Assert.Single(Audit.GetEntries(), e => e.Target == target).Outcomes);
    }

    [Fact]
    public void RecordOutcome_ForEveryEntryOfTheTrigger_AddsItToEach()
    {
        var target = UniqueTarget();
        var triggerId = Trigger.Start();
        Audit.Record(target, "Shut down");
        Audit.Record(target, "Power off");

        Audit.RecordOutcome(triggerId, AuditOutcomeKind.NoReply, "Projector", "No reply to PWR OFF");

        Assert.All(Audit.GetEntries().Where(e => e.Target == target), entry => Assert.Single(entry.Outcomes));
    }

    [Fact]
    public void RecordOutcome_WithinTheWindow_AddsTheOutcome()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Audit.Clock = clock;
        try
        {
            var target = UniqueTarget();
            var triggerId = Trigger.Start();
            Audit.Record(target, "Recall preset", "3");
            clock.Advance(Audit.OutcomeWindow - TimeSpan.FromSeconds(1));

            Audit.RecordOutcome(triggerId, AuditOutcomeKind.NoReply, "Lectern camera", "No completion for 81-01-04-3F-02-03-FF");

            Assert.Single(Assert.Single(Audit.GetEntries(), e => e.Target == target).Outcomes);
        }
        finally
        {
            Audit.Clock = TimeProvider.System;
        }
    }

    [Fact]
    public void RecordOutcome_ForAnEntryOlderThanTheWindow_LeavesItAlone()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Audit.Clock = clock;
        try
        {
            var target = UniqueTarget();
            var triggerId = Trigger.Start();
            Audit.Record(target, "Volume", "50");
            clock.Advance(Audit.OutcomeWindow + TimeSpan.FromSeconds(1));

            Audit.RecordOutcome(triggerId, AuditOutcomeKind.NoReply, "Projector", "No reply to a poll");

            Assert.Empty(Assert.Single(Audit.GetEntries(), e => e.Target == target).Outcomes);
        }
        finally
        {
            Audit.Clock = TimeProvider.System;
        }
    }

    [Fact]
    public void Record_ByAPerson_LogsTheClientFieldsWithNoEmptyName()
    {
        var target = UniqueTarget();
        var logs = new ScopeCapturingLoggerFactory();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            Actor.Current = Browser;
            var triggerId = Trigger.Start();

            Audit.Record(target, "Power", "On");

            Assert.Contains(logs.Logs, log => log.Message == $"{target}: Power On by Browser 10.1.2.3 LAPTOP-12 Firefox circuit-1 {triggerId}");
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }

    [Fact]
    public void Record_ByASchedule_LogsItsName()
    {
        var target = UniqueTarget();
        var logs = new ScopeCapturingLoggerFactory();
        var original = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            Actor.Current = Actor.Schedule("DailyShutdown");
            var triggerId = Trigger.Start();

            Audit.Record(target, "Shut down");

            Assert.Contains(logs.Logs, log => log.Message == $"{target}: Shut down  by Schedule DailyShutdown {triggerId}");
        }
        finally
        {
            LogBase.LoggerFactory = original;
        }
    }

    [Fact]
    public void RecordOutcome_PastTheLimit_KeepsTheLatest()
    {
        const int extra = 3;
        var target = UniqueTarget();
        var triggerId = Trigger.Start();
        Audit.Record(target, "Route");

        for (var i = 0; i < Audit.OutcomeLimit + extra; i++)
            Audit.RecordOutcome(triggerId, AuditOutcomeKind.Dropped, "Switcher comms", $"drop {i}");

        var outcomes = Assert.Single(Audit.GetEntries(), e => e.Target == target).Outcomes;
        Assert.Equal(Enumerable.Range(extra, Audit.OutcomeLimit).Select(i => $"drop {i}"), outcomes.Select(outcome => outcome.Detail));
    }

    [Fact]
    public void RecordOutcome_AThrowingSubscriber_DoesNotStopTheNextOrEscape()
    {
        var target = UniqueTarget();
        var triggerId = Trigger.Start();
        Audit.Record(target, "Volume", "50");
        AuditEntry? received = null;
        AuditEntryHandler throwing = _ => throw new InvalidOperationException("Subscriber failed");
        AuditEntryHandler next = entry =>
        {
            if (entry.Target == target)
                received = entry;
        };
        Audit.Updated += throwing;
        Audit.Updated += next;
        try
        {
            var exception = Record.Exception(() => Audit.RecordOutcome(triggerId, AuditOutcomeKind.NoReply, "Dsp", "No reply"));

            Assert.Null(exception);
            Assert.NotNull(received);
        }
        finally
        {
            Audit.Updated -= throwing;
            Audit.Updated -= next;
        }
    }

    [Fact]
    public void Record_AThrowingSubscriber_DoesNotStopTheNextOrEscape()
    {
        var target = UniqueTarget();
        AuditEntry? received = null;
        AuditEntryHandler throwing = _ => throw new InvalidOperationException("Subscriber failed");
        AuditEntryHandler next = entry =>
        {
            if (entry.Target == target)
                received = entry;
        };
        Audit.Recorded += throwing;
        Audit.Recorded += next;
        try
        {
            var exception = Record.Exception(() => Audit.Record(target, "Volume", "50"));

            Assert.Null(exception);
            Assert.NotNull(received);
        }
        finally
        {
            Audit.Recorded -= throwing;
            Audit.Recorded -= next;
        }
    }
}
