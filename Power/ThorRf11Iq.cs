using System.Globalization;
using System.Text;
using System.Xml;
using AVCoders.Core;

namespace AVCoders.Power;

public record ThorRf11IqCircuitStatus(int Circuit, bool Protected, bool BuzzerEnabled);

public delegate void ThorRf11IqCircuitHandler(IReadOnlyList<ThorRf11IqCircuitStatus> circuits);

public class ThorRf11IqOutlet : Outlet
{
    /// <summary>The status.xml / cpan.cgi channel number: 1-7 are the switched GPO outlets, 8 is the IEC group.</summary>
    public readonly int Channel;
    public FloatHandler? CurrentHandlers;
    private readonly ThorRf11IqPdu _pdu;
    private float _currentAmps;

    public ThorRf11IqOutlet(string name, ThorRf11IqPdu pdu, int channel) : base(name)
    {
        _pdu = pdu;
        Channel = channel;
    }

    public float CurrentAmps
    {
        get => _currentAmps;
        internal set
        {
            if (Math.Abs(_currentAmps - value) < 0.05f)
                return;
            _currentAmps = value;
            CurrentHandlers?.Invoke(value);
        }
    }

    public override void PowerOn() => _pdu.PowerOn(this);

    public override void PowerOff() => _pdu.PowerOff(this);

    public override void Reboot() => _pdu.Reboot(this);
}

/// <summary>
/// Thor Technologies RF11iQ Smart Rack Guard (HTTP, basic auth as admin). Channels 1-7 are the
/// individually switched GPO outlets on circuit 1 and channel 8 switches the IEC outputs on
/// circuit 2 as a group. The unit has no outlet names or native reboot: a reboot is an off
/// command followed by an on command after <see cref="RebootDelay"/>.
/// </summary>
public class ThorRf11IqPdu : Pdu
{
    public const string DefaultUser = "admin";
    public const int OutletCount = 8;
    public const int IecOutletChannel = 8;
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultRebootDelay = TimeSpan.FromSeconds(5);

    public ThorRf11IqCircuitHandler? CircuitHandlers;
    public BoolHandler? BuzzerActiveHandlers;
    public StringHandler? FirmwareVersionHandlers;

    private const string StatusPath = "/status.xml";
    private const string CommandPath = "/cpan.cgi";
    private const string CommandAcknowledgement = "Success!";
    private const string PollIssueKey = "unanswered-poll";
    private const string CommandIssueKey = "unacknowledged-command";
    private const string BuzzerIssueKey = "buzzer";

    private readonly RestComms _restClient;
    private readonly Uri _statusUri = new(StatusPath, UriKind.Relative);
    private readonly ThreadWorker _pollWorker;
    private readonly object _lock = new();
    // Responses only identify their request by path, so identical in-flight commands are
    // matched to their replies first-in first-out.
    private readonly Dictionary<string, Queue<TaskCompletionSource<string?>>> _pendingCommands = new();
    private readonly Dictionary<int, ChannelState> _channels = new();
    private DateTime _pollDispatchedUtc;
    private volatile bool _statusReceived;
    private volatile string? _pollFailure;
    private volatile IReadOnlyList<ThorRf11IqCircuitStatus> _circuits = [];
    private bool _buzzerActive;
    private string _firmwareVersion = string.Empty;

    private sealed class ChannelState
    {
        public DateTime LastCommandUtc;
        public CancellationTokenSource? Reboot;
    }

    public TimeSpan RebootDelay { get; set; } = DefaultRebootDelay;

