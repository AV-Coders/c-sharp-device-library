using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public class ExterityTci : MediaPlayer, ISetTopBox
{
    private const string ParamGroup = "param";
    private const string ValueGroup = "value";
    private const string UnrecognisedReplyIssueKey = "unrecognised-reply";
    private const string ErrorParam = "error";
    private const char ErrorWordSeparator = ' ';
    private const char ReplyStart = '^';
    private const char ReplyEnd = '!';
    private const int MaxIgnoredReplies = 10;
    private const int MaxMissedReplies = 3;
    private const int MaxHeldTextLength = 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginSettle = TimeSpan.FromSeconds(1);
    private static readonly Regex ParameterReplyParser = new($@"\^(?<{ParamGroup}>[^:!^]+):(?<{ValueGroup}>[^!^]*)!",
        RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Dictionary<string, string> Labels = new()
    {
        { "softwareVersion", "Software" },
        { "serialNumber", "Serial" },
        { "macAddress", "MAC" },
        { "name", "Name" },
        { "IPAddress", "IP Address" },
        { "productType", "Model" },
        { "upTime", "Uptime" },
        { "hdmiState", "HDMI" }
    };
    private static readonly string[] IdentityParams = ["softwareVersion", "serialNumber", "macAddress", "name", "IPAddress", "productType"];
    private static readonly string[] HealthParams = ["upTime", "hdmiState"];
    private readonly string _password;
    private readonly Dictionary<MuteState, string> _muteDictionary = new()
    {
        { MuteState.On, "on"},
        { MuteState.Off, "off"}
    };
    private readonly ThreadWorker _pollWorker;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _unreadIdentity = [..IdentityParams];
    private readonly HashSet<string> _unansweredReads = [];
    private readonly HashSet<string> _unsupportedReads = [];
    private readonly Dictionary<string, int> _missedReplies = new();
    private readonly List<string> _ignoredReplies = [];
    private readonly object _readsLock = new();
    private readonly object _firstReadsTimerLock = new();
    private ITimer? _firstReadsTimer;
    private string _heldText = string.Empty;

    public ExterityTci(CommunicationClient communicationClient, string password, string name)
        : this(communicationClient, password, name, null)
    {
    }

    internal ExterityTci(CommunicationClient communicationClient, string password, string name, TimeProvider? timeProvider)
        : base(name, communicationClient)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _password = password;
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.Unknown;
        _pollWorker = new ThreadWorker(Poll, PollInterval, false, _timeProvider);
        _ = _pollWorker.Restart();
    }

    private void WriteParameter(string parameter, string value)
    {
        CommunicationClient.Send($"^set:{parameter}:{value}!\n");
    }

    private void SimulateRemoteKeypress(string key)
    {
        CommunicationClient.Send($"^send:rm_{key}!\n");
    }

    public override void PowerOn()
    {
        WriteParameter("currentMode", "av");
        PowerState = PowerState.On;
    }

    public override void PowerOff()
    {
        WriteParameter("currentMode", "off");
        PowerState = PowerState.Off;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState == ConnectionState.Connected)
            ReadTheIdentity();
    }

    private void ReadTheIdentity()
    {
        lock (_readsLock)
        {
            _unreadIdentity.UnionWith(IdentityParams);
            _unansweredReads.Clear();
            _unsupportedReads.Clear();
            _missedReplies.Clear();
            _ignoredReplies.Clear();
            _heldText = string.Empty;
        }
        lock (_firstReadsTimerLock)
        {
            _firstReadsTimer?.Dispose();
            _firstReadsTimer = _timeProvider.CreateTimer(_ => _pollWorker.Restart(), null, LoginSettle, Timeout.InfiniteTimeSpan);
        }
    }

    private Task Poll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        using (PushProperties("Poll"))
        {
            ReportUnansweredReads();
            List<string> reads;
            lock (_readsLock)
            {
                reads = IdentityParams.Where(_unreadIdentity.Contains)
                    .Concat(HealthParams.Where(param => !_unsupportedReads.Contains(param)))
                    .ToList();
                _unansweredReads.UnionWith(reads);
            }
            foreach (var param in reads)
                CommunicationClient.Send($"^get:{param}!\n");
        }
        return Task.CompletedTask;
    }

    private void ReportUnansweredReads()
    {
        List<(string Param, int Missed)> unanswered = [];
        string ignored;
        lock (_readsLock)
        {
            foreach (var param in _unansweredReads)
            {
                var missed = _missedReplies.GetValueOrDefault(param) + 1;
                _missedReplies[param] = missed;
                unanswered.Add((param, missed));
            }
            ignored = string.Join(" ", _ignoredReplies);
            _unansweredReads.Clear();
            _ignoredReplies.Clear();
        }
        var result = ignored.Length == 0 ? "no reply" : $"no reply, the replies this driver does not read were: {ignored}";
        foreach (var (param, missed) in unanswered)
        {
            if (missed < MaxMissedReplies)
                LogInformation("The receiver gave {Result} to ^get:{Param}!, asking again at the next poll ({Missed} of {MaxMissedReplies})",
                    result, param, missed, MaxMissedReplies);
            else
                RaiseOngoingIssue(ReadIssueKey(param),
                    $"The receiver did not answer ^get:{param}! {MaxMissedReplies} times in a row, the last was {result}",
                    IssueSeverity.Minor);
        }
    }

    private void HandleResponse(string response)
    {
        using (PushProperties("HandleResponse"))
        {
            if (response.Contains("login as:"))
            {
                CommunicationClient.Send("ctrl\n");
            }
            else if (response.Contains("'s password:"))
            {
                CommunicationClient.Send($"{_password}\n");
                ReadTheIdentity();
            }
            else
            {
                HandleReplyText(response);
            }
        }
    }

    private void HandleReplyText(string response)
    {
        string complete;
        string held;
        lock (_readsLock)
        {
            var text = FromTheFirstReplyStart(_heldText + response);
            var end = text.LastIndexOf(ReplyEnd) + 1;
            complete = text[..end];
            held = FromTheFirstReplyStart(text[end..]);
            _heldText = held.Length > MaxHeldTextLength ? string.Empty : held;
        }
        if (held.Length > MaxHeldTextLength)
            RaiseMomentaryIssue($"The receiver sent {held.Length} characters with no closing {ReplyEnd}: {held}", UnrecognisedReplyIssueKey);
        if (complete.Length > 0)
            HandleParameterReplies(complete);
    }

    private string FromTheFirstReplyStart(string text)
    {
        var start = text.IndexOf(ReplyStart);
        var ignored = start < 0 ? text : text[..start];
        if (ignored.Trim().Length > 0)
            LogDebug("Ignoring {Text}, it is outside a ^param:value! reply", ignored);
        return start < 0 ? string.Empty : text[start..];
    }

    private void HandleParameterReplies(string text)
    {
        var replies = ParameterReplyParser.Matches(text);
        var unreadable = ParameterReplyParser.Replace(text, string.Empty).Trim();
        if (unreadable.Length > 0)
            RaiseMomentaryIssue($"The receiver sent a reply this driver can't read: {unreadable}", UnrecognisedReplyIssueKey);
        if (replies.Count == 0)
            return;
        CommunicationState = CommunicationState.Okay;
        foreach (Match reply in replies)
            HandleParameter(reply.Groups[ParamGroup].Value, reply.Groups[ValueGroup].Value, reply.Value);
    }

    private void HandleParameter(string param, string value, string reply)
    {
        if (param == ErrorParam)
        {
            HandleErrorReply(value, reply);
            return;
        }
        if (!Labels.TryGetValue(param, out var label))
        {
            IgnoreReply(reply);
            return;
        }
        lock (_readsLock)
        {
            _unansweredReads.Remove(param);
            _unreadIdentity.Remove(param);
            _missedReplies.Remove(param);
        }
        SetDetail(label, value.Trim().Length == 0 ? reply : value);
        ResolveIssue(ReadIssueKey(param));
    }

    private void HandleErrorReply(string error, string reply)
    {
        string? rejected;
        lock (_readsLock)
        {
            rejected = _unansweredReads.FirstOrDefault(param => error.Split(ErrorWordSeparator).Contains(param));
            if (rejected != null)
            {
                _unansweredReads.Remove(rejected);
                _unreadIdentity.Remove(rejected);
                _missedReplies.Remove(rejected);
                _unsupportedReads.Add(rejected);
            }
        }
        if (rejected == null)
        {
            IgnoreReply(reply);
            return;
        }
        LogInformation("The receiver does not support ^get:{Param}!: {Reply}", rejected, reply);
        SetDetail(Labels[rejected], $"Not supported: {reply}", DetailTone.Normal);
        ResolveIssue(ReadIssueKey(rejected));
    }

    private void IgnoreReply(string reply)
    {
        LogDebug("Ignoring {Reply}", reply);
        lock (_readsLock)
        {
            if (_unansweredReads.Count > 0 && _ignoredReplies.Count < MaxIgnoredReplies)
                _ignoredReplies.Add(reply);
        }
    }

    private static string ReadIssueKey(string param) => $"{param}-read";

    public void ChannelUp()
    {
        SimulateRemoteKeypress("chup");
    }

    public void ChannelDown()
    {
        SimulateRemoteKeypress("chdown");
    }

    public IReadOnlyCollection<RemoteButton> SupportedButtons => Enum.GetValues<RemoteButton>();

    public void SendIRCode(RemoteButton button)
    {
        SimulateRemoteKeypress(button.ToString());
    }

    public void VolumeUp()
    {
        SimulateRemoteKeypress("volup");
    }

    public void VolumeDown()
    {
        SimulateRemoteKeypress("voldown");
    }

    public void SetAudioMute(MuteState state)
    {
        if (!_muteDictionary.TryGetValue(state, out var mute))
        {
            LogError("Can't set the audio mute to {MuteState}", state);
            return;
        }

        WriteParameter("mute", mute);
    }

    public void SetChannel(int channelNumber)
    {
        if (channelNumber < 0)
        {
            LogError("Channel {Channel} is invalid, it can't be negative", channelNumber);
            return;
        }

        string channel = channelNumber.ToString();
        foreach (char digit in channel)
        {
            SimulateRemoteKeypress($"{digit}");
        }
        SimulateRemoteKeypress("enter");
    }

    public void ToggleSubtitles()
    {
        SimulateRemoteKeypress("subtitle");
    }
}
