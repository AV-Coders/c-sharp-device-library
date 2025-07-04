namespace AVCoders.Matrix;

public class ExtronMatrixInput(string name, int inputNumber)
    : ExtronMatrixEndpoint(name, inputNumber, AVEndpointType.Encoder)
{
    public void SetName(string name) => Name = name;
}

public class ExtronMatrixEndpoint : SyncStatus
{
    private readonly int _number;
    public bool InUse { get; protected set; }

    public ExtronMatrixEndpoint(string name, int number, AVEndpointType type) : base(name, type)
    {
        _number = number;
    }
    public void SetInputStatus(ConnectionStatus status)
    {
        InUse = true;
        InputConnectionStatus = status;
    }

    public void SetOutputStatus(ConnectionStatus connectionStatus)
    {
        InUse = true;
        OutputConnectionStatus = connectionStatus;
    }
    
    public void SetOutputHdcpStatus(HdcpStatus status)
    {
        InUse = true;
        OutputHdcpStatus = status;
    }

    public void SetName(string name) => Name = name;
}


public class ExtronMatrixOutput(string name, int number)
{
    public readonly ExtronMatrixEndpoint Primary = new(name, number, AVEndpointType.Decoder);
    public readonly ExtronMatrixEndpoint Secondary = new(name, number, AVEndpointType.Decoder);

    public void SetName(string name)
    {
        Primary.SetName(name);
        Secondary.SetName($"{name} - B");
    }
}