    /// <param name="outletNames">Names keyed by channel (1-7 = GPO outlets, 8 = IEC group). The device
    /// stores no names, so any channel not supplied is named "Output n" or "IEC Outputs".</param>
    public ThorRf11IqPdu(RestComms restClient, string name, string password,
        IReadOnlyDictionary<int, string>? outletNames = null, TimeSpan? pollInterval = null)
        : base(name, restClient)
    {
        _restClient = restClient;
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{DefaultUser}:{password}"));
        _restClient.AddDefaultHeader("Authorization", $"Basic {credentials}");
        _restClient.HttpResponseHandlers += HandleResponse;
        CommunicationState = CommunicationState.Unknown;

        for (var channel = 1; channel <= OutletCount; channel++)
        {
            var defaultName = channel == IecOutletChannel ? "IEC Outputs" : $"Output {channel}";
            var outletName = outletNames?.GetValueOrDefault(channel);
            AddOutlet(new ThorRf11IqOutlet(string.IsNullOrWhiteSpace(outletName) ? defaultName : outletName,
                this, channel));
            _channels[channel] = new ChannelState();
        }

        _pollWorker = new ThreadWorker(Poll, pollInterval ?? DefaultPollInterval);
        _pollWorker.Restart();
    }

    public IReadOnlyList<ThorRf11IqCircuitStatus> Circuits
    {
        get => _circuits;
        private set
        {
            if (_circuits.SequenceEqual(value))
                return;
            _circuits = value;
            foreach (var circuit in value)
            {
                if (circuit.Protected)
                    ResolveIssue(CircuitIssueKey(circuit.Circuit));
                else
                    RaiseOngoingIssue(CircuitIssueKey(circuit.Circuit),
                        $"Circuit {circuit.Circuit} surge protection is damaged", IssueSeverity.Critical);
            }
            CircuitHandlers?.Invoke(value);
        }
    }

    public bool BuzzerActive
    {
        get => _buzzerActive;
        private set
        {
            if (_buzzerActive == value)
                return;
            _buzzerActive = value;
            if (value)
                RaiseOngoingIssue(BuzzerIssueKey, "The fault buzzer is sounding", IssueSeverity.Minor);
            else
                ResolveIssue(BuzzerIssueKey);
            BuzzerActiveHandlers?.Invoke(value);
        }
    }

    public string FirmwareVersion
    {
        get => _firmwareVersion;
        private set
        {
            if (_firmwareVersion == value)
                return;
            _firmwareVersion = value;
            FirmwareVersionHandlers?.Invoke(value);
        }
    }

    private static string CircuitIssueKey(int circuit) => $"circuit-{circuit}-damaged";

    private void HandleResponse(HttpResponseMessage response)
    {
        using (PushProperties())
        {
            var path = response.RequestMessage?.RequestUri?.PathAndQuery;
            if (string.IsNullOrEmpty(path))
            {
                LogWarning("Ignoring a response that does not identify its request");
                return;
            }

            if (path.StartsWith(CommandPath))
                CompleteCommand(path, response);
            else if (path.StartsWith(StatusPath))
                HandleStatusResponse(response);
        }
    }

    private void CompleteCommand(string path, HttpResponseMessage response)
    {
        string? failure = null;
        if (!response.IsSuccessStatusCode)
            failure = $"HTTP {(int)response.StatusCode}";
        else
        {
            var body = ReadBody(response) ?? string.Empty;
            if (!body.StartsWith(CommandAcknowledgement))
                failure = $"the reply was '{body.Trim()}'";
        }

        TaskCompletionSource<string?>? pending = null;
        lock (_lock)
        {
            if (_pendingCommands.TryGetValue(path, out var queue) && queue.Count > 0)
                pending = queue.Dequeue();
        }
        pending?.TrySetResult(failure);
    }

