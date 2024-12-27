using AVCoders.Core;

namespace AVCoders.Matrix;

public enum AVoIPDeviceType
{
    Encoder,
    Decoder
};

public delegate void AddressChangeHandler(string streamAddress);

public abstract class AVoIPEndpoint : InputOutputStatus
{
    public readonly AVoIPDeviceType DeviceType;
    public readonly CommunicationClient CommunicationClient;
    public AddressChangeHandler? StreamChangeHandlers;
    public AddressChangeHandler? PreviewUrlChangeHandlers;
    private string _streamAddress;
    private string _previewUrl;
    public string StreamAddress { 
        get => _streamAddress;
        protected set
        {
            _streamAddress = value;
            StreamChangeHandlers?.Invoke(value);
        }
    }

    public string PreviewUrl
    {
        get => _previewUrl;
        protected set
        {
            _previewUrl = value;
            PreviewUrlChangeHandlers?.Invoke(value);
        }
    }


    protected AVoIPEndpoint(AVoIPDeviceType deviceType, CommunicationClient communicationClient)
    {
        DeviceType = deviceType;
        CommunicationClient = communicationClient;
        _streamAddress = String.Empty;
        PreviewUrl = String.Empty;
    }
}