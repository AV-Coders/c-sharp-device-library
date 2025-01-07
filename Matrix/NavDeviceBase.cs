using AVCoders.Core;

namespace AVCoders.Matrix;

public enum NavManualPollItem
{
    None,
    DeviceNumber,
    SerialNumber,
    DeviceName
}

public abstract class NavDeviceBase : AVoIPEndpoint
{
    public LogHandler? LogHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    protected readonly ThreadWorker PollWorker;
    protected readonly Navigator Navigator;
    protected readonly string EscapeHeader = "\x1b";
    protected int? DeviceNumber = null;
    protected string? SerialNumber = null;
    protected string? DeviceName = null;
    private NavManualPollItem _currentPollItem = NavManualPollItem.None;
    
    private readonly string _deviceId;
    private PowerState _powerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;


    public NavDeviceBase(string name, AVoIPDeviceType deviceType, string ipAddress, Navigator navigator) : 
        base(name, deviceType, navigator.SshClient)
    {
        Navigator = navigator;
        _deviceId = ipAddress;
        Navigator.RegisterDevice(ipAddress, PreHandleResponse);
        Navigator.SshClient.ConnectionStateHandlers += HandleConnectionState;
        PollWorker = new ThreadWorker(PrePoll, TimeSpan.FromSeconds(60), true);
        PollWorker.Restart();
    }

    private void HandleConnectionState(ConnectionState connectionstate)
    {
        if (connectionstate != ConnectionState.Connected)
            return;
        Random random = new Random();
        int waitDelay = random.Next(2, 20 + 1) * 200;
        Thread.Sleep(TimeSpan.FromMilliseconds(waitDelay));
        Send($"{EscapeHeader}3CV\r");
    }

    protected abstract Task Poll(CancellationToken token);

    private Task PrePoll(CancellationToken token)
    {
        if (DeviceNumber == null)
        {
            _currentPollItem = NavManualPollItem.DeviceNumber;
            Send($"{EscapeHeader}DNUM\r");
            return Task.CompletedTask;
        }
        if (SerialNumber == null)
        {
            _currentPollItem = NavManualPollItem.SerialNumber;
            Send("98I");
            return Task.CompletedTask;
        }

        if (DeviceName == null)
        {
            _currentPollItem = NavManualPollItem.DeviceName;
            Send($"{EscapeHeader}CN\r");
            return Task.CompletedTask;
        }
        
        Poll(token);
        return Task.CompletedTask;
    }

    protected abstract void HandleResponse(string response);

    private void PreHandleResponse(string payload)
    {
        switch (_currentPollItem)
        {
            case NavManualPollItem.DeviceNumber when payload.StartsWith("Dnum"):
                DeviceNumber = int.Parse(payload.Replace("Dnum", String.Empty));
                _currentPollItem = NavManualPollItem.None;
                Navigator.RegisterDevice($"{DeviceNumber:D4}{GetLetterForDeviceType()}", HandleResponse);
                if (DeviceType == AVoIPDeviceType.Encoder)
                    StreamAddress = DeviceNumber.ToString() ?? string.Empty;
                return;
            case NavManualPollItem.SerialNumber when payload.StartsWith("Inf98*"):
                SerialNumber = payload.Replace("Inf98*", String.Empty);
                _currentPollItem = NavManualPollItem.None;
                return;
            
            case NavManualPollItem.DeviceName when payload.StartsWith("Ipn "):
                DeviceName = payload.Replace("Ipn ", String.Empty);
                _currentPollItem = NavManualPollItem.None;
                return;
        }

        if (payload.Contains('*'))
        {
            payload.Split('*').ToList().ForEach(ProcessConcatenatedResponse);
            return;
        }
        HandleResponse(payload);
    }

    protected abstract void ProcessConcatenatedResponse(string response);

    private string GetLetterForDeviceType()
    {
        return DeviceType switch
        {
            AVoIPDeviceType.Encoder => "i",
            AVoIPDeviceType.Decoder => "o",
            _ => throw new ArgumentOutOfRangeException(nameof(DeviceType), DeviceType, null)
        };
    }

    protected void Send(string message) => Navigator.SendCommandToDevice(_deviceId, message);
    
    protected void Log(string message) => LogHandlers?.Invoke($"{GetType()} - {Name} - {message}");

    protected void Error(string message) => LogHandlers?.Invoke($"{GetType()} - {Name} - {message}", EventLevel.Error);
    
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