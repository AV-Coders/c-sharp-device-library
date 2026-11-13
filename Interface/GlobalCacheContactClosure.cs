using AVCoders.Core;

namespace AvCoders.Interface;

public class GlobalCacheContactClosure : DeviceBase
{
    public const ushort DefaultPort = 4998;
    public const int DefaultModule = 1;
    public const int Ip2ccRelayCount = 3;
    public const string RejectedIssueKey = "rejected-command";
    public const string UnansweredIssueKey = "unanswered-command";
    public const string UnreadableReplyIssueKey = "unreadable-reply";
    public const int MaxUnterminatedLength = 1024;
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private const string SetStateCommand = "setstate";
    private const string GetStateCommand = "getstate";
    private const string StateReplyPrefix = "state,";
    private const string ErrorReplyPrefix = "ERR_";
    private const string UnknownCommandReplyPrefix = "unknowncommand";
    private const char FieldSeparator = ',';
    private const string CommandEnd = "\r";
    private const string ClosedValue = "1";
    private const string OpenValue = "0";
    private const string ClosedText = "Closed";
    private const string OpenText = "Open";
    private const string RelayLabelPrefix = "Relay ";
    private const int ReplyFieldCount = 3;
    private const int AddressField = 1;
    private const int ReplyValueField = 2;
    private const int ShownLength = 200;
    private static readonly char[] LineEnds = ['\r', '\n'];
    private static readonly string[] RelayCommandPrefixes = [SetStateCommand + ",", GetStateCommand + ","];

    private readonly int _module;
    private readonly int _relayCount;
    private readonly SentCommandTracker<string> _sent;
    private readonly ITimer _unansweredCheck;
    private readonly Dictionary<int, bool> _commandedClosed = new();
    private readonly object _lock = new();
    private readonly object _sendLock = new();
    private string _received = string.Empty;

    public GlobalCacheContactClosure(CommunicationClient client, string name, int module = DefaultModule, int relayCount = Ip2ccRelayCount)
        : this(client, name, module, relayCount, null)
    {
    }

