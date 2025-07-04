using AVCoders.Core;

namespace AVCoders.Matrix;

public class NavCommunicationEmulator : CommunicationClient
{
    public NavCommunicationEmulator(string name) : base(name)
    {
        ConnectionState = ConnectionState.Disconnected;
    }

    public void SetConnectionState(ConnectionState state) { ConnectionState = state; }

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
    private uint? _deviceNumber = null;
    private string _hostname = String.Empty;
    protected string? SerialNumber = null;
    private int _unansweredRequests = 0;
    
    private readonly string _deviceId;
    private PowerState _powerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;

    public uint DeviceNumber
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


    public NavDeviceBase(string name, AVEndpointType deviceType, string ipAddress, Navigator navigator) : 
        base(name, deviceType, new NavCommunicationEmulator(GetCommunicationClientName(deviceType, name)))
    {
        Navigator = navigator;
        _deviceId = ipAddress;
        Navigator.RegisterDevice(ipAddress, PreHandleResponse);
        Navigator.SshClient.ConnectionStateHandlers += HandleNavConnectionState;
        PollWorker = new ThreadWorker(PrePoll, TimeSpan.FromSeconds(30));
        PollWorker.Restart();
    }

    public static string GetCommunicationClientName(AVEndpointType type, string name) => $"{name} {type.ToString()}";

    private void HandleNavConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        Send($"{EscapeHeader}3CV\r");
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        PollWorker.Restart();
    }

    protected abstract Task Poll(CancellationToken token);

    private async Task PrePoll(CancellationToken token)
    {
        _unansweredRequests++;
        if (_deviceNumber == null)
            Send($"{EscapeHeader}DNUM\r");

        if (Hostname == String.Empty)
            Send($"{EscapeHeader}CN\r");
        
        if (SerialNumber == null)
            Send("98I");
        
        await Poll(token);

        if (_unansweredRequests >= 3)
        {
            var client = (NavCommunicationEmulator)CommunicationClient;
            client.SetConnectionState(ConnectionState.Disconnected);
        }
    }

    protected abstract void HandleResponse(string response);

    private void PreHandleResponse(string payload)
    {
        if (payload.StartsWith("Dnum"))
        {
            DeviceNumber = uint.Parse(payload.Replace("Dnum", String.Empty));
            Navigator.RegisterDevice($"{DeviceNumber:D4}{GetLetterForDeviceType()}", PreHandleResponse);
        }

        else if (payload.StartsWith("Inf98*"))
            SerialNumber = payload.Replace("Inf98*", String.Empty);

        else if (payload.StartsWith("Ipn "))
            Hostname = payload.Replace("Ipn ", String.Empty);

        else if (payload.Contains('*') && !payload.Contains("Amt"))
            payload.Split('*').ToList().ForEach(ProcessConcatenatedResponse);

        else
            HandleResponse(payload);
        
        _unansweredRequests = 0;
        var client = (NavCommunicationEmulator)CommunicationClient;
        client.SetConnectionState(ConnectionState.Connected);
    }

    protected abstract void ProcessConcatenatedResponse(string response);

    private string GetLetterForDeviceType()
    {
        return DeviceType switch
        {
            AVEndpointType.Encoder => "i",
            AVEndpointType.Decoder => "o",
            _ => throw new ArgumentOutOfRangeException(nameof(DeviceType), DeviceType, null)
        };
    }

    protected void Send(string message) => Navigator.SendCommandToDevice(_deviceId, message);
    
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