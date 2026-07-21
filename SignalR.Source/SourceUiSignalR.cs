using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Source;

public class SourceUiSignalR : DeviceBase
{
    private readonly SourceManager _sourceManager;
    private readonly IHubContext<SourceHub, ISourceHub> _hubContext;

    public SourceUiSignalR(SourceManager sourceManager, IHubContext<SourceHub, ISourceHub> hubContext)
        : base(sourceManager.Name, CommunicationClient.None)
    {
        _sourceManager = sourceManager;
        _sourceManager.OnSourceIndexChanged += OnSourceIndexChanged;
        _sourceManager.OnSourceListChanged += OnSourceListChanged;
        _hubContext = hubContext;
        SourceHub.RegisterSourceManager(Name, sourceManager);
    }

    private async void OnSourceListChanged(List<SourceDefinition> sources)
    {
        await _hubContext.Clients.Group(Name).UpdateSourceList(sources);
    }

    private async void OnSourceIndexChanged(int activeSource)
    {
        await _hubContext.Clients.Group(Name).UpdateSourceIndex(activeSource);
    }

    public override void PowerOn() => _sourceManager.PowerOn();

    public override void PowerOff() => _sourceManager.PowerOff();
}
