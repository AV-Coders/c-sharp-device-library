using AVCoders.Core;

namespace AVCoders.Matrix;

public enum AVEndpointType
{
    Unnown,
    Encoder,
    Decoder
}

public delegate void SyncInfoHandler(ConnectionState status, string resolution, HdcpStatus hdcpStatus);

public enum HdcpStatus
{
    Unknown,
    NotSupported,
    Available,
    Active,
}

public abstract class SyncStatus(string name, AVEndpointType type) : LogBase(name)
{
    private ConnectionState _deviceConnectionState = ConnectionState.Unknown;
    private ConnectionState _inputConnectionStatus;
    private string _inputResolution = string.Empty;
    private HdcpStatus _inputHdcpStatus = HdcpStatus.Unknown;
    public SyncInfoHandler? InputStatusChangedHandlers;
    
    private ConnectionState _outputConnectionStatus;
    private string _outputResolution = string.Empty;
    private HdcpStatus _outputHdcpStatus = HdcpStatus.Unknown;
    public SyncInfoHandler? OutputStatusChangedHandlers;
    
    private string _streamAddress = string.Empty;
    public AddressChangeHandler? StreamChangeHandlers;
    
    public readonly AVEndpointType DeviceType = type;
    
    public ConnectionState DeviceConnectionState
    {
        get => _deviceConnectionState;
        protected set
        {
            if (_deviceConnectionState == value)
                return;
            _deviceConnectionState = value;
            AddEvent(EventType.Connection, $"Device Connection Changed to {_deviceConnectionState}");
        }
    }

    public ConnectionState InputConnectionStatus
    {
        get => _inputConnectionStatus;
        protected set
        {
            if (_inputConnectionStatus == value)
                return;
            _inputConnectionStatus = value;
            AddEvent(EventType.Connection, $"Input Connection Changed to {_inputConnectionStatus}");
            InputStatusChangedHandlers?.Invoke(_inputConnectionStatus, _inputResolution, _inputHdcpStatus);
        }
    }
    
    public string StreamAddress { 
        get => _streamAddress;
        protected set
        {
            _streamAddress = value;
            AddEvent(EventType.Other, $"Stream Address Changed to {_streamAddress}");
            StreamChangeHandlers?.Invoke(value);
        }
    }

    public string InputResolution
    {
        get => _inputResolution;
        protected set
        {
            if(_inputResolution == value)
                return;
            _inputResolution = value;
            AddEvent(EventType.Other, $"Input Resolution Changed to {_inputResolution}");
            InputStatusChangedHandlers?.Invoke(_inputConnectionStatus, _inputResolution, _inputHdcpStatus);
        }
    }

    public ConnectionState OutputConnectionStatus
    {
        get => _outputConnectionStatus;
        protected set
        {
            if (_outputConnectionStatus == value)
                return;
            _outputConnectionStatus = value;
            AddEvent(EventType.Connection, $"Output Connection Changed to {_outputConnectionStatus}");
            OutputStatusChangedHandlers?.Invoke(_outputConnectionStatus, _outputResolution, _outputHdcpStatus);
        }
    }

    public string OutputResolution
    {
        get => _outputResolution;
        protected set
        {
            if(_outputResolution == value)
                return;
            _outputResolution = value;
            AddEvent(EventType.Other, $"Output Resolution Changed to {_outputResolution}");
            OutputStatusChangedHandlers?.Invoke(_outputConnectionStatus, _outputResolution, _outputHdcpStatus);
        }
    }
    
    public HdcpStatus InputHdcpStatus
    {
        get => _inputHdcpStatus;
        protected set
        {
            if (_inputHdcpStatus == value)
                return;
            _inputHdcpStatus = value;
            AddEvent(EventType.Other, $"Input Hdcp Changed to {_inputHdcpStatus}");
            InputStatusChangedHandlers?.Invoke(_inputConnectionStatus, _inputResolution, _inputHdcpStatus);
        }
    }

    public HdcpStatus OutputHdcpStatus
    {
        get => _outputHdcpStatus;
        protected set
        {
            if (_outputHdcpStatus == value)
                return;
            _outputHdcpStatus = value;
            AddEvent(EventType.Other, $"Output Hdcp Changed to {_outputHdcpStatus}");
            OutputStatusChangedHandlers?.Invoke(_outputConnectionStatus, _outputResolution, _outputHdcpStatus);
        }
    }
}