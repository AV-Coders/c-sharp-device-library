namespace AVCoders.SignalR.Destination;

public interface IDestinationHub
{
    Task OnDestinationChanged(DestinationDefinition destination);
}
