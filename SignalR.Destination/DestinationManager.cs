using AVCoders.Core;
using AVCoders.SignalR.Source;

namespace AVCoders.SignalR.Destination;

public class DestinationManager : DeviceBase
{
    private SourceDefinition _currentSource;
    public string DestinationId { get; }
    public string Icon { get; }
    public bool VideoMute { get; protected set; }

    public event Action<DestinationDefinition>? OnDestinationChanged;
    public event Action<SourceDefinition>? OnSourceChanged; 

    private readonly SourceManager _sourceManager;
    private readonly string _defaultSource;
    private readonly string _offSource;

    public SourceDefinition CurrentSource
    {
        get => _currentSource;
        protected set
        {
            if (_currentSource == value)
                return;
            _currentSource = value;
            OnSourceChanged?.Invoke(value);
        }
    }

    public DestinationDefinition Snapshot => new(Name, DestinationId, Icon, CurrentSource, VideoMute);

    public DestinationManager(string name, string destinationId, string icon, SourceManager sourceManager,
        string defaultSource = "None", string offSource = "None")
        : base(name, CommunicationClient.None)
    {
        DestinationId = destinationId;
        Icon = icon;
        _sourceManager = sourceManager;
        _defaultSource = defaultSource;
        _offSource = offSource;
        _currentSource = _sourceManager.Sources.First(s => s.SourceId == offSource);
    }

    public void RouteSource(string sourceId)
    {
        using (PushProperties("RouteSource"))
        {
            try
            {
                var source = _sourceManager.Sources.FirstOrDefault(s => s.SourceId == sourceId);
                if (source is null)
                {
                    LogWarning("Source {SourceId} not in source list, ignoring route on destination {Name}", sourceId, Name);
                    return;
                }
                LogDebug("Routing source {SourceId} to destination {Name}", sourceId, Name);
                CurrentSource = source;
                PowerState = sourceId == "None" ? PowerState.Off : PowerState.On;
                OnDestinationChanged?.Invoke(Snapshot);
            }
            catch (Exception e)
            {
                LogException(e, $"There was an error routing source {sourceId} to destination {Name}");
            }
        }
    }

    public void SetVideoMute(bool muted)
    {
        using (PushProperties("SetVideoMute"))
        {
            VideoMute = muted;
            LogDebug("Video mute on destination {Name} set to {Muted}", Name, muted);
            OnDestinationChanged?.Invoke(Snapshot);
        }
    }

    public override void PowerOn() => RouteSource(_defaultSource);

    public override void PowerOff() => RouteSource(_offSource);
}
