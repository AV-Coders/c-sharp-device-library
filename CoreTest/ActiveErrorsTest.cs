namespace AVCoders.Core.Tests;

public class ActiveErrorsTest
{
    private class TestLogBase(string name) : LogBase(name)
    {
        public void Momentary(string message, TimeSpan? ttl = null, string? key = null) =>
            RaiseMomentaryError(message, ttl, key);

        public void Persistent(string key, string message) => RaisePersistentError(key, message);

        public void Clear(string key) => ClearPersistentError(key);
    }

    private readonly TestLogBase _logBase = new("Test");

    [Fact]
    public void RaisePersistentError_AddsEntry_AndFiresChanged()
    {
        IReadOnlyList<ActiveError>? reported = null;
        _logBase.ActiveErrorsChanged += (_, e) => reported = e.ActiveErrors;

        _logBase.Persistent("input", "Wrong input");

        var error = Assert.Single(_logBase.ActiveErrors);
        Assert.Equal("input", error.Key);
        Assert.Equal("Wrong input", error.Message);
        Assert.Equal(ErrorPersistence.Persistent, error.Persistence);
        Assert.Null(error.ExpiresAt);
        Assert.NotNull(reported);
        Assert.Single(reported!, e => e.Key == "input");
    }

    [Fact]
    public void RaisePersistentError_WithSameKeyAndMessage_IsANoOp()
    {
        var changedCount = 0;
        _logBase.ActiveErrorsChanged += (_, _) => changedCount++;

        _logBase.Persistent("input", "Wrong input");
        _logBase.Persistent("input", "Wrong input");

        Assert.Single(_logBase.ActiveErrors);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void RaisePersistentError_WithNewMessage_UpdatesEntry_AndFiresAgain()
    {
        var changedCount = 0;
        _logBase.ActiveErrorsChanged += (_, _) => changedCount++;

        _logBase.Persistent("input", "Input is Hdmi2, should be Hdmi1");
        _logBase.Persistent("input", "Input is Hdmi3, should be Hdmi1");

        var error = Assert.Single(_logBase.ActiveErrors);
        Assert.Equal("Input is Hdmi3, should be Hdmi1", error.Message);
        Assert.Equal(2, changedCount);
    }

    [Fact]
    public void ClearPersistentError_RemovesEntry_AndFiresChanged()
    {
        _logBase.Persistent("input", "Wrong input");
        var changedCount = 0;
        _logBase.ActiveErrorsChanged += (_, _) => changedCount++;

        _logBase.Clear("input");

        Assert.Empty(_logBase.ActiveErrors);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void ClearPersistentError_WithUnknownKey_DoesNotFire()
    {
        var changedCount = 0;
        _logBase.ActiveErrorsChanged += (_, _) => changedCount++;

        _logBase.Clear("nothing");

        Assert.Equal(0, changedCount);
    }

    [Fact]
    public void ClearPersistentError_DoesNotClearMomentaryErrors()
    {
        _logBase.Momentary("A poll was missed", key: "poll");

        _logBase.Clear("poll");

        Assert.Single(_logBase.ActiveErrors);
    }

    [Fact]
    public void RaiseMomentaryError_DefaultsKeyToMessage_AndDedupes()
    {
        _logBase.Momentary("A poll was missed");
        _logBase.Momentary("A poll was missed");

        var error = Assert.Single(_logBase.ActiveErrors);
        Assert.Equal("A poll was missed", error.Key);
        Assert.Equal(ErrorPersistence.Momentary, error.Persistence);
        Assert.NotNull(error.ExpiresAt);
    }

    [Fact]
    public void RaiseMomentaryError_ReRaising_ExtendsExpiry()
    {
        _logBase.Momentary("A poll was missed", TimeSpan.FromSeconds(10), "poll");
        var firstExpiry = _logBase.ActiveErrors[0].ExpiresAt;

        _logBase.Momentary("A poll was missed", TimeSpan.FromSeconds(60), "poll");

        var error = Assert.Single(_logBase.ActiveErrors);
        Assert.True(error.ExpiresAt > firstExpiry);
    }

    [Fact]
    public void RaisingAnError_AddsOneEventEntry_AndDedupedRaisesAddNone()
    {
        _logBase.Persistent("input", "Wrong input");
        _logBase.Persistent("input", "Wrong input");

        Assert.Single(_logBase.Events, e => e.Type == EventType.Error && e.Info == "Wrong input");
    }

    [Fact]
    public async Task MomentaryError_ExpiresAfterTtl_AndFiresChanged()
    {
        var expired = new TaskCompletionSource();
        _logBase.ActiveErrorsChanged += (_, e) =>
        {
            if (e.ActiveErrors.Count == 0)
                expired.TrySetResult();
        };

        _logBase.Momentary("A poll was missed", TimeSpan.FromMilliseconds(100));

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(_logBase.ActiveErrors);
    }

    [Fact]
    public async Task MomentaryExpiry_LeavesPersistentErrorsAlone()
    {
        _logBase.Persistent("input", "Wrong input");
        var expired = new TaskCompletionSource();
        _logBase.ActiveErrorsChanged += (_, e) =>
        {
            if (e.ActiveErrors.Count == 1)
                expired.TrySetResult();
        };

        _logBase.Momentary("A poll was missed", TimeSpan.FromMilliseconds(100));

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = Assert.Single(_logBase.ActiveErrors);
        Assert.Equal("input", error.Key);
    }

    [Fact]
    public void ActiveErrors_AreCappedAt50ByDefault()
    {
        for (var i = 0; i < 60; i++)
            _logBase.Momentary($"Error {i}", TimeSpan.FromMinutes(1));

        Assert.Equal(50, _logBase.ActiveErrors.Count);
    }

    [Fact]
    public void LimitingActiveErrors_EvictsOldestMomentaryErrorsFirst()
    {
        _logBase.SetActiveErrorLimit(2);

        _logBase.Momentary("A poll was missed", TimeSpan.FromMinutes(1), "poll");
        _logBase.Persistent("input", "Wrong input");
        _logBase.Persistent("communication", "Comms down");

        Assert.Equal(new[] { "input", "communication" }, _logBase.ActiveErrors.Select(e => e.Key));
    }

    [Fact]
    public void LimitingActiveErrors_NeverEvictsTheErrorJustRaised()
    {
        _logBase.SetActiveErrorLimit(2);

        _logBase.Persistent("input", "Wrong input");
        _logBase.Persistent("communication", "Comms down");
        _logBase.Momentary("A poll was missed", TimeSpan.FromMinutes(1), "poll");

        Assert.Equal(new[] { "communication", "poll" }, _logBase.ActiveErrors.Select(e => e.Key));
    }

    [Fact]
    public void SetActiveErrorLimit_TrimsExistingEntries_AndFiresChanged()
    {
        _logBase.Persistent("first", "First error");
        _logBase.Persistent("second", "Second error");
        _logBase.Persistent("third", "Third error");
        var changedCount = 0;
        _logBase.ActiveErrorsChanged += (_, _) => changedCount++;

        _logBase.SetActiveErrorLimit(1);

        var error = Assert.Single(_logBase.ActiveErrors);
        Assert.Equal("third", error.Key);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void ActiveErrors_AreOrderedByRaisedAt()
    {
        _logBase.Persistent("first", "First error");
        _logBase.Persistent("second", "Second error");

        Assert.Equal(new[] { "first", "second" }, _logBase.ActiveErrors.Select(e => e.Key));
    }
}
