namespace AVCoders.Core.Tests;

// Same collection as LogBaseRegistryTest — see the note there.
[Collection("LogBaseIssues")]
public class IssuesTest
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public void Momentary(string message, string? key = null,
            IssueSeverity severity = IssueSeverity.Minor, int? escalateAfter = null) =>
            RaiseMomentaryIssue(message, key, severity, escalateAfter);

        public void Ongoing(string key, string message, IssueSeverity severity = IssueSeverity.Major) =>
            RaiseOngoingIssue(key, message, severity);

        public void Resolve(string key) => ResolveIssue(key);
    }

    private readonly TestLogBase _logBase = new("Test");

    [Fact]
    public void RaiseOngoingIssue_AddsEntry_AndFiresChanged()
    {
        IssuesChangedEventArgs? reported = null;
        _logBase.IssuesChanged += (_, e) => reported = e;

        _logBase.Ongoing("input", "Wrong input");

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal("input", issue.Key);
        Assert.Equal("Wrong input", issue.Message);
        Assert.Equal(IssueStatus.Ongoing, issue.Status);
        Assert.Equal(IssueSeverity.Major, issue.Severity);
        Assert.Null(issue.ResolvedAt);
        Assert.Single(_logBase.OngoingIssues, i => i.Key == "input");
        Assert.NotNull(reported);
        Assert.Equal(IssueChangeKind.Raised, reported!.Kind);
        Assert.Equal(issue, reported.ChangedIssue);
        Assert.Single(reported.Issues, i => i.Key == "input");
    }

    [Fact]
    public void RaiseOngoingIssue_WithSameKeyAndMessage_IsANoOp()
    {
        var changedCount = 0;
        _logBase.IssuesChanged += (_, _) => changedCount++;

        _logBase.Ongoing("input", "Wrong input");
        _logBase.Ongoing("input", "Wrong input");

        Assert.Single(_logBase.Issues);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void RaiseOngoingIssue_WithNewMessage_UpdatesEntry_KeepsIdAndRaisedAt_AndFiresAgain()
    {
        var changedCount = 0;
        IssuesChangedEventArgs? lastArgs = null;
        _logBase.IssuesChanged += (_, e) => { changedCount++; lastArgs = e; };

        _logBase.Ongoing("input", "Wrong input");
        var original = Assert.Single(_logBase.Issues);
        _logBase.Ongoing("input", "Still the wrong input");

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal("Still the wrong input", issue.Message);
        Assert.Equal(IssueStatus.Ongoing, issue.Status);
        Assert.Equal(original.Id, issue.Id);
        Assert.Equal(original.RaisedAt, issue.RaisedAt);
        Assert.Equal(2, changedCount);
        Assert.Equal(IssueChangeKind.Updated, lastArgs!.Kind);
    }

    [Fact]
    public void RaiseOngoingIssue_WithNewSeverity_UpdatesEntry_AndFiresAgain()
    {
        _logBase.Ongoing("input", "Wrong input");
        _logBase.Ongoing("input", "Wrong input", IssueSeverity.Critical);

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
    }

    [Fact]
    public void ResolveIssue_KeepsEntryWithResolvedStatus_AndFiresChanged()
    {
        IssuesChangedEventArgs? lastArgs = null;
        _logBase.IssuesChanged += (_, e) => lastArgs = e;

        _logBase.Ongoing("input", "Wrong input");
        var raised = Assert.Single(_logBase.Issues);
        _logBase.Resolve("input");

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal(IssueStatus.Resolved, issue.Status);
        Assert.NotNull(issue.ResolvedAt);
        Assert.Equal(raised.Id, issue.Id);
        Assert.Empty(_logBase.OngoingIssues);
        Assert.Equal(IssueChangeKind.Resolved, lastArgs!.Kind);
        Assert.Equal(issue, lastArgs.ChangedIssue);
    }

    [Fact]
    public void ResolveIssue_WithUnknownKey_DoesNotFire()
    {
        var changedCount = 0;
        _logBase.IssuesChanged += (_, _) => changedCount++;

        _logBase.Resolve("input");

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void ResolveIssue_WithAlreadyResolvedKey_DoesNotFireAgain()
    {
        _logBase.Ongoing("input", "Wrong input");
        _logBase.Resolve("input");

        var changedCount = 0;
        _logBase.IssuesChanged += (_, _) => changedCount++;
        _logBase.Resolve("input");

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void ResolveIssue_DoesNotTouchMomentaryEntries()
    {
        _logBase.Momentary("Missed a poll", key: "poll");

        _logBase.Resolve("poll");

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Null(issue.ResolvedAt);
    }

    [Fact]
    public void RaiseOngoingIssue_AfterResolve_CreatesANewEntry()
    {
        _logBase.Ongoing("input", "Wrong input");
        var first = Assert.Single(_logBase.Issues);
        _logBase.Resolve("input");

        _logBase.Ongoing("input", "Wrong input again");

        Assert.Equal(2, _logBase.Issues.Count);
        Assert.Single(_logBase.Issues, i => i.Status == IssueStatus.Resolved);
        var second = Assert.Single(_logBase.Issues, i => i.Status == IssueStatus.Ongoing);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Single(_logBase.OngoingIssues);
    }

    [Fact]
    public void RaiseMomentaryIssue_IsInstantlyHistorical_AndNeverOngoing()
    {
        _logBase.Momentary("Missed a poll");

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal(IssueStatus.Momentary, issue.Status);
        Assert.Equal(IssueSeverity.Minor, issue.Severity);
        Assert.Equal("Missed a poll", issue.Key);
        Assert.Equal(1, issue.OccurrenceCount);
        Assert.Empty(_logBase.OngoingIssues);
    }

    [Fact]
    public void RaiseMomentaryIssue_RepeatedSameKey_CoalescesIntoOneEntry()
    {
        IssuesChangedEventArgs? lastArgs = null;
        _logBase.IssuesChanged += (_, e) => lastArgs = e;

        _logBase.Momentary("Query A was not answered", key: "poll");
        var first = Assert.Single(_logBase.Issues);
        _logBase.Momentary("Query B was not answered", key: "poll");
        _logBase.Momentary("Query C was not answered", key: "poll");

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal(3, issue.OccurrenceCount);
        Assert.Equal("Query C was not answered", issue.Message);
        Assert.Equal(first.Id, issue.Id);
        Assert.Equal(first.RaisedAt, issue.RaisedAt);
        Assert.True(issue.LastRaisedAt >= first.LastRaisedAt);
        Assert.Equal(IssueChangeKind.Updated, lastArgs!.Kind);
    }

    [Fact]
    public void RaiseMomentaryIssue_AfterADifferentKeyIntervenes_StillCoalescesByKey()
    {
        _logBase.Momentary("Missed a poll", key: "poll");
        _logBase.Momentary("Something else", key: "other");
        _logBase.Momentary("Missed a poll", key: "poll");

        Assert.Equal(2, _logBase.Issues.Count);
        var poll = Assert.Single(_logBase.Issues, i => i.Key == "poll");
        Assert.Equal(2, poll.OccurrenceCount);
    }

    [Fact]
    public void RaisingAnIssue_AddsOneEventEntry_AndIdempotentRaisesAddNone()
    {
        var eventCountBefore = _logBase.Events.Count;

        _logBase.Ongoing("input", "Wrong input");
        _logBase.Ongoing("input", "Wrong input");
        _logBase.Momentary("Missed a poll", key: "poll");
        _logBase.Momentary("Missed a poll", key: "poll");

        Assert.Equal(eventCountBefore + 2, _logBase.Events.Count);
    }

    [Fact]
    public void Issues_AreCappedAt50ByDefault()
    {
        for (var i = 0; i < 60; i++)
            _logBase.Momentary($"error {i}");

        Assert.Equal(50, _logBase.Issues.Count);
    }

    [Fact]
    public void LimitIssues_EvictsHistoricalEntriesBeforeOngoing()
    {
        _logBase.SetIssueLimit(2);
        _logBase.Momentary("Missed a poll", key: "poll");
        _logBase.Ongoing("communication", "Comms error");
        _logBase.Ongoing("input", "Wrong input");

        Assert.Equal(new[] { "communication", "input" }, _logBase.Issues.Select(i => i.Key));
    }

    [Fact]
    public void LimitIssues_NeverEvictsTheIssueJustRaised()
    {
        _logBase.SetIssueLimit(2);
        _logBase.Ongoing("communication", "Comms error");
        _logBase.Ongoing("input", "Wrong input");
        _logBase.Momentary("Missed a poll", key: "poll");

        Assert.Contains(_logBase.Issues, i => i.Key == "poll");
        Assert.Equal(2, _logBase.Issues.Count);
    }

    [Fact]
    public void SetIssueLimit_TrimsExistingEntries_AndFiresChanged()
    {
        _logBase.Momentary("Missed a poll", key: "poll");
        _logBase.Ongoing("input", "Wrong input");

        IssuesChangedEventArgs? lastArgs = null;
        _logBase.IssuesChanged += (_, e) => lastArgs = e;
        _logBase.SetIssueLimit(1);

        var issue = Assert.Single(_logBase.Issues);
        Assert.Equal("input", issue.Key);
        Assert.NotNull(lastArgs);
        Assert.Equal(IssueChangeKind.Trimmed, lastArgs!.Kind);
        Assert.Null(lastArgs.ChangedIssue);
    }

    [Fact]
    public void IssuesChanged_SubscriberThrowing_DoesNotPropagate_AndOthersStillRun()
    {
        _logBase.IssuesChanged += (_, _) => throw new InvalidOperationException("Bad subscriber");
        var secondSubscriberRan = false;
        _logBase.IssuesChanged += (_, _) => secondSubscriberRan = true;

        _logBase.Ongoing("input", "Wrong input");

        Assert.True(secondSubscriberRan);
        Assert.Contains(_logBase.Errors, e => e.Exception is InvalidOperationException);
    }

    [Fact]
    public void Issues_AreOrderedByRaiseOrder()
    {
        _logBase.Ongoing("first", "First issue");
        _logBase.Ongoing("second", "Second issue");

        Assert.Equal(new[] { "first", "second" }, _logBase.Issues.Select(i => i.Key));
    }

    [Fact]
    public void RaiseMomentaryIssue_BelowEscalationThreshold_DoesNotRaiseOngoing()
    {
        _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);
        _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);

        Assert.Empty(_logBase.OngoingIssues);
    }

    [Fact]
    public void RaiseMomentaryIssue_AtEscalationThreshold_RaisesAnOngoingIssue()
    {
        IssuesChangedEventArgs? lastArgs = null;
        _logBase.IssuesChanged += (_, e) => lastArgs = e;

        for (var i = 0; i < 3; i++)
            _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);

        var ongoing = Assert.Single(_logBase.OngoingIssues);
        Assert.Equal("poll", ongoing.Key);
        Assert.Equal("Missed a poll (3 consecutive occurrences)", ongoing.Message);
        Assert.Equal(IssueSeverity.Major, ongoing.Severity);
        Assert.Equal(IssueChangeKind.Raised, lastArgs!.Kind);
        Assert.Equal(ongoing, lastArgs.ChangedIssue);
        Assert.Equal(2, _logBase.Issues.Count);
    }

    [Fact]
    public void RaiseMomentaryIssue_BeyondEscalationThreshold_DoesNotRaiseASecondOngoingIssue()
    {
        for (var i = 0; i < 5; i++)
            _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);

        Assert.Single(_logBase.OngoingIssues);
    }

    [Fact]
    public void ResolveIssue_ResetsTheConsecutiveCount()
    {
        _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);
        _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);
        _logBase.Resolve("poll");
        _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);
        _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);

        Assert.Empty(_logBase.OngoingIssues);
    }

    [Fact]
    public void Escalation_AfterResolve_CreatesANewOngoingIssue()
    {
        for (var i = 0; i < 3; i++)
            _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);
        var first = Assert.Single(_logBase.OngoingIssues);
        _logBase.Resolve("poll");

        for (var i = 0; i < 3; i++)
            _logBase.Momentary("Missed a poll", key: "poll", escalateAfter: 3);

        var second = Assert.Single(_logBase.OngoingIssues);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Single(_logBase.Issues, i => i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void Escalation_BumpsSeverityOneLevel_CappedAtCritical()
    {
        for (var i = 0; i < 3; i++)
            _logBase.Momentary("Minor blip", key: "minor", escalateAfter: 3);
        for (var i = 0; i < 3; i++)
            _logBase.Momentary("Critical blip", key: "critical", severity: IssueSeverity.Critical, escalateAfter: 3);

        Assert.Equal(IssueSeverity.Major,
            Assert.Single(_logBase.OngoingIssues, i => i.Key == "minor").Severity);
        Assert.Equal(IssueSeverity.Critical,
            Assert.Single(_logBase.OngoingIssues, i => i.Key == "critical").Severity);
    }

    [Fact]
    public void ConsecutiveMomentaryTracking_IsCappedAt200Keys()
    {
        _logBase.Momentary("A failed", key: "a", escalateAfter: 3);
        _logBase.Momentary("A failed", key: "a", escalateAfter: 3);
        // 200 distinct keys evict "a" (the oldest entry) from the consecutive-count dictionary.
        for (var i = 0; i < 200; i++)
            _logBase.Momentary($"flood {i}", key: $"flood-{i}");

        // Without the eviction this third raise would reach the threshold and escalate.
        _logBase.Momentary("A failed", key: "a", escalateAfter: 3);

        Assert.Empty(_logBase.OngoingIssues);
    }

    [Fact]
    public void ConsecutiveMomentaryTracking_StillEscalates_ForKeysRaisedAfterTheCapWasHit()
    {
        for (var i = 0; i < 250; i++)
            _logBase.Momentary($"flood {i}", key: $"flood-{i}");

        for (var i = 0; i < 3; i++)
            _logBase.Momentary("B failed", key: "b", escalateAfter: 3);

        Assert.Single(_logBase.OngoingIssues, i => i.Key == "b");
    }

    [Fact]
    public void RaiseMomentaryIssue_WithoutEscalateAfter_NeverEscalates()
    {
        for (var i = 0; i < 10; i++)
            _logBase.Momentary("Missed a poll", key: "poll");

        Assert.Empty(_logBase.OngoingIssues);
    }

    [Fact]
    public void TicketScenario_RaiseThenResolve_ReportsOneRaisedAndOneResolvedWithMatchingId()
    {
        var changes = new List<(IssueChangeKind Kind, Issue? Issue)>();
        _logBase.IssuesChanged += (_, e) => changes.Add((e.Kind, e.ChangedIssue));

        _logBase.Ongoing("communication", "Comms error");
        _logBase.Resolve("communication");

        var raised = Assert.Single(changes, c => c.Kind == IssueChangeKind.Raised);
        var resolved = Assert.Single(changes, c => c.Kind == IssueChangeKind.Resolved);
        Assert.Equal(raised.Issue!.Id, resolved.Issue!.Id);
    }
}
