using AVCoders.Core;

namespace AVCoders.Matrix;

public delegate void SyncInfoHandler(ConnectionStatus status, string resolution);

public enum ConnectionStatus
{
    Connected,
    Disconnected,
}

public abstract class SyncStatus : LogBase
{
    private ConnectionStatus _inputConnectionStatus;
    private string _inputResolution = String.Empty;
    private ConnectionStatus _outputConnectionStatus;
    private string _outputResolution = String.Empty;
    public SyncInfoHandler? InputStatusChangedHandlers;
    public SyncInfoHandler? OutputStatusChangedHandlers;

    protected SyncStatus(string name) : base(name)
    {
    }

    public ConnectionStatus InputConnectionStatus
    {
        get => _inputConnectionStatus;
        protected set
        {
            if (_inputConnectionStatus == value)
                return;
            _inputConnectionStatus = value;
            InputStatusChangedHandlers?.Invoke(_inputConnectionStatus, _inputResolution);
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
            InputStatusChangedHandlers?.Invoke(_inputConnectionStatus, _inputResolution);
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
            OutputStatusChangedHandlers?.Invoke(_outputConnectionStatus, _outputResolution);
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
            OutputStatusChangedHandlers?.Invoke(_outputConnectionStatus, _outputResolution);
        }
    }
}