using AVCoders.Core;

namespace AVCoders.Alarm;

public abstract class Zone : LogBase
{
    public readonly ZoneType Type;
    public event EventHandler<ZoneState>? StateChanged;
    public event EventHandler<bool>? IsolatedChanged;
    public event EventHandler<bool>? TamperedChanged;

    private ZoneState _state = ZoneState.Unknown;
    private bool _isolated;
    private bool _tampered;

    public ZoneState State
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

    public bool Isolated
    {
        get => _isolated;
        protected set
        {
            if (value == _isolated)
                return;
            _isolated = value;
            AddEvent(EventType.DriverState, value ? "Isolated" : "Not isolated");
            IsolatedChanged?.Invoke(this, value);
        }
    }

    public bool Tampered
    {
        get => _tampered;
        protected set
        {
            if (value == _tampered)
                return;
            _tampered = value;
            AddEvent(EventType.DriverState, value ? "Tampered" : "Not tampered");
            TamperedChanged?.Invoke(this, value);
        }
    }

    protected Zone(string name, ZoneType type) : base(name)
    {
        Type = type;
    }
}
