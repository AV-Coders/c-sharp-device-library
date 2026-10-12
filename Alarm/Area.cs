using AVCoders.Core;

namespace AVCoders.Alarm;

public abstract class Area : LogBase
{
    public event EventHandler<AreaState>? StateChanged;

    private AreaState _state = AreaState.Unknown;

    public AreaState State
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

    protected Area(string name) : base(name) { }

    public abstract void Arm(string pin);

    public abstract void Disarm(string pin);
}
