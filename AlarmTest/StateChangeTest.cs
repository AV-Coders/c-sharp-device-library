namespace AVCoders.Alarm.Tests;

public class StateChangeTest
{
    private class TestArea() : Area("Area")
    {
        public void SetState(AreaState state) => State = state;
        public override void Arm(string pin) { }
        public override void Disarm(string pin) { }
    }

    private class TestZone() : Zone("Zone", ZoneType.Motion)
    {
        public void SetState(ZoneState state) => State = state;
        public void SetIsolated(bool isolated) => Isolated = isolated;
        public void SetTampered(bool tampered) => Tampered = tampered;
    }

    private class TestRelay() : Relay("Relay", RelayType.Door)
    {
        public void SetState(RelayState state) => State = state;
        public override void Activate() { }
        public override void Deactivate() { }
        public override void Activate(TimeSpan duration) { }
    }

    [Fact]
    public void Area_RaisesStateChangedOncePerChange()
    {
        var area = new TestArea();
        var states = new List<AreaState>();
        area.StateChanged += (_, state) => states.Add(state);

        area.SetState(AreaState.Armed);
        area.SetState(AreaState.Armed);
        area.SetState(AreaState.Disarmed);

        Assert.Equal([AreaState.Armed, AreaState.Disarmed], states);
    }

    [Fact]
    public void Zone_RaisesEachEventOncePerChange()
    {
        var zone = new TestZone();
        var states = new List<ZoneState>();
        var isolated = new List<bool>();
        var tampered = new List<bool>();
        zone.StateChanged += (_, state) => states.Add(state);
        zone.IsolatedChanged += (_, value) => isolated.Add(value);
        zone.TamperedChanged += (_, value) => tampered.Add(value);

        zone.SetState(ZoneState.Unsealed);
        zone.SetState(ZoneState.Unsealed);
        zone.SetIsolated(true);
        zone.SetIsolated(true);
        zone.SetTampered(true);
        zone.SetTampered(false);

        Assert.Equal([ZoneState.Unsealed], states);
        Assert.Equal([true], isolated);
        Assert.Equal([true, false], tampered);
    }

    [Fact]
    public void Zone_KeepsItsType()
    {
        Assert.Equal(ZoneType.Motion, new TestZone().Type);
    }

    [Fact]
    public void Relay_RaisesStateChangedOncePerChange()
    {
        var relay = new TestRelay();
        var states = new List<RelayState>();
        relay.StateChanged += (_, state) => states.Add(state);

        relay.SetState(RelayState.Active);
        relay.SetState(RelayState.Active);
        relay.SetState(RelayState.Inactive);

        Assert.Equal([RelayState.Active, RelayState.Inactive], states);
        Assert.Equal(RelayType.Door, relay.Type);
    }
}
