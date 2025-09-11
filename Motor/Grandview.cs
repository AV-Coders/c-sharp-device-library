using AVCoders.Core;

namespace AVCoders.Motor;

public class Grandview : Motor
{
    private readonly RestComms _comms;

    public Grandview(string name, RestComms comms, RelayAction powerOnAction, int moveSeconds) 
        : base(name, powerOnAction, moveSeconds)
    {
        _comms = comms;
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