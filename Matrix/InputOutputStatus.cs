namespace AVCoders.Matrix;

public delegate InputStatusChangedHandler InputStatusChangedHandler(uint inputNumber, ConnectionStatus status, string resolution);
public delegate OutputStatusChangedHandler OutputStatusChangedHandler(uint outputNumber, ConnectionStatus status, string resolution);

public enum ConnectionStatus
{
    Connected,
    Disconnected,
}

public abstract class InputOutputStatus
{
    private ConnectionStatus _inputConnectionStatus;
    private string _inputResolution = String.Empty;
    private ConnectionStatus _outputConnectionStatus;
    private string _outputResolution = String.Empty;
    public InputStatusChangedHandler? InputStatusChangedHandlers;
    public OutputStatusChangedHandler? OutputStatusChangedHandlers;

    protected void UpdateInputStatus(ConnectionStatus status, string resolution, uint inputNumber = 1)
    {
        if (_inputConnectionStatus != status || _inputResolution != resolution)
        {
            InputStatusChangedHandlers?.Invoke(inputNumber, status, resolution);
        }
        _inputConnectionStatus = status;
        _inputResolution = resolution;
    }

    protected void UpdateOutputStatus(ConnectionStatus status, string resolution, uint outputNumber = 1)
    {
        
        if (_outputConnectionStatus != status || _outputResolution != resolution)
        {
            OutputStatusChangedHandlers?.Invoke(outputNumber, status, resolution);
        }
        _outputConnectionStatus = status;
        _outputResolution = resolution;
    }
}