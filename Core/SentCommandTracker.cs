namespace AVCoders.Core;

public sealed record SentCommand<TCommand>(TCommand Command, string? TriggerId);

public sealed class SentCommandTracker<TCommand>
{
    public const int MaxCommands = 256;
    private readonly TimeSpan _replyTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly List<Entry> _sent = [];
    private readonly List<Entry> _missed = [];

    public SentCommandTracker(TimeSpan replyTimeout)
        : this(replyTimeout, TimeProvider.System)
    {
    }

    internal SentCommandTracker(TimeSpan replyTimeout, TimeProvider? timeProvider)
    {
        _replyTimeout = replyTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SentCommand<TCommand>? Add(TCommand command, TimeSpan lateErrorWindow = default)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            MoveMissed(now);
            _sent.Add(new Entry(new SentCommand<TCommand>(command, Trigger.Id), now, lateErrorWindow));
            return DropOldestOverMaxCommands();
        }
    }

    public SentCommand<TCommand>? TakeReply(Func<TCommand, bool> answers)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            var answered = _sent.Find(entry => AwaitsReply(entry, now) && answers(entry.Sent.Command));
            if (answered == null)
                return null;
            if (answered.LateErrorWindow > TimeSpan.Zero)
                answered.AnsweredAt = now;
            else
                _sent.Remove(answered);
            return answered.Sent;
        }
    }

    public SentCommand<TCommand>? TakeError(Func<TCommand, bool>? canReceive = null)
    {
        var eligible = canReceive ?? (_ => true);
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            var rejected = _sent.Find(entry => AwaitsReply(entry, now) && eligible(entry.Sent.Command))
                ?? _sent.Find(entry => InLateErrorWindow(entry, now) && eligible(entry.Sent.Command));
            if (rejected == null)
                return null;
            _sent.Remove(rejected);
            return rejected.Sent;
        }
    }

    public bool IsAwaitingReply(Func<TCommand, bool> matches)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetTimestamp();
            return _sent.Exists(entry => AwaitsReply(entry, now) && matches(entry.Sent.Command));
        }
    }

    public List<SentCommand<TCommand>> TakeMissed()
    {
        lock (_lock)
        {
            MoveMissed(_timeProvider.GetTimestamp());
            var missed = _missed.Select(entry => entry.Sent).ToList();
            _missed.Clear();
            return missed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _sent.Clear();
            _missed.Clear();
        }
    }

    private void MoveMissed(long now)
    {
        _missed.AddRange(_sent.Where(entry => entry.AnsweredAt == null && !AwaitsReply(entry, now)));
        _sent.RemoveAll(entry => !AwaitsReply(entry, now) && !InLateErrorWindow(entry, now));
    }

    private SentCommand<TCommand>? DropOldestOverMaxCommands()
    {
        if (_missed.Count + _sent.Count <= MaxCommands)
            return null;
        var oldest = _missed.Count > 0 ? _missed : _sent;
        var dropped = oldest[0];
        oldest.RemoveAt(0);
        return dropped.Sent;
    }

    private bool AwaitsReply(Entry entry, long now) =>
        entry.AnsweredAt == null && _timeProvider.GetElapsedTime(entry.SentAt, now) < _replyTimeout;

    private bool InLateErrorWindow(Entry entry, long now) =>
        entry.AnsweredAt is { } answeredAt && _timeProvider.GetElapsedTime(answeredAt, now) < entry.LateErrorWindow;

    private sealed class Entry(SentCommand<TCommand> sent, long sentAt, TimeSpan lateErrorWindow)
    {
        public SentCommand<TCommand> Sent { get; } = sent;
        public long SentAt { get; } = sentAt;
        public TimeSpan LateErrorWindow { get; } = lateErrorWindow;
        public long? AnsweredAt { get; set; }
    }
}