    internal GlobalCacheContactClosure(CommunicationClient client, string name, int module, int relayCount, TimeProvider? timeProvider)
        : base(name, client)
    {
        _module = module;
        _relayCount = relayCount;
        var time = timeProvider ?? TimeProvider.System;
        _sent = new SentCommandTracker<string>(ReplyTimeout, time);
        _unansweredCheck = time.CreateTimer(_ => ReportUnanswered(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        client.RequestHandlers += RequestWritten;
        client.ResponseHandlers += HandleResponse;
        client.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(client.ConnectionState);
    }

    public void Close(int relay)
    {
        using (PushProperties())
        {
            AddEvent(EventType.DriverState, $"Closing relay {_module}:{relay}");
            lock (_sendLock)
            {
                lock (_lock)
                    _commandedClosed[relay] = true;
                CommunicationClient.Send($"{SetStateCommand},{_module}:{relay},{ClosedValue}{CommandEnd}");
            }
        }
    }

    public void Open(int relay)
    {
        using (PushProperties())
        {
            AddEvent(EventType.DriverState, $"Opening relay {_module}:{relay}");
            lock (_sendLock)
            {
                lock (_lock)
                    _commandedClosed[relay] = false;
                CommunicationClient.Send($"{SetStateCommand},{_module}:{relay},{OpenValue}{CommandEnd}");
            }
        }
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        using (PushProperties())
        {
            AddEvent(EventType.Connection, connectionState.ToString());
            if (connectionState != ConnectionState.Connected)
            {
                lock (_lock)
                {
                    _sent.Clear();
                    _unansweredCheck.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _received = string.Empty;
                }
                CommunicationState = CommunicationClient.DesiredConnectionState == ConnectionState.Disconnected
                    ? CommunicationState.NotAttempted
                    : CommunicationState.Unknown;
                return;
            }
            lock (_sendLock)
            {
                for (var relay = 1; relay <= _relayCount; relay++)
                    CommunicationClient.Send($"{GetStateCommand},{_module}:{relay}{CommandEnd}");
                List<KeyValuePair<int, bool>> commanded;
                lock (_lock)
                    commanded = [.._commandedClosed.OrderBy(relay => relay.Key)];
                foreach (var (relay, closed) in commanded)
                {
                    AddEvent(EventType.DriverState, $"Setting relay {_module}:{relay} {(closed ? ClosedText : OpenText)} again, as last commanded");
                    CommunicationClient.Send($"{SetStateCommand},{_module}:{relay},{(closed ? ClosedValue : OpenValue)}{CommandEnd}");
                }
            }
        }
    }

    private void RequestWritten(string request)
    {
        var command = request.TrimEnd(LineEnds);
        if (!RelayCommandPrefixes.Any(prefix => command.StartsWith(prefix, StringComparison.Ordinal)))
            return;
        if (_sent.Add(command) is { } forgotten)
        {
            using (PushProperties())
                LogWarning("Forgot {Command}: more than {Max} commands are awaiting a reply", forgotten.Command,
                    SentCommandTracker<string>.MaxCommands);
        }
        ScheduleUnansweredCheck();
    }

    private void HandleResponse(string response)
    {
        List<string> lines = [];
        string? dropped = null;
        lock (_lock)
        {
            _received += response;
            var end = _received.IndexOfAny(LineEnds);
            while (end >= 0)
            {
                lines.Add(_received[..end]);
                _received = _received[(end + 1)..];
                end = _received.IndexOfAny(LineEnds);
            }
            if (_received.Length > MaxUnterminatedLength)
            {
                dropped = _received;
                _received = string.Empty;
            }
        }
        using (PushProperties())
        {
            foreach (var line in lines.Where(line => line.Length > 0))
                HandleLine(line);
            if (dropped != null)
                RaiseMomentaryIssue($"Dropped {dropped.Length} characters from the Global Cache with no line end: {Escaped(dropped)}",
                    UnreadableReplyIssueKey);
        }
    }

    private void HandleLine(string line)
    {
        var fields = line.Split(FieldSeparator);
        if (line.StartsWith(StateReplyPrefix, StringComparison.Ordinal) && fields.Length == ReplyFieldCount)
        {
            HandleState(fields[AddressField], fields[ReplyValueField]);
            return;
        }
        if (line.StartsWith(ErrorReplyPrefix, StringComparison.Ordinal) || line.StartsWith(UnknownCommandReplyPrefix, StringComparison.Ordinal))
        {
            HandleRejection(line);
            return;
        }
        RaiseMomentaryIssue($"The Global Cache sent a reply this driver can't read: {Escaped(line)}", UnreadableReplyIssueKey);
    }

    private void HandleState(string address, string value)
    {
        using (Trigger.Resume(_sent.TakeReply(command => command.Split(FieldSeparator)[AddressField] == address)?.TriggerId))
        {
            CommunicationState = CommunicationState.Okay;
            SetDetail($"{RelayLabelPrefix}{address}", value switch
            {
                ClosedValue => ClosedText,
                OpenValue => OpenText,
                _ => value
            });
        }
    }

    private void HandleRejection(string line)
    {
        CommunicationState = CommunicationState.Okay;
        if (_sent.TakeError() is not { } rejected)
        {
            LogWarning("The Global Cache sent {Reply} with no command awaiting a reply", Escaped(line));
            return;
        }
        using (Trigger.Resume(rejected.TriggerId))
            RaiseMomentaryIssue($"The Global Cache rejected {rejected.Command} with {Escaped(line)}", RejectedIssueKey);
    }

    private void ScheduleUnansweredCheck()
    {
        lock (_lock)
            _unansweredCheck.Change(_sent.TimeUntilNextMiss() ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void ReportUnanswered()
    {
        foreach (var missed in _sent.TakeMissed())
        {
            using (Trigger.Resume(missed.TriggerId))
            using (PushProperties())
                RaiseMomentaryIssue($"The Global Cache did not answer {missed.Command} within {ReplyTimeout.TotalSeconds} s{UnterminatedText()}",
                    UnansweredIssueKey);
        }
        ScheduleUnansweredCheck();
    }

    private string UnterminatedText()
    {
        lock (_lock)
            return _received.Length == 0 ? "" : $" (it sent {_received.Length} characters with no line end: {Escaped(_received)})";
    }

    private static string Escaped(string text) => Bytes.Escaped(Bytes.FromString(text), ShownLength);
}
