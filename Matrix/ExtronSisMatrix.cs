using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract partial class ExtronSisMatrix : VideoMatrix
{
    private protected const string RejectedCommandIssueKey = "rejected-command";
    private const int MaxMissedReplies = 3;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private protected readonly TimeProvider TimeProvider;
    private readonly SentCommandTracker<ExtronSisCommand> _sent;
    private readonly object _sendLock = new();
    private readonly object _readsLock = new();
    private readonly HashSet<ExtronSisQuery> _unread = [];
    private readonly HashSet<ExtronSisQuery> _notSupported = [];
    private readonly Dictionary<ExtronSisQuery, int> _missedReplies = new();

    private protected ExtronSisMatrix(int numberOfOutputs, CommunicationClient communicationClient, string name, TimeProvider? timeProvider)
        : base(numberOfOutputs, communicationClient, name)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        _sent = new SentCommandTracker<ExtronSisCommand>(ReplyTimeout, TimeProvider);
    }

    [GeneratedRegex(@"^E\d\d$")]
    private static partial Regex ErrorRegex();

    private protected static bool IsError(string line) => ErrorRegex().IsMatch(line);

    private protected void Send(ExtronSisCommand command)
    {
        try
        {
            lock (_sendLock)
            {
                if (_sent.Add(command) is { } dropped)
                    LogWarning("Dropped {Command}: more than {Max} commands awaiting a reply",
                        dropped.Command.Name, SentCommandTracker<ExtronSisCommand>.MaxCommands);
                CommunicationClient.Send(command.Text);
            }
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    private protected IDisposable TakeReply(string line)
    {
        var tie = ExtronSisTie.Parse(line);
        return Trigger.Resume(_sent.TakeReply(command => command.IsAnsweredBy(line, tie))?.TriggerId);
    }

    private protected void Read(ExtronSisQuery[] queries)
    {
        lock (_readsLock)
            _unread.UnionWith(queries);
        foreach (var query in queries)
            Send(new ExtronSisCommand(query));
    }

    private protected void ReadAgainIfUnanswered(ExtronSisQuery[] queries)
    {
        List<ExtronSisQuery> unread;
        lock (_readsLock)
            unread = queries.Where(_unread.Contains).ToList();
        foreach (var query in unread.Where(query => !_sent.IsAwaitingReply(command => command.Query == query)))
            Send(new ExtronSisCommand(query));
    }

    private protected void ReadUnlessNotSupported(ExtronSisQuery query)
    {
        bool notSupported;
        lock (_readsLock)
            notSupported = _notSupported.Contains(query);
        if (!notSupported)
            Send(new ExtronSisCommand(query));
    }

    private protected void ShowRead(ExtronSisQuery query, string reply)
    {
        lock (_readsLock)
        {
            _unread.Remove(query);
            _notSupported.Remove(query);
            _missedReplies.Remove(query);
        }
        var value = reply.Length > query.ReplyTag.Length ? reply[query.ReplyTag.Length..] : string.Empty;
        SetDetail(query.Label, value.Length > 0 ? value : reply);
        ResolveIssue(query.IssueKey);
    }

    private protected void HandleError(string error)
    {
        if (_sent.TakeError() is not { } rejected)
        {
            LogWarning("The switcher sent {Error} with no command awaiting a reply", error);
            return;
        }
        using (Trigger.Resume(rejected.TriggerId))
        using (PushProperties())
        {
            if (rejected.Command.Query is not { } query)
                ReportRejected(rejected.Command, error);
            else if (ExtronSis.MeansNotSupported(error))
                ShowNotSupported(query, error);
            else
                CountMissedReply(query, error);
        }
    }

    private protected virtual void ReportRejected(ExtronSisCommand command, string error) =>
        RaiseMomentaryIssue($"The switcher rejected {command.Name} with {error}", RejectedCommandIssueKey);

    private void ShowNotSupported(ExtronSisQuery query, string error)
    {
        lock (_readsLock)
        {
            _unread.Remove(query);
            _notSupported.Add(query);
            _missedReplies.Remove(query);
        }
        LogInformation("The switcher does not support {Query} ({Label}): {Error}", query.Name, query.Label, error);
        SetDetail(query.Label, $"Not supported: {error}", DetailTone.Normal);
        ResolveIssue(query.IssueKey);
    }

    private protected void ReportMissedReplies()
    {
        foreach (var missed in _sent.TakeMissed())
            using (Trigger.Resume(missed.TriggerId))
            using (PushProperties())
            {
                if (missed.Command.Query is { } query)
                    CountMissedReply(query, $"no {query.ReplyTag.Trim()} reply within {ReplyTimeout.TotalSeconds} s");
                else
                    LogDebug("The switcher didn't answer {Command} within {Timeout} s", missed.Command.Name, ReplyTimeout.TotalSeconds);
            }
    }

    private void CountMissedReply(ExtronSisQuery query, string result)
    {
        int missed;
        lock (_readsLock)
        {
            missed = _missedReplies.GetValueOrDefault(query) + 1;
            _missedReplies[query] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("The switcher gave {Result} to {Query} ({Label}), asking again at the next poll ({Missed} of {MaxMissedReplies})",
                result, query.Name, query.Label, missed, MaxMissedReplies);
            return;
        }
        RaiseOngoingIssue(query.IssueKey,
            $"The switcher did not answer {query.Name} ({query.Label}) {MaxMissedReplies} times in a row, the last was {result}",
            IssueSeverity.Minor);
    }

    private protected void ForgetCommands()
    {
        _sent.Clear();
        lock (_readsLock)
        {
            _unread.Clear();
            _notSupported.Clear();
            _missedReplies.Clear();
        }
    }
}
