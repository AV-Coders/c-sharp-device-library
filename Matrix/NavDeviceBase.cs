using AVCoders.Core;

namespace AVCoders.Matrix;

public class NavigatorTunnel : CommunicationClient
{
    private readonly CommunicationClient? _navigatorClient;

    public NavigatorTunnel(string name, string host) : this(name, host, null)
    {
    }

    internal NavigatorTunnel(string name, string host, TimeProvider? timeProvider = null)
        : base(name, host, 0, CommandStringFormat.Ascii, timeProvider ?? TimeProvider.System)
    {
        ConnectionState = ConnectionState.Disconnected;
    }

    internal NavigatorTunnel(string name, string host, CommunicationClient navigatorClient, TimeProvider? timeProvider)
        : this(name, host, timeProvider)
    {
        _navigatorClient = navigatorClient;
        navigatorClient.ConnectionStateHandlers += _ => ExplainNotConnected();
        navigatorClient.ConnectionReasonHandlers += _ => ExplainNotConnected();
    }

    private string NotResponding => $"{Name} at {Host} is not responding via the Navigator tunnel";

    public void SetConnectionState(ConnectionState state)
    {
        // NavDeviceBase calls this every failing poll cycle. Report before the deduping
        // ConnectionState setter so repeated failures keep feeding the momentary issue and
        // the ConnectionIssueThreshold escalation; the Connected transition resolves it.
        if (state is ConnectionState.Disconnected or ConnectionState.Error)
            ReportConnectionFailure(ConnectionReasonCategory.TimedOut, NotResponding);
        ConnectionState = state;
        ExplainNotConnected();
    }

    private void ExplainNotConnected()
    {
        if (_navigatorClient == null || ConnectionState == ConnectionState.Connected)
            return;
        if (_navigatorClient.ConnectionState == ConnectionState.Connected)
            SetConnectionReason(ConnectionReasonCategory.TimedOut, NotResponding);
        else
            MirrorConnectionReason(_navigatorClient.ConnectionReason);
    }

    public override void Send(string message) { }

    public override void Send(byte[] bytes) { }
}

public abstract class NavDeviceBase : AVoIPEndpoint
{
    public PowerStateHandler? PowerStateHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    protected readonly ThreadWorker PollWorker;
    protected readonly Navigator Navigator;
    protected readonly string EscapeHeader = "\x1b";
    private const string SerialLabel = "Serial";
    private const string HostnameLabel = "Hostname";
    private const string AudioInputTag = "AudI";
    private const string AudioInputLabel = "Audio Input";
    private const string StreamInputTag = "StrmI";
    private const string StreamInputLabel = "Stream Input";
    private const string LinkTag = "Lnk";
    private const string LinkLabel = "Link";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(90);
    private const string VerboseModeTag = "Vrb";
    private static readonly TimeSpan VerboseModeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NavConnectedSettle = TimeSpan.FromSeconds(3);
    private readonly TimeProvider _timeProvider;
    private readonly object _spacedSendsLock = new();
    private Task _spacedSends = Task.CompletedTask;
    private TaskCompletionSource _verboseModeSet = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _deviceNumber = null;
    private string _hostname = string.Empty;
    protected string? SerialNumber = null;
    private int _unansweredRequests = 0;
    
    private readonly string _deviceId;
    private PowerState _powerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;

    public int DeviceNumber
    {
        get => _deviceNumber ?? 0;
        protected set
        {
            if (value == _deviceNumber)
                return;
            _deviceNumber = value;
            if (DeviceType == AVEndpointType.Encoder)
                StreamAddress = DeviceNumber.ToString();
        }
    }

    public string Hostname
    {
        get => _hostname;
        protected set => _hostname = value;
    }

    public NavDeviceBase(string name, AVEndpointType deviceType, string ipAddress, Navigator navigator)
        : this(name, deviceType, ipAddress, navigator, null)
    {
    }

