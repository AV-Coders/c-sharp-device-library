namespace AVCoders.Climate;

public delegate void HvacModeHandler(HvacMode mode);

public enum HvacMode
{
    Unknown,
    Heat,
    Cool,
    Dry,
    FanOnly
}
