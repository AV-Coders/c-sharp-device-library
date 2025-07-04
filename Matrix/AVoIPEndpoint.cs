using AVCoders.Core;

namespace AVCoders.Matrix;

public delegate void AddressChangeHandler(string streamAddress);

public abstract class AVoIPEndpoint(string name, AVEndpointType deviceType, CommunicationClient communicationClient)
    : SyncStatus(name, deviceType)
{
    public readonly CommunicationClient CommunicationClient = communicationClient;
    public AddressChangeHandler? PreviewUrlChangeHandlers;
    private string _previewUrl = String.Empty;

    public string PreviewUrl
    {
        get => _previewUrl;
        protected set
        {
            _previewUrl = value;
            PreviewUrlChangeHandlers?.Invoke(value);
        }
    }
}