    internal NavDeviceBase(string name, AVEndpointType deviceType, string ipAddress, Navigator navigator, TimeProvider? timeProvider = null) :
        base(name, deviceType, new NavigatorTunnel(GetCommunicationClientName(deviceType, name), ipAddress, navigator.SshClient, timeProvider))
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Navigator = navigator;
        Navigator.AddEndpoint(this);
        _deviceId = ipAddress;
        Navigator.RegisterDevice(ipAddress, PreHandleResponse);
        Navigator.SshClient.ConnectionStateHandlers += HandleNavConnectionState;
        CommunicationClient.ConnectionStateHandlers += x => DeviceConnectionState = x;
        DeviceConnectionState = CommunicationClient.ConnectionState;
        PollWorker = new ThreadWorker(PrePoll, PollInterval, false, _timeProvider);
        PollWorker.Restart();
    }

    public static string GetCommunicationClientName(AVEndpointType type, string name) => $"Nav {type.ToString()} - {name}";

    internal string DeviceId => _deviceId;

    internal void HandleMissedReply(string command, string error)
    {
        using (PushProperties())
            LogDebug("No reply to {Command}: the Navigator answered {Error} while its inventory lists offline endpoints",
                ExtronSis.Printable(command), error);
    }

    private void HandleNavConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(_ => InitialiseAfterNavConnect(), TaskScheduler.Default).Unwrap();
    }

    private async Task InitialiseAfterNavConnect()
    {
        try
        {
            await Task.Delay(NavConnectedSettle, _timeProvider);
            var verboseModeSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _verboseModeSet = verboseModeSet;
            Send($"{EscapeHeader}3CV\r");
            await Task.WhenAny(verboseModeSet.Task, Task.Delay(VerboseModeTimeout, _timeProvider));
            await PollWorker.Restart();
        }
        catch (Exception e)
        {
            LogException(e, "Initialising the endpoint after the Navigator connected failed");
        }
    }

    protected abstract Task Poll(CancellationToken token);

    private async Task PrePoll(CancellationToken token)
    {
        if (Navigator.SshClient.DesiredConnectionState == ConnectionState.Disconnected)
            return;
        _unansweredRequests++;
        if (_deviceNumber == null)
            Send($"{EscapeHeader}DNUM\r");

        if (Hostname == string.Empty)
            Send($"{EscapeHeader}CN\r");
        
        if (SerialNumber == null)
            Send("98I");
        
        await Poll(token);

        if (_unansweredRequests >= 3)
        {
            var client = (NavigatorTunnel)CommunicationClient;
            client.SetConnectionState(ConnectionState.Disconnected);
        }
    }

    protected abstract void HandleResponse(string response);

    private void PreHandleResponse(string payload)
    {
        if (payload.StartsWith("Dnum"))
        {
            var previousDeviceNumber = _deviceNumber;
            DeviceNumber = int.Parse(payload.Replace("Dnum", string.Empty));
            if (previousDeviceNumber != null && previousDeviceNumber != DeviceNumber)
                Navigator.DeregisterDevice(GetNavigatorDeviceId(previousDeviceNumber.Value));
            Navigator.RegisterDevice(GetNavigatorDeviceId(DeviceNumber), PreHandleResponse);
            Navigator.QueryEndpointState(this);
        }

        else if (payload.StartsWith("Inf98*"))
        {
            SerialNumber = payload.Replace("Inf98*", string.Empty);
            SetDetail(SerialLabel, SerialNumber);
        }

        else if (payload.StartsWith("Ipn "))
        {
            Hostname = payload.Replace("Ipn ", string.Empty);
            SetDetail(HostnameLabel, Hostname);
        }

        else if (payload.StartsWith(VerboseModeTag))
            _verboseModeSet.TrySetResult();

        else if (payload.Contains('*') && !payload.Contains("Amt"))
            payload.Split('*').ToList().ForEach(ProcessStatusField);

        else
            HandleResponse(payload);
        
        _unansweredRequests = 0;
        var client = (NavigatorTunnel)CommunicationClient;
        client.SetConnectionState(ConnectionState.Connected);
    }

    private void ProcessStatusField(string field)
    {
        if (field.StartsWith(AudioInputTag))
            SetDetail(AudioInputLabel, field[AudioInputTag.Length..]);
        else if (field.StartsWith(StreamInputTag))
            SetDetail(StreamInputLabel, field[StreamInputTag.Length..]);
        else if (field.StartsWith(LinkTag))
            SetDetail(LinkLabel, field[LinkTag.Length..]);
        else
            ProcessConcatenatedResponse(field);
    }

    protected abstract void ProcessConcatenatedResponse(string response);

    protected static HdcpStatus ParseSourceHdcpStatus(string value) => value switch
    {
        "2" => HdcpStatus.Active,
        "1" => HdcpStatus.Inactive,
        "0" => HdcpStatus.Unknown,
        _ => HdcpStatus.Unknown
    };

    protected static HdcpStatus ParseSinkHdcpStatus(string value) => value switch
    {
        "2" => HdcpStatus.Available,
        "1" => HdcpStatus.NotSupported,
        "0" => HdcpStatus.Unknown,
        _ => HdcpStatus.Unknown
    };

    public string GetLetterForDeviceType()
    {
        return DeviceType switch
        {
            AVEndpointType.Encoder => "i",
            AVEndpointType.Decoder => "o",
            _ => throw new ArgumentOutOfRangeException(nameof(DeviceType), DeviceType, null)
        };
    }

    private string GetNavigatorDeviceId(int deviceNumber) => $"{deviceNumber:D4}{GetLetterForDeviceType()}";

    protected void Send(string message) =>
        Navigator.SendCommandToDevice(_deviceId, message, DeviceConnectionState == ConnectionState.Connected);
    
    public void PowerOn() { }

    public void PowerOff() { }
    
    public PowerState PowerState
    {
        get => _powerState;
        protected set
        {
            if (value == _powerState)
                return;
            _powerState = value;
            PowerStateHandlers?.Invoke(PowerState);
        }
    }

    public CommunicationState CommunicationState
    {
        get => _communicationState;
        protected set
        {
            if (value == _communicationState)
                return;
            
            _communicationState = value;
            CommunicationStateHandlers?.Invoke(CommunicationState);
        }
    }
}