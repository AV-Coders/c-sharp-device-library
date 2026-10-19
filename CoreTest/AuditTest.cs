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
