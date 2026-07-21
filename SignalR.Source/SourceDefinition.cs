namespace AVCoders.SignalR.Source;

public record SourceDefinition(string Name, string Subtitle, string SourceId, string Icon)
{
    private bool _isConnected;
    private string _previewUrl = string.Empty;

    public event Action<bool>? OnConnectedChanged;
    public event Action<string>? OnPreviewUrlChanged;

    public bool IsConnected
    {
        get => _isConnected;
        protected set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            OnConnectedChanged?.Invoke(value);
        }
    }

    public string PreviewUrl
    {
        get => _previewUrl;
        protected set
        {
            if (_previewUrl == value) return;
            _previewUrl = value;
            OnPreviewUrlChanged?.Invoke(value);
        }
    }
}