    private void HandleStatusResponse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            _pollFailure = $"HTTP {(int)response.StatusCode}";
            return;
        }
        var body = ReadBody(response);
        if (body == null)
        {
            _pollFailure = "the response body could not be read";
            return;
        }
        ParseStatus(body, _pollDispatchedUtc);
    }

    private string? ReadBody(HttpResponseMessage response)
    {
        try
        {
            return response.Content.ReadAsStringAsync().Result;
        }
        catch (Exception e)
        {
            LogException(e);
            return null;
        }
    }

    private void ParseStatus(string body, DateTime dispatchedUtc)
    {
        XmlElement root;
        try
        {
            var document = new XmlDocument();
            document.LoadXml(body);
            if (document.DocumentElement == null)
            {
                _pollFailure = "the status response was empty";
                return;
            }
            root = document.DocumentElement;
        }
        catch (XmlException e)
        {
            _pollFailure = "the status response was not valid XML";
            LogException(e, "The status response was not valid XML");
            AddEvent(EventType.Error, $"The status response could not be parsed: {e.Message}");
            return;
        }
        _statusReceived = true;

        // Consumer handlers run inside the property setters; one that throws must not turn a
        // healthy poll into a communication failure.
        try
        {
            string? Value(string element) => root.SelectSingleNode(element)?.InnerText;

            foreach (var outlet in Outlets.OfType<ThorRf11IqOutlet>())
            {
                var state = Value($"p{outlet.Channel}");
                if (state != null && PollMayUpdate(outlet.Channel, dispatchedUtc))
                    outlet.OverridePowerState(state == "1" ? PowerState.On : PowerState.Off);
                if (float.TryParse(Value($"c{outlet.Channel}"), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var amps))
                    outlet.CurrentAmps = amps;
            }

            List<ThorRf11IqCircuitStatus> circuits = [];
            for (var circuit = 1; circuit <= 2; circuit++)
            {
                var status = Value($"s{circuit}");
                if (status == null)
                    continue;
                circuits.Add(new ThorRf11IqCircuitStatus(circuit, status == "1", Value($"b{circuit}") == "1"));
            }
            Circuits = circuits;

            var buzzer = Value("bz");
            if (buzzer != null)
                BuzzerActive = buzzer == "1";
            var version = Value("ver");
            if (!string.IsNullOrWhiteSpace(version))
                FirmwareVersion = version;
        }
        catch (Exception e)
        {
            LogException(e, "A status update handler threw");
            AddEvent(EventType.Error, $"A status update handler threw: {e.Message}");
        }
    }

    // A poll dispatched before a command carries the pre-command state, and a rebooting
    // channel is deliberately off; neither may overwrite what the driver knows.
    private bool PollMayUpdate(int channel, DateTime dispatchedUtc)
    {
        lock (_lock)
        {
            var state = _channels[channel];
            return state.Reboot == null && state.LastCommandUtc <= dispatchedUtc;
        }
    }

    private async Task Poll(CancellationToken token)
    {
        _statusReceived = false;
        _pollFailure = null;
        _pollDispatchedUtc = DateTime.UtcNow;
        await _restClient.Get(_statusUri);
        if (_statusReceived)
        {
            ResolveIssue(PollIssueKey);
            CommunicationState = CommunicationState.Okay;
            return;
        }
        RaiseMomentaryIssue($"The status poll failed: {_pollFailure ?? "no response"}", key: PollIssueKey,
            escalateAfter: 3);
        CommunicationState = CommunicationState.Error;
    }

    public override void PowerOn() => _ = SetAllOutlets(true);

    public override void PowerOff() => _ = SetAllOutlets(false);

    private async Task SetAllOutlets(bool on)
    {
        foreach (var outlet in Outlets.OfType<ThorRf11IqOutlet>())
            await SetOutletAsync(outlet, on);
    }

    public void PowerOn(ThorRf11IqOutlet outlet) => _ = SetOutletAsync(outlet, true);

    public void PowerOff(ThorRf11IqOutlet outlet) => _ = SetOutletAsync(outlet, false);

    public void Reboot(ThorRf11IqOutlet outlet) => _ = RebootAsync(outlet);

    /// <summary>Enables or disables the fault buzzer for circuit 1 (GPO outlets) or 2 (IEC outputs).</summary>
    public void SetBuzzerEnabled(int circuit, bool enabled)
    {
        if (circuit is < 1 or > 2)
        {
            using (PushProperties(nameof(SetBuzzerEnabled)))
                LogWarning("Ignoring the buzzer command, circuit {Circuit} does not exist", circuit);
            return;
        }
        _ = SendCommandAsync($"b{circuit}", enabled ? "1" : "0",
            $"buzzer {(enabled ? "enable" : "disable")} command for circuit {circuit}", () => { });
    }

    public void SilenceBuzzer() => _ = SendCommandAsync("bz", "0", "buzzer silence command", () => { });

    /// <summary>
    /// Sends an outlet command. An explicit command cancels any reboot in progress on the channel
    /// so the delayed on command cannot reverse it.
    /// </summary>
    private Task<bool> SetOutletAsync(ThorRf11IqOutlet outlet, bool on)
    {
        CancellationTokenSource? reboot;
        lock (_lock)
        {
            reboot = _channels[outlet.Channel].Reboot;
            _channels[outlet.Channel].Reboot = null;
        }
        reboot?.Cancel();
        return SendOutletCommandAsync(outlet, on, on ? PowerState.On : PowerState.Off);
    }

    private Task<bool> SendOutletCommandAsync(ThorRf11IqOutlet outlet, bool on, PowerState acknowledgedState)
    {
        lock (_lock)
            _channels[outlet.Channel].LastCommandUtc = DateTime.UtcNow;
        return SendCommandAsync($"p{outlet.Channel}", on ? "1" : "0",
            $"{(on ? "power on" : "power off")} command for outlet {outlet.Name}",
            () => outlet.OverridePowerState(acknowledgedState));
    }

    private async Task RebootAsync(ThorRf11IqOutlet outlet)
    {
        var cancellation = new CancellationTokenSource();
        lock (_lock)
        {
            if (_channels[outlet.Channel].Reboot != null)
            {
                using (PushProperties(nameof(Reboot)))
                    LogWarning("Ignoring the reboot command, outlet {Outlet} is already rebooting", outlet.Name);
                return;
            }
            _channels[outlet.Channel].Reboot = cancellation;
        }

        try
        {
            if (!await SendOutletCommandAsync(outlet, false, PowerState.Rebooting))
                return;
            try
            {
                await Task.Delay(RebootDelay, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                AddEvent(EventType.Power, $"The reboot of outlet {outlet.Name} was cancelled by a later command");
                return;
            }
            await SendOutletCommandAsync(outlet, true, PowerState.On);
        }
        finally
        {
            lock (_lock)
            {
                if (_channels[outlet.Channel].Reboot == cancellation)
                    _channels[outlet.Channel].Reboot = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task<bool> SendCommandAsync(string parameter, string value, string description, Action onAcknowledged)
    {
        var path = $"{CommandPath}?param={parameter}&value={value}";
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (!_pendingCommands.TryGetValue(path, out var queue))
                _pendingCommands[path] = queue = new Queue<TaskCompletionSource<string?>>();
            queue.Enqueue(pending);
        }

        await _restClient.Get(new Uri(path, UriKind.Relative));

        string? failure;
        if (pending.Task.IsCompleted)
            failure = pending.Task.Result;
        else
        {
            failure = "no response";
            lock (_lock)
            {
                if (_pendingCommands.TryGetValue(path, out var queue))
                {
                    var remaining = queue.Where(item => item != pending).ToList();
                    queue.Clear();
                    remaining.ForEach(queue.Enqueue);
                    if (queue.Count == 0)
                        _pendingCommands.Remove(path);
                }
            }
        }

        if (failure != null)
        {
            RaiseMomentaryIssue($"The {description} was not acknowledged: {failure}", key: CommandIssueKey);
            return false;
        }
        ResolveIssue(CommandIssueKey);
        AddEvent(EventType.Power, $"Sent the {description}");
        onAcknowledged();
        return true;
    }
}
