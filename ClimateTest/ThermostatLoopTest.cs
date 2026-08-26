using System.Reflection;
using AVCoders.Core;
using Moq;

namespace AVCoders.Climate.Tests;

public class ThermostatLoopTest
{
    private readonly TemperzoneUc8 _uc8;
    private readonly ThermostatLoop _loop;
    private readonly Mock<ModbusRtuClient> _mockClient = new("Test client", "host", (ushort)502);
    private readonly List<string> _ops = [];
    private const byte DeviceId = 44;

    public ThermostatLoopTest()
    {
        _mockClient.Setup(client => client.WriteRegister(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<ushort>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, ushort, CancellationToken>((_, register, value, _) =>
                _ops.Add($"reg:{register}={value}"))
            .Returns(Task.CompletedTask);
        _mockClient.Setup(client => client.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, bool, CancellationToken>((_, coil, value, _) =>
                _ops.Add($"coil:{coil}={(value ? 1 : 0)}"))
            .Returns(Task.CompletedTask);
        _uc8 = new TemperzoneUc8("Test HVAC", _mockClient.Object);
        VerifyIdentity(_uc8);
        _loop = new ThermostatLoop("Test loop", _uc8)
        {
            MinDecisionInterval = TimeSpan.Zero,
            ChangeoverLockout = TimeSpan.Zero
        };
    }

    private static void VerifyIdentity(TemperzoneUc8 unit) =>
        typeof(TemperzoneUc8).GetMethod("ProcessIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(unit, [new ushort[] { 210, 209 }, new ushort[] { DeviceId }]);

    private void InvokeEvaluate() =>
        typeof(ThermostatLoop).GetMethod("Evaluate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_loop, []);

    private void MakeRoomTemperatureStale() =>
        typeof(ThermostatLoop).GetField("_roomTemperatureReceivedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_loop, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

    [Fact]
    public void CoolMode_StartsCoolingAboveTheBand()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _loop.SetRoomTemperature(24.6f);

        Assert.Equal(new[] { "coil:2=1", "reg:103=0", "coil:1=1", "reg:102=1", "coil:8=1", "reg:109=40" }, _ops);
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
    }

    [Fact]
    public void CoolMode_StopsBelowTheBand()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(24.6f);

        _loop.SetRoomTemperature(23.4f);

        Assert.Equal("reg:102=0", _ops.Last());
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);
    }

    [Fact]
    public void CoolMode_NoCommandInsideTheDeadband()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _loop.SetRoomTemperature(24.2f);

        Assert.Empty(_ops);
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);
    }

    [Fact]
    public void CoolMode_NeverHeats()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _loop.SetRoomTemperature(15f);

        Assert.Empty(_ops);
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);
    }

    [Fact]
    public void HeatMode_StartsHeatingBelowTheBand()
    {
        _loop.SetMode(ThermostatLoopMode.Heat);
        _loop.Enable();

        _loop.SetRoomTemperature(19.4f);

        Assert.Equal(new[] { "coil:2=1", "reg:103=1", "coil:1=1", "reg:102=1", "coil:8=1", "reg:109=40" }, _ops);
        Assert.Equal(ThermostatLoopState.Heating, _loop.State);
    }

    [Fact]
    public void HeatMode_StopsAboveTheBand()
    {
        _loop.SetMode(ThermostatLoopMode.Heat);
        _loop.Enable();
        _loop.SetRoomTemperature(19.4f);

        _loop.SetRoomTemperature(20.6f);

        Assert.Equal("reg:102=0", _ops.Last());
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);
    }

    [Fact]
    public void AutoMode_IdlesInTheGapBetweenSetpoints()
    {
        _loop.Enable();

        _loop.SetRoomTemperature(22f);

        Assert.Empty(_ops);
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);
    }

    [Fact]
    public void AutoMode_CoolsAboveTheCoolBandAndHeatsBelowTheHeatBand()
    {
        _loop.Enable();

        _loop.SetRoomTemperature(24.6f);
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
        Assert.Contains("reg:103=0", _ops);

        _loop.SetRoomTemperature(23.4f);
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);

        _loop.SetRoomTemperature(19.4f);
        Assert.Equal(ThermostatLoopState.Heating, _loop.State);
        Assert.Contains("reg:103=1", _ops);

        _loop.SetRoomTemperature(20.6f);
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);
    }

    [Fact]
    public void AutoMode_ChangeoverIsGatedByTheLockout()
    {
        _loop.ChangeoverLockout = TimeSpan.FromMinutes(10);
        _loop.Enable();
        _loop.SetRoomTemperature(24.6f);
        _ops.Clear();

        _loop.SetRoomTemperature(19.4f);

        Assert.Equal(new[] { "reg:102=0" }, _ops);
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);

        _loop.ChangeoverLockout = TimeSpan.Zero;
        _loop.SetRoomTemperature(19.4f);

        Assert.Contains("reg:103=1", _ops);
        Assert.Equal(ThermostatLoopState.Heating, _loop.State);
    }

    [Fact]
    public void SetHeatSetpoint_PushesTheCoolSetpointToKeepTheGap()
    {
        _loop.SetHeatSetpoint(23.5f);

        Assert.Equal(23.5f, _loop.HeatSetpoint);
        Assert.Equal(24.5f, _loop.CoolSetpoint);
        Assert.Equal(24.5f, _uc8.History.GetSamples(ThermostatLoop.CoolSetpointPoint).Last().Value);
    }

    [Fact]
    public void SetCoolSetpoint_PushesTheHeatSetpointToKeepTheGap()
    {
        _loop.SetCoolSetpoint(20f);

        Assert.Equal(20f, _loop.CoolSetpoint);
        Assert.Equal(19f, _loop.HeatSetpoint);
        Assert.Equal(19f, _uc8.History.GetSamples(ThermostatLoop.HeatSetpointPoint).Last().Value);
    }

    [Fact]
    public void SetHeatSetpoint_ClampsAndKeepsTheGapAtTheTopOfTheRange()
    {
        _loop.SetHeatSetpoint(50f);

        Assert.Equal(34f, _loop.HeatSetpoint);
        Assert.Equal(35f, _loop.CoolSetpoint);
    }

    [Fact]
    public void SetHeatSetpoint_ClampsTheBottomOfTheRange()
    {
        _loop.SetHeatSetpoint(5f);

        Assert.Equal(10f, _loop.HeatSetpoint);
        Assert.Equal(24f, _loop.CoolSetpoint);
    }

    [Fact]
    public void DryMode_CommandsUnitDryModeAndCyclesAgainstTheCoolSetpoint()
    {
        _loop.SetMode(ThermostatLoopMode.Dry);
        _loop.Enable();

        _loop.SetRoomTemperature(25f);

        Assert.Equal(new[] { "coil:11=1", "reg:112=1", "coil:1=1", "reg:102=1", "coil:8=1", "reg:109=40" }, _ops);
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);

        _loop.SetRoomTemperature(23.4f);

        Assert.Equal("reg:102=0", _ops.Last());
        Assert.Equal(ThermostatLoopState.Idle, _loop.State);
    }

    [Fact]
    public void LeavingDryMode_RestoresTheNonDryUnitMode()
    {
        _loop.SetMode(ThermostatLoopMode.Dry);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _ops.Clear();

        _loop.SetMode(ThermostatLoopMode.Cool);

        Assert.Contains("reg:103=0", _ops);
        Assert.Contains("reg:112=0", _ops);
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
    }

    [Fact]
    public void ProportionalCapacity_ScalesClampsQuantizesAndOnlyCommandsOnChange()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _loop.SetRoomTemperature(26.16f);
        _loop.SetRoomTemperature(27f);
        _loop.SetRoomTemperature(27f);
        _loop.SetRoomTemperature(32f);
        _loop.SetRoomTemperature(25f);

        Assert.Equal(new[] { "reg:109=65", "reg:109=90", "reg:109=100", "reg:109=40" },
            _ops.Where(op => op.StartsWith("reg:109")).ToArray());
    }

    [Fact]
    public void FixedCapacity_CommandsTheConfiguredValue()
    {
        _loop.ProportionalCapacity = false;
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _loop.SetRoomTemperature(27f);

        Assert.Contains("reg:109=65", _ops);
    }

    [Fact]
    public void StaleInput_FailsSafeOnceAndRaisesAnIssue()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _ops.Clear();
        MakeRoomTemperatureStale();

        InvokeEvaluate();

        Assert.Equal(new[] { "reg:102=0" }, _ops);
        Assert.Equal(ThermostatLoopState.InputStale, _loop.State);
        Assert.Single(_loop.GetOngoingIssues(), i => i.Key == "stale-input");

        InvokeEvaluate();

        Assert.Equal(new[] { "reg:102=0" }, _ops);
    }

    [Fact]
    public void StaleInput_FreshInputResolvesAndResumes()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        MakeRoomTemperatureStale();
        InvokeEvaluate();
        _ops.Clear();

        _loop.SetRoomTemperature(25f);

        Assert.DoesNotContain(_loop.GetOngoingIssues(), i => i.Key == "stale-input");
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
        Assert.Contains("reg:102=1", _ops);
    }

    [Fact]
    public void Waiting_WhileIdentityIsUnverified()
    {
        var unverified = new TemperzoneUc8("Unverified HVAC", _mockClient.Object);
        var loop = new ThermostatLoop("Unverified loop", unverified)
        {
            MinDecisionInterval = TimeSpan.Zero,
            ChangeoverLockout = TimeSpan.Zero
        };
        loop.SetMode(ThermostatLoopMode.Cool);
        loop.Enable();

        loop.SetRoomTemperature(30f);

        Assert.Equal(ThermostatLoopState.Waiting, loop.State);
        Assert.Empty(_ops);
    }

    [Fact]
    public void Waiting_UntilARoomTemperatureArrives()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);

        _loop.Enable();

        Assert.Equal(ThermostatLoopState.Waiting, _loop.State);
        Assert.Empty(_ops);
    }

    [Fact]
    public void Disable_CommandsCompressorOffOnceThenStaysSilent()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _ops.Clear();

        _loop.Disable();

        Assert.Equal(new[] { "reg:102=0" }, _ops);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);

        _loop.SetRoomTemperature(30f);

        Assert.Equal(new[] { "reg:102=0" }, _ops);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);
    }

    [Fact]
    public void MinDecisionInterval_GatesCompressorStateChanges()
    {
        _loop.MinDecisionInterval = TimeSpan.FromMinutes(10);
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);

        _loop.SetRoomTemperature(23f);

        Assert.DoesNotContain("reg:102=0", _ops);
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
    }

    [Fact]
    public void StateHandlers_AreInvokedOnTransitions()
    {
        var handler = new Mock<ThermostatLoopStateHandler>();
        _loop.StateHandlers += handler.Object;
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _loop.SetRoomTemperature(25f);

        handler.Verify(x => x.Invoke(ThermostatLoopState.Cooling), Times.Once);
    }

    [Fact]
    public void History_RecordsTheLoopPoints()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.SetHeatSetpoint(20f);
        _loop.SetCoolSetpoint(24f);
        _loop.Enable();
        _loop.SetRoomTemperature(26f);

        Assert.Equal(ThermostatLoop.ModeCoolCode,
            _uc8.History.GetSamples(ThermostatLoop.ModePoint).Last().Value);
        Assert.Contains(_uc8.History.GetSamples(ThermostatLoop.HeatSetpointPoint), s => s.Value == 20f);
        Assert.Contains(_uc8.History.GetSamples(ThermostatLoop.CoolSetpointPoint), s => s.Value == 24f);
        Assert.Contains(_uc8.History.GetSamples(ThermostatLoop.RoomTemperaturePoint), s => s.Value == 26f);
        Assert.Contains(_uc8.History.GetSamples(ThermostatLoop.StatePoint), s => s.Value == ThermostatLoop.CoolingCode);
        Assert.Contains(_uc8.History.GetSamples(ThermostatLoop.CommandedCapacityPoint), s => s.Value == 60f);
    }
}
