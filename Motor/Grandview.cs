using AVCoders.Core;

namespace AVCoders.Motor;

public class Grandview : Motor
{
    private readonly RestComms _comms;

    public Grandview(string name, RestComms comms, RelayAction powerOnAction, int moveSeconds) 
        : base(name, powerOnAction, moveSeconds, comms)
    {
        _comms = comms;
        _comms.HttpResponseHandlers += HandleResponse;
        _comms.ConnectionStateHandlers += HandleConnectionState;
    }

    private void HandleResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            CommunicationState = CommunicationState.Okay;
            return;
        }
        using (PushProperties())
            LogWarning("{Uri} returned {StatusCode} {ReasonPhrase}", response.RequestMessage?.RequestUri, (int)response.StatusCode, response.ReasonPhrase);
        CommunicationState = CommunicationState.Error;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Error)
            return;
        CommunicationState = CommunicationState.Error;
    }

    public override void Raise()
    {
        _comms.Get(new Uri("close.js", UriKind.Relative));
    }

    public override void Lower()
    {
        _comms.Get(new Uri("open.js", UriKind.Relative));
    }

    public override void Stop()
    {
        _comms.Get(new Uri("stop.js", UriKind.Relative));
    }
}