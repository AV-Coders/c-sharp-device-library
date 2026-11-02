using AVCoders.Core;

namespace AVCoders.Matrix;

public class Navigator : VideoMatrix
{
    public static readonly ushort DefaultPort = 22023;
    public readonly SshClient SshClient;
    private readonly Dictionary<string, Action<string>> _callbacks = new();
    private readonly object _callbacksLock = new();
    public const string EscapeHeader = "\x1b";
    private const char NotConfigured = '0';
    private const char Online = '1';
    private const char Offline = '2';
    private const char PresentNotConnected = '3';
    private const string InventoryIssuePrefix = "inventory-read:";
    private const string RelayIssueKey = "endpoint-relay";
    private const string UnregisteredIssuePrefix = "unregistered:";
    private const string UnclaimedOfflineIssuePrefix = "unclaimed-offline:";
    private const string AddressQuery = EscapeHeader + "CI\r";
    private const string AddressTag = "Ipi";
    private const int UnansweredForwardLimit = 5;
    private static readonly TimeSpan InventoryPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ForwardReplyTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeProvider _timeProvider;
    private readonly ThreadWorker _inventoryWorker;
    private readonly SentCommandTracker<ForwardedCommand> _forwarded;
    private readonly object _relayLock = new();
    private readonly object _addressLookupsLock = new();
    private readonly Dictionary<string, AddressLookup> _addressLookups = new();
    private readonly Dictionary<string, string> _unregisteredFindings = new();
    private readonly HashSet<string> _inventoriesSinceConnect = [];
    private int _forwardsSent;
    private int _lastRelayedForward;
    private int _unansweredDeviceForwards = 0;
    private int _offlineEncoders;
    private int _offlineDecoders;

    private readonly List<NavEncoder> _inputs = [];
    private readonly List<NavDecoder> _outputs = [];
    private readonly object _endpointsLock = new();

    private static readonly Dictionary<string, string> ErrorDescriptions = new()
    {
        { "E10", "Invalid command" },
        { "E12", "Invalid port number" },
        { "E13", "Invalid parameter" },
        { "E14", "Invalid for this port configuration" },
        { "E17", "Invalid command for signal type" },
        { "E22", "Busy" },
        { "E24", "Privilege violation" },
        { "E25", "Device not present" },
        { "E28", "Bad file name or file not found" },
    };

    private sealed record ForwardedCommand(string DeviceId, string Command, int Number, bool WasAnswering);

    private sealed record AddressLookup(long UnclaimedSince, string? Reply);

    public Navigator(string name, SshClient sshClient) : this(name, sshClient, null)
    {
    }

    internal Navigator(string name, SshClient sshClient, TimeProvider? timeProvider = null) : base(0, sshClient, name)
    {
        SshClient = sshClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _forwarded = new SentCommandTracker<ForwardedCommand>(ForwardReplyTimeout, _timeProvider);
        _inventoryWorker = new ThreadWorker(PollInventory, InventoryPollInterval, true, _timeProvider);
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        _forwarded.Clear();
        lock (_relayLock)
            _unansweredDeviceForwards = 0;
        lock (_inventoriesSinceConnect)
            _inventoriesSinceConnect.Clear();
        if (connectionState != ConnectionState.Connected)
            return;
        CommunicationClient.Send($"{EscapeHeader}3CV\r");
        SeedSystemState();
        _ = _inventoryWorker.Restart();
    }

    public void SeedSystemState()
    {
        SendInventoryQueries();
        CommunicationClient.Send($"{EscapeHeader}Ties*A*RPRT\r");
    }

    private void SendInventoryQueries()
    {
        CommunicationClient.Send($"{EscapeHeader}Inventory*I*RPRT\r");
        CommunicationClient.Send($"{EscapeHeader}Inventory*O*RPRT\r");
    }

    private Task PollInventory(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;
        using (PushProperties())
            CountUnansweredForwards();
        SendInventoryQueries();
        return Task.CompletedTask;
    }

