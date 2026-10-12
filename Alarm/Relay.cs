using AVCoders.Core;

namespace AVCoders.Alarm;

public abstract class Relay : LogBase
{
    public readonly RelayType Type;
    public event EventHandler<RelayState>? StateChanged;

    private RelayState _state = RelayState.Unknown;

    public RelayState State
    {
        get => _state;
        protected set
        {
            if (value == _state)
                return;
            _state = value;
            AddEvent(EventType.DriverState, value.ToString());
            StateChanged?.Invoke(this, value);
        }
    }

    protected Relay(string name, RelayType type) : base(name)
    {
        Type = type;
    }

    public abstract void Activate();

    public abstract void Deactivate();

    public abstract void Activate(TimeSpan duration);
}
