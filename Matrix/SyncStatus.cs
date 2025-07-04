using AVCoders.Core;

namespace AVCoders.Matrix;

public enum AVEndpointType
{
    Unnown,
    Encoder,
    Decoder
}

public delegate void SyncInfoHandler(ConnectionStatus status, string resolution, HdcpStatus hdcpStatus);

public enum ConnectionStatus
{
    Unknown,
    Connected,
    Disconnected,
}

public enum HdcpStatus
{
    Unknown,
    NotSupported,
    Available,
    Active,
}

public abstract class SyncStatus(string name, AVEndpointType type) : LogBase(name)
{
    private ConnectionStatus _inputConnectionStatus;
    private string _inputResolution = String.Empty;
    private HdcpStatus _inputHdcpStatus = HdcpStatus.Unknown;
    public SyncInfoHandler? InputStatusChangedHandlers;
    
    private ConnectionStatus _outputConnectionStatus;
    private string _outputResolution = String.Empty;
    private HdcpStatus _outputHdcpStatus = HdcpStatus.Unknown;
    public SyncInfoHandler? OutputStatusChangedHandlers;
    
    private string _streamAddress = String.Empty;
    public AddressChangeHandler? StreamChangeHandlers;
    
    public readonly AVEndpointType DeviceType = type;

    public ConnectionStatus InputConnectionStatus
    {
        get => _inputConnectionStatus;
        protected set
        {
            if (_inputConnectionStatus == value)
                return;
            _inputConnectionStatus = value;
            InputStatusChangedHandlers?.Invoke(_inputConnectionStatus, _inputResolution, _inputHdcpStatus);
        }
    }
    
    public string StreamAddress { 
        get => _streamAddress;
        protected set
        {
            _streamAddress = value;
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
            InputStatusChangedHandlers?.Invoke(_inputConnectionStatus, _inputResolution, _inputHdcpStatus);
        }
    }

    public ConnectionStatus OutputConnectionStatus
    {
        get => _outputConnectionStatus;
        protected set
        {
            if (_outputConnectionStatus == value)
                return;
            _outputConnectionStatus = value;
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
            OutputStatusChangedHandlers?.Invoke(_outputConnectionStatus, _outputResolution, _outputHdcpStatus);
        }
    }
}