    private void CountUnansweredForwards()
    {
        var missed = _forwarded.TakeMissed().Where(sent => sent.Command.WasAnswering).ToList();
        int unanswered;
        lock (_relayLock)
        {
            missed = missed.Where(sent => sent.Command.Number > _lastRelayedForward).ToList();
            _unansweredDeviceForwards += missed.Count;
            unanswered = _unansweredDeviceForwards;
        }
        if (missed.Count == 0 || unanswered <= UnansweredForwardLimit)
            return;
        RaiseOngoingIssue(RelayIssueKey,
            $"The Navigator has relayed no reply to the last {unanswered} commands sent to endpoints, the latest {Printable(missed[^1].Command)}",
            IssueSeverity.Critical);
    }

    private List<NavDeviceBase> Endpoints(bool encoders, bool decoders)
    {
        lock (_endpointsLock)
            return [..encoders ? _inputs : [], ..decoders ? _outputs : []];
    }

    public virtual void QueryEndpointState(NavDeviceBase endpoint)
    {
        if (endpoint.DeviceNumber == 0)
            return;
        CommunicationClient.Send($"{EscapeHeader}P*{endpoint.DeviceNumber}{endpoint.GetLetterForDeviceType()}DEVP\r");
        if (endpoint.DeviceType != AVEndpointType.Decoder)
            return;
        CommunicationClient.Send($"{EscapeHeader}{endpoint.DeviceNumber}%\r");
        CommunicationClient.Send($"{EscapeHeader}{endpoint.DeviceNumber}$\r");
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            if (response.StartsWith('{'))
            {
                ForwardDeviceResponse(response);
                CommunicationState = CommunicationState.Okay;
            }
            else if (response.StartsWith("Out"))
                HandleTieResponse(response);
            else if (response.StartsWith("Devp"))
                HandleDevicePresenceResponse(response);
            else if (response.StartsWith("Rprt*Inventory*"))
                HandleInventoryReport(response);
            else if (response.Contains('\t'))
                HandleTieReportRow(response);
            else if (response.Length == 3 && response.StartsWith('E') && char.IsDigit(response[1]) && char.IsDigit(response[2]))
                HandleErrorCode(response);
        }
    }

    private void HandleErrorCode(string error)
    {
        var offline = OfflineEndpoints();
        if (error == ExtronSis.InvalidParameter && _forwarded.TakeError(command => offline.ContainsKey(command.DeviceId)) is { } missed)
        {
            using (Trigger.Resume(missed.TriggerId))
                offline[missed.Command.DeviceId].HandleMissedReply(missed.Command.Command, error);
            return;
        }
        LogWarning("The Navigator rejected a command: {ErrorCode} - {ErrorMeaning}", error,
            ErrorDescriptions.GetValueOrDefault(error, "Unknown error"));
    }

    private Dictionary<string, NavDeviceBase> OfflineEndpoints()
    {
        var offline = new Dictionary<string, NavDeviceBase>();
        foreach (var endpoint in Endpoints(_offlineEncoders > 0, _offlineDecoders > 0)
                     .Where(endpoint => endpoint.DeviceConnectionState != ConnectionState.Connected))
            offline[endpoint.DeviceId] = endpoint;
        return offline;
    }

    private void HandleTieResponse(string response)
    {
        var parts = response.Split(' ');
        if (parts.Length != 3 || !int.TryParse(parts[0][3..], out var outputNumber))
            return;
        CommunicationState = CommunicationState.Okay;
        FindCallback($"{outputNumber:D4}o")?.Invoke($"{parts[1]} {parts[2]}");
    }

    private void HandleDevicePresenceResponse(string response)
    {
        var parts = response.Split('*');
        if (parts.Length != 3)
            return;
        CommunicationState = CommunicationState.Okay;
        var endpoint = FindEndpoint(parts[1]);
        if (endpoint == null)
            return;
        switch (parts[0])
        {
            case "DevpP":
            case "DevpC":
                var tunnel = (NavigatorTunnel)endpoint.CommunicationClient;
                tunnel.SetConnectionState(parts[2] == "1" ? ConnectionState.Connected : ConnectionState.Disconnected);
                break;
        }
    }

    private void HandleInventoryReport(string response)
    {
        var parts = response.Split('*');
        if (parts.Length != 4)
            return;
        CommunicationState = CommunicationState.Okay;
        var endpoints = Endpoints(parts[2] == "I", parts[2] == "O");
        var inventory = parts[3];
        ShowInventoryCounts(parts[2], inventory, response);
        bool firstSinceConnect;
        lock (_inventoriesSinceConnect)
            firstSinceConnect = _inventoriesSinceConnect.Add(parts[2]);
        foreach (var endpoint in endpoints)
        {
            if (endpoint.DeviceNumber == 0 || endpoint.DeviceNumber > inventory.Length)
                continue;
            var state = inventory[endpoint.DeviceNumber - 1];
            var tunnel = (NavigatorTunnel)endpoint.CommunicationClient;
            if (IsNotAnswering(state))
                tunnel.SetConnectionState(ConnectionState.Disconnected);
            else if (state == Online && firstSinceConnect)
                tunnel.SetConnectionState(ConnectionState.Connected);
        }
        switch (parts[2])
        {
            case "I":
                _offlineEncoders = inventory.Count(IsNotAnswering);
                CheckUnregisteredEndpoints(endpoints, inventory, "i", "encoder");
                break;
            case "O":
                _offlineDecoders = inventory.Count(IsNotAnswering);
                CheckUnregisteredEndpoints(endpoints, inventory, "o", "decoder");
                break;
        }
    }

    private void ShowInventoryCounts(string endpointType, string inventory, string response)
    {
        var endpoints = endpointType switch
        {
            "I" => "Encoders",
            "O" => "Decoders",
            _ => $"Type {endpointType} Endpoints"
        };
        var offline = inventory.Count(state => state == Offline);
        var notConnected = inventory.Count(state => state == PresentNotConnected);
        SetDetail($"{endpoints} Online", inventory.Count(state => state == Online).ToString());
        SetDetail($"{endpoints} Offline", offline.ToString(), offline == 0 ? DetailTone.Normal : DetailTone.Warning);
        SetDetail($"{endpoints} Present, Not Connected", notConnected.ToString(), notConnected == 0 ? DetailTone.Normal : DetailTone.Warning);
        if (inventory.All(state => state is NotConfigured or Online or Offline or PresentNotConnected))
            ResolveIssue($"{InventoryIssuePrefix}{endpointType}");
        else
            RaiseOngoingIssue($"{InventoryIssuePrefix}{endpointType}",
                $"The Navigator's {endpoints.ToLowerInvariant()} inventory has states other than {NotConfigured}, {Online}, {Offline} and {PresentNotConnected}: {response}",
                IssueSeverity.Minor);
    }

    private static bool IsNotAnswering(char state) => state is Offline or PresentNotConnected;

    private void CheckUnregisteredEndpoints(List<NavDeviceBase> endpoints, string inventory, string letter, string label)
    {
        var registered = endpoints.Select(endpoint => endpoint.DeviceId).ToHashSet();
        var reportedNumbers = endpoints.Select(endpoint => endpoint.DeviceNumber).Where(number => number != 0).ToHashSet();
        var unclaimedOnline = DeviceNumbers(inventory, state => state == Online).Where(number => !reportedNumbers.Contains(number)).ToHashSet();
        var located = LocatedAddresses(letter, unclaimedOnline);
        var claimed = reportedNumbers.Union(located.Where(device => registered.Contains(device.Value)).Select(device => device.Key)).ToHashSet();
        var unregistered = UpdateUnregisteredDevices(inventory, letter, label, claimed);
        foreach (var number in unclaimedOnline.Where(number => !claimed.Contains(number)))
            CheckOnlineDevice(number, letter, label);
        CheckOfflineDevices(endpoints, inventory, letter, label, claimed.Union(unregistered).ToHashSet(), located.Values.ToHashSet());
    }

    private static IEnumerable<int> DeviceNumbers(string inventory, Func<char, bool> inState) =>
        Enumerable.Range(1, inventory.Length).Where(number => inState(inventory[number - 1]));

    private Dictionary<int, string> LocatedAddresses(string letter, HashSet<int> unclaimedOnline)
    {
        var located = new Dictionary<int, string>();
        lock (_addressLookupsLock)
        {
            foreach (var deviceId in _addressLookups.Keys.Where(deviceId => deviceId.EndsWith(letter, StringComparison.Ordinal)).ToList())
            {
                var number = int.Parse(deviceId[..^1]);
                if (!unclaimedOnline.Contains(number))
                    _addressLookups.Remove(deviceId);
                else if (_addressLookups[deviceId].Reply is { } reply && ReportedAddress(reply) is { } address)
                    located[number] = address;
            }
        }
        return located;
    }

    private HashSet<int> UpdateUnregisteredDevices(string inventory, string letter, string label, HashSet<int> claimed)
    {
        var unregistered = new HashSet<int>();
        foreach (var issue in GetOngoingIssues().Where(issue => issue.Key.StartsWith(UnregisteredIssuePrefix, StringComparison.Ordinal) &&
                                                               issue.Key.EndsWith(letter, StringComparison.Ordinal)))
        {
            var deviceId = issue.Key[UnregisteredIssuePrefix.Length..];
            var number = int.Parse(deviceId[..^1]);
            if (claimed.Contains(number) || number > inventory.Length || inventory[number - 1] == NotConfigured)
            {
                ResolveIssue(issue.Key);
                _unregisteredFindings.Remove(deviceId);
                continue;
            }
            RaiseUnregisteredIssue(deviceId, number, label, inventory[number - 1], _unregisteredFindings[deviceId]);
            unregistered.Add(number);
        }
        return unregistered;
    }

    private void CheckOnlineDevice(int number, string letter, string label)
    {
        var deviceId = $"{number:D4}{letter}";
        AddressLookup? lookup;
        lock (_addressLookupsLock)
            _addressLookups.TryGetValue(deviceId, out lookup);
        if (lookup == null)
        {
            AskForAddress(deviceId, _timeProvider.GetTimestamp());
            return;
        }
        if (lookup.Reply == null && _forwarded.IsAwaitingReply(command => command.DeviceId == deviceId))
            return;
        if (lookup.Reply is { } reply && ReportedAddress(reply) is { } address)
        {
            RaiseUnregisteredIssue(deviceId, number, label, Online, $"it reports IP {address} ({{{deviceId}}}:CI replied {reply})");
            return;
        }
        AskForAddress(deviceId, lookup.UnclaimedSince);
        if (_timeProvider.GetElapsedTime(lookup.UnclaimedSince) < NavDeviceBase.PollInterval)
            return;
        RaiseUnregisteredIssue(deviceId, number, label, Online, lookup.Reply == null
            ? $"IP unknown: no reply to {{{deviceId}}}:CI"
            : $"IP unknown: {{{deviceId}}}:CI replied {lookup.Reply}");
    }

    private void RaiseUnregisteredIssue(string deviceId, int number, string label, char state, string finding)
    {
        _unregisteredFindings[deviceId] = finding;
        RaiseOngoingIssue($"{UnregisteredIssuePrefix}{deviceId}",
            $"The Navigator has {label} {number} (inventory state {state}), which isn't registered to this module: {finding}",
            IssueSeverity.Minor);
    }

    private void AskForAddress(string deviceId, long unclaimedSince)
    {
        lock (_addressLookupsLock)
            _addressLookups[deviceId] = new AddressLookup(unclaimedSince, null);
        SendCommandToDevice(deviceId, AddressQuery, false);
    }

    private static string? ReportedAddress(string reply)
    {
        var octets = (reply.StartsWith(AddressTag, StringComparison.Ordinal) ? reply[AddressTag.Length..] : reply).Trim().Split('.');
        if (octets.Length != 4 || !octets.All(octet => byte.TryParse(octet, out _)))
            return null;
        return string.Join('.', octets.Select(byte.Parse));
    }

    private void CheckOfflineDevices(List<NavDeviceBase> endpoints, string inventory, string letter, string label,
        HashSet<int> accountedFor, HashSet<string> locatedAddresses)
    {
        var unclaimed = DeviceNumbers(inventory, IsNotAnswering).Where(number => !accountedFor.Contains(number)).ToList();
        var withoutNumber = endpoints.Where(endpoint => endpoint.DeviceNumber == 0 && !locatedAddresses.Contains(endpoint.DeviceId))
            .Select(endpoint => endpoint.DeviceId).ToList();
        var issueKey = $"{UnclaimedOfflineIssuePrefix}{letter}";
        if (unclaimed.Count <= withoutNumber.Count)
        {
            ResolveIssue(issueKey);
            return;
        }
        RaiseOngoingIssue(issueKey,
            $"The Navigator lists {unclaimed.Count} offline or not-connected {label}s that no registered {label} has claimed ({string.Join(", ", unclaimed)}), " +
            $"more than the {withoutNumber.Count} registered {label}s still without a device number ({(withoutNumber.Count == 0 ? "none" : string.Join(", ", withoutNumber))})",
            IssueSeverity.Minor);
    }

    private void HandleTieReportRow(string response)
    {
        var parts = response.Split('\t');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var outputNumber))
            return;
        CommunicationState = CommunicationState.Okay;
        var action = FindCallback($"{outputNumber:D4}o");
        if (action == null)
            return;
        action.Invoke($"In{ParseTieReportInput(parts[1])} Vid");
        action.Invoke($"In{ParseTieReportInput(parts[2])} Aud");
    }

    private static string ParseTieReportInput(string value) => value.All(c => c == '-') ? "0" : value;

    private NavDeviceBase? FindEndpoint(string endpointId)
    {
        if (endpointId.Length < 2 || !int.TryParse(endpointId[..^1], out var deviceNumber))
            return null;
        return endpointId[^1] switch
        {
            'i' => _inputs.FirstOrDefault(x => x.DeviceNumber == deviceNumber),
            'o' => _outputs.FirstOrDefault(x => x.DeviceNumber == deviceNumber),
            _ => null
        };
    }
    public override void RouteAV(int input, int output)
    {
        FindDecoder(output)?.UpdateDesiredRoute(input, input);
        CommunicationClient.Send($"{EscapeHeader}{input}*{output}!\r");
    }

    public override void RouteAudio(int input, int output)
    {
        FindDecoder(output)?.UpdateDesiredRoute(null, input);
        CommunicationClient.Send($"{EscapeHeader}{input}*{output}$\r");
    }

    public override void RouteVideo(int input, int output)
    {
        FindDecoder(output)?.UpdateDesiredRoute(input, null);
        CommunicationClient.Send($"{EscapeHeader}{input}*{output}%\r");
    }

    private NavDecoder? FindDecoder(int outputNumber) => _outputs.FirstOrDefault(x => x.DeviceNumber == outputNumber);

    public void RouteUsb(NavDeviceBase host, NavDeviceBase device)
    {
        using (PushProperties())
        {
            if (host.DeviceNumber == 0)
            {
                LogWarning("Not requesting a USB route from {HostName} as its device number is 0", host.Name);
                return;
            }
            if (device.DeviceNumber == 0)
            {
                LogWarning("Not requesting a USB route to {DeviceName} as its device number is 0", device.Name);
                return;
            }
            var inputString = $"{host.DeviceNumber}{host.GetLetterForDeviceType()}";
            var outputString = $"{device.DeviceNumber}{device.GetLetterForDeviceType()}";
            CommunicationClient.Send($"{EscapeHeader}{inputString}*{outputString}^\r");
        }
    }

    public void DerouteUsb(NavDeviceBase device)
    {
        using (PushProperties())
        {
            if (device.DeviceNumber == 0)
            {
                LogWarning("Not requesting a USB deroute of {DeviceName} as its device number is 0", device.Name);
                return;
            }
            var outputString = $"{device.DeviceNumber}{device.GetLetterForDeviceType()}";
            CommunicationClient.Send($"{EscapeHeader}0i*{outputString}^\r");
        }
    }

    public void DerouteAllUsb()
    {
        CommunicationClient.Send($"{EscapeHeader}0i*^\r");
    }


    public void SendCommandToDevice(string deviceId, string command) =>
        SendCommandToDevice(deviceId, command, Endpoints(true, true)
            .Any(endpoint => endpoint.DeviceId == deviceId && endpoint.DeviceConnectionState == ConnectionState.Connected));

    internal void SendCommandToDevice(string deviceId, string command, bool toAnsweringEndpoint)
    {
        var forwarded = new ForwardedCommand(deviceId, command, Interlocked.Increment(ref _forwardsSent), toAnsweringEndpoint);
        if (_forwarded.Add(forwarded) is { } dropped)
            using (PushProperties())
                LogWarning("Dropped {Command}: more than {Max} commands awaiting a reply", Printable(dropped.Command),
                    SentCommandTracker<ForwardedCommand>.MaxCommands);
        CommunicationClient.Send($"{{{deviceId}:{command}}}\r");
    }

    private static string Printable(ForwardedCommand command) => $"{{{command.DeviceId}:{ExtronSis.Printable(command.Command)}}}";

    private void ForwardDeviceResponse(string response)
    {
        using (PushProperties("ForwardDeviceResponse"))
        {
            var hostEndIndex = response.IndexOf('}');
            if (hostEndIndex == -1)
            {
                LogWarning("} was not found");
                return;
            }
            var respondant = response.Substring(0, hostEndIndex).Trim('{').Trim('}');
            var payload = response.Substring(hostEndIndex + 1, response.Length - hostEndIndex - 1);
            var forwarded = _forwarded.TakeReply(command => command.DeviceId == respondant) ?? TakeAddressReply(payload);
            if (forwarded != null)
                RecordRelayedReply(forwarded.Command, payload);
            using (Trigger.Resume(forwarded?.TriggerId))
            {
                var action = FindCallback(respondant);
                if (action != null)
                    action.Invoke(payload);
                else
                    LogVerbose("Navigator has returned a response for a device that's not registered to this module: {Respondant}", respondant);
            }
        }
    }

    private SentCommand<ForwardedCommand>? TakeAddressReply(string payload) =>
        ReportedAddress(payload) == null ? null : _forwarded.TakeReply(command => command.Command == AddressQuery);

    private void RecordRelayedReply(ForwardedCommand command, string payload)
    {
        lock (_relayLock)
        {
            _unansweredDeviceForwards = 0;
            _lastRelayedForward = Math.Max(_lastRelayedForward, command.Number);
        }
        ResolveIssue(RelayIssueKey);
        if (command.Command != AddressQuery)
            return;
        lock (_addressLookupsLock)
            if (_addressLookups.TryGetValue(command.DeviceId, out var lookup))
                _addressLookups[command.DeviceId] = lookup with { Reply = payload };
    }

    public virtual void RegisterDevice(string deviceId, Action<string> responseHandler)
    {
        lock (_callbacksLock)
            _callbacks[deviceId] = responseHandler;
    }

    public virtual void DeregisterDevice(string deviceId)
    {
        lock (_callbacksLock)
            _callbacks.Remove(deviceId);
    }

    private Action<string>? FindCallback(string deviceId)
    {
        lock (_callbacksLock)
            return _callbacks.GetValueOrDefault(deviceId);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
    public override int NumberOfOutputs { get => _outputs.Count; }
    public override int NumberOfInputs { get => _inputs.Count; }
    public override bool RequiresOutputSpecification { get => true; }
    public override bool SupportsVideoBreakaway { get => true; }
    public override bool SupportsAudioBreakaway { get => true; }

    public override List<SyncStatus> GetInputs() => [.._inputs];
    public override List<SyncStatus> GetOutputs() => [.._outputs];

    public void AddEndpoint(NavDeviceBase navDeviceBase)
    {
        lock (_endpointsLock)
        {
            switch (navDeviceBase.DeviceType)
            {
                case AVEndpointType.Encoder:
                    _inputs.Add((NavEncoder)navDeviceBase);
                    break;
                case AVEndpointType.Decoder:
                    _outputs.Add((NavDecoder)navDeviceBase);
                    break;
            }
        }
    }
}
