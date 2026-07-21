using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Destination;

public class DestinationUiSignalR : DeviceBase
{
    private readonly DestinationManager _destinationManager;
    private readonly IHubContext<DestinationHub, IDestinationHub> _hubContext;

    public DestinationUiSignalR(DestinationManager destinationManager,
        IHubContext<DestinationHub, IDestinationHub> hubContext)
        : base(destinationManager.Name, CommunicationClient.None)
    {
        _destinationManager = destinationManager;
        _hubContext = hubContext;
        DestinationHub.RegisterDestinationManager(Name, destinationManager);

        _destinationManager.OnDestinationChanged += OnDestinationChanged;
    }

    private async void OnDestinationChanged(DestinationDefinition destination)
    {
        await _hubContext.Clients.Group(Name).OnDestinationChanged(destination);
    }

    public override void PowerOn() => _destinationManager.PowerOn();

    public override void PowerOff() => _destinationManager.PowerOff();
}
