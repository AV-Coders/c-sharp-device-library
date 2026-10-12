namespace AVCoders.Alarm;

public enum AreaState
{
    Unknown,
    Armed,
    Disarmed
}

public enum ZoneState
{
    Unknown,
    Sealed,
    Unsealed
}

public enum ZoneType
{
    Reed,
    Motion,
    Smoke,
    GlassBreak,
    Panic
}

public enum RelayState
{
    Unknown,
    Active,
    Inactive
}

public enum RelayType
{
    Door
}
