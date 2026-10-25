using AVCoders.Core;

namespace AVCoders.SignalR.Source;

public class SourceManager : DeviceBase
{
    public string CurrentSource { get; protected set; } = "None";
    public event Action<string>? OnSourceChanged;
    public event Action<int>? OnSourceIndexChanged;
    public event Action<List<SourceDefinition>>? OnSourceListChanged;

    protected readonly List<SourceDefinition> _sources;
    protected readonly string _defaultSource;
    private readonly string _offSource;
    protected int _currentSourceIndex;

    public List<SourceDefinition> Sources => [.._sources];


    public SourceManager(string name, List<SourceDefinition> sources, string defaultSource = "None", string offSource = "None")
        : base(name, CommunicationClient.None)
    {
        _defaultSource = defaultSource;
        _offSource = offSource;
        _sources = sources;
        foreach (var source in _sources)
        {
            source.OnConnectedChanged += HandleSourceConnectedChanged;
            source.OnPreviewUrlChanged += HandleSourcePreviewUrlChanged;
        }
    }

    private void HandleSourceConnectedChanged(bool _) => OnSourceListChanged?.Invoke(Sources);

    private void HandleSourcePreviewUrlChanged(string _) => OnSourceListChanged?.Invoke(Sources);

    public void SetCurrentSource(string sourceName)
    {
        using (PushProperties("SetCurrentSource - by name"))
        {
            var sourceIndex = _sources.FindIndex(source => source.Name == sourceName);
            if (sourceIndex < 0)
            {
                LogWarning("Source {SourceName} not in source list, ignoring selection on {Name}", sourceName, Name);
                return;
            }
            SetCurrentSource(sourceIndex);
        }
    }

    public void SetCurrentSource(int sourceIndex)
    {
        using (PushProperties("SetCurrentSource - by index"))
        {
            if (sourceIndex < 0 || sourceIndex >= _sources.Count)
            {
                LogWarning("Source index {SourceIndex} is outside the {SourceCount} sources, ignoring selection on {Name}",
                    sourceIndex, _sources.Count, Name);
                return;
            }
            try
            {
                _currentSourceIndex = sourceIndex;
                var source = _sources[sourceIndex];
                LogDebug("Setting source to {SourceName}, index {SourceIndex}", source.Name, sourceIndex);
                CurrentSource = source.SourceId;
                OnSourceIndexChanged?.Invoke(sourceIndex);
                OnSourceChanged?.Invoke(CurrentSource);
                PowerState = source.Name == _offSource ? PowerState.Off : PowerState.On;
            }
            catch (Exception e)
            {
                LogException(e, $"There was an error setting the source to index {sourceIndex}");
            }
        }
    }


    public override void PowerOn() => SetCurrentSource(_defaultSource);

    public override void PowerOff() => SetCurrentSource(_offSource);
}
