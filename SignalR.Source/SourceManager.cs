using AVCoders.Core;
using Serilog;

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
        }
    }

    private void HandleSourceConnectedChanged(bool _) => OnSourceListChanged?.Invoke(Sources);

    public void SetCurrentSource(string sourceName)
    {
        using (PushProperties("SetCurrentSource - by name"))
        {
            try
            {
                SetCurrentSource(_sources.FindIndex(source => source.Name == sourceName));
            }
            catch (Exception e)
            {
                LogException(e, $"There was an error setting the source to name {sourceName}.");
            }
        }
    }

    public void SetCurrentSource(int sourceIndex)
    {
        using (PushProperties("SetCurrentSource - by index"))
        {
            try
            {
                _currentSourceIndex = sourceIndex;
                Log.Debug($"Setting source to {_sources[sourceIndex].Name}, index {sourceIndex}");
                CurrentSource = _sources[sourceIndex].SourceId;
                OnSourceIndexChanged?.Invoke(sourceIndex);
                OnSourceChanged?.Invoke(CurrentSource);
                PowerState = CurrentSource == "None" ? PowerState.Off : PowerState.On;
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
