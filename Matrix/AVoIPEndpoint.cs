namespace AVCoders.Matrix;

public enum AVoIPDeviceType
{
    Encoder,
    Decoder
};

public delegate StreamChangeHandler StreamChangeHandler(string streamAddress);

public abstract class AVoIPEndpoint : InputOutputStatus
{
    public readonly AVoIPDeviceType DeviceType;
    public StreamChangeHandler? StreamChangeHandlers;
    private string _streamAddress;
    public string StreamAddress { 
        get => _streamAddress;
        protected set
        {
            _streamAddress = value;
            StreamChangeHandlers?.Invoke(value);
        }
    }
    public string PreviewUrl { get; protected set; }


    protected AVoIPEndpoint(AVoIPDeviceType deviceType)
    {
        DeviceType = deviceType;
        
    }
}