﻿using AVCoders.Core;

namespace AVCoders.Matrix;

public abstract class NavDeviceBase : AVoIPEndpoint
{
    public LogHandler? LogHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public CommunicationStateHandler? CommunicationStateHandlers;
    protected readonly ThreadWorker PollWorker;
    protected readonly Navigator Navigator;
    protected readonly string EscapeHeader = "\x1b";
    protected uint? _deviceNumber = null;
    protected string? SerialNumber = null;
    protected string? DeviceName = null;
    
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
            if (DeviceType == AVoIPDeviceType.Encoder)
                StreamAddress = DeviceNumber.ToString();
        }
    }


    public NavDeviceBase(string name, AVoIPDeviceType deviceType, string ipAddress, Navigator navigator) : 
        base(name, deviceType, navigator.SshClient)
    {
        Navigator = navigator;
        _deviceId = ipAddress;
        Navigator.RegisterDevice(ipAddress, PreHandleResponse);
        Navigator.SshClient.ConnectionStateHandlers += HandleConnectionState;
        PollWorker = new ThreadWorker(PrePoll, TimeSpan.FromSeconds(45));
        PollWorker.Restart();
    }

    private void HandleConnectionState(ConnectionState connectionstate)
    {
        if (connectionstate != ConnectionState.Connected)
            return;
        Random random = new Random();
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        Send($"{EscapeHeader}3CV\r");
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        PollWorker.Restart();
    }

    protected abstract Task Poll(CancellationToken token);

    private async Task PrePoll(CancellationToken token)
    {
        if (_deviceNumber == null)
            Send($"{EscapeHeader}DNUM\r");

        if (DeviceName == null)
            Send($"{EscapeHeader}CN\r");
        
        if (SerialNumber == null)
            Send("98I");
        
        await Poll(token);
    }

    protected abstract void HandleResponse(string response);

    private void PreHandleResponse(string payload)
    {
        if (payload.StartsWith("Dnum"))
        {
            DeviceNumber = uint.Parse(payload.Replace("Dnum", String.Empty));
            Navigator.RegisterDevice($"{DeviceNumber:D4}{GetLetterForDeviceType()}", HandleResponse);
            return;
        }

        if (payload.StartsWith("Inf98*"))
        {
            SerialNumber = payload.Replace("Inf98*", String.Empty);
            return;
        }

        if (payload.StartsWith("Ipn "))
        {
            DeviceName = payload.Replace("Ipn ", String.Empty);
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