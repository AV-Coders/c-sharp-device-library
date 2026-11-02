using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Moq;

namespace AVCoders.Climate.Tests;

public class ThermostatLoopTest : IDisposable
{
    private readonly List<string> _ops = [];
    private readonly Mock<ModbusRtuClient> _mockClient;
    private readonly TemperzoneUc8 _uc8;
    private readonly ThermostatLoop _loop;
    private const byte DeviceId = 44;
    internal static readonly string[] Released = ["coil:1=0", "coil:2=0", "coil:8=0"];
    internal static readonly string[] ArmedForCooling =
        ["coil:2=1", "reg:103=0", "coil:1=1", "reg:102=1", "coil:8=1", "reg:109=40"];
    private static readonly TimeSpan HandlerCallTimeout = TimeSpan.FromSeconds(2);

    public ThermostatLoopTest()
    {
        _mockClient = CreateClient(_ops);
        _uc8 = CreateUnit(_mockClient);
        _loop = CreateLoop(_uc8);
    }

    public void Dispose()
    {
        _loop.Dispose();
        _uc8.Dispose();
    }

    internal static Mock<ModbusRtuClient> CreateClient(List<string> ops)
    {
        var client = new Mock<ModbusRtuClient>("Test client", "host", (ushort)502);
        client.Setup(c => c.WriteRegister(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<ushort>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, ushort, CancellationToken>((_, register, value, _) =>
                ops.Add($"reg:{register}={value}"))
            .Returns(Task.CompletedTask);
        client.Setup(c => c.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, bool, CancellationToken>((_, coil, value, _) =>
                ops.Add($"coil:{coil}={(value ? 1 : 0)}"))
            .Returns(Task.CompletedTask);
        return client;
    }

    internal static TemperzoneUc8 CreateUnit(Mock<ModbusRtuClient> client)
    {
        var unit = new TemperzoneUc8("Test HVAC", client.Object);
        VerifyIdentity(unit);
        return unit;
    }

    internal static ThermostatLoop CreateLoop(TemperzoneUc8 unit) => new("Test loop", unit)
    {
        MinDecisionInterval = TimeSpan.Zero,
        ChangeoverLockout = TimeSpan.Zero
    };

    private static void VerifyIdentity(TemperzoneUc8 unit) =>
        typeof(TemperzoneUc8).GetMethod("ProcessIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(unit, [new ushort[] { 210, 209 }, new ushort[] { DeviceId }]);

    internal static void InvokeEvaluate(ThermostatLoop loop) =>
        typeof(ThermostatLoop).GetMethod("Evaluate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(loop, []);

    internal static void MakeRoomTemperatureStale(ThermostatLoop loop) =>
        typeof(ThermostatLoop).GetField("_roomTemperatureReceivedAt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(loop, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));

    [Fact]
    public void CoolMode_StartsCoolingAboveTheBand()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _loop.SetRoomTemperature(24.6f);

        Assert.Equal(ArmedForCooling, _ops);
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

    [Theory]
    [InlineData(25.5f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void MinSetpointGap_RefusesAGapOutsideTheSetpointRange(float gap)
    {
        _loop.MinSetpointGap = gap;

        Assert.Equal(1f, _loop.MinSetpointGap);
        Assert.Contains(_loop.GetIssues(), i => i.Key == "min-setpoint-gap" &&
            i.Message.EndsWith("refused, it must be between 0 and 25", StringComparison.Ordinal));
    }

    [Fact]
    public void SetHeatSetpoint_KeepsBothSetpointsInRangeAtTheWidestGap()
    {
        _loop.MinSetpointGap = 25f;

        _loop.SetHeatSetpoint(30f);

        Assert.Equal(10f, _loop.HeatSetpoint);
        Assert.Equal(35f, _loop.CoolSetpoint);
    }

    [Fact]
    public void SetCoolSetpoint_KeepsBothSetpointsInRangeAtTheWidestGap()
    {
        _loop.MinSetpointGap = 25f;

        _loop.SetCoolSetpoint(15f);

        Assert.Equal(35f, _loop.CoolSetpoint);
        Assert.Equal(10f, _loop.HeatSetpoint);
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
        MakeRoomTemperatureStale(_loop);

        InvokeEvaluate(_loop);

        Assert.Equal(new[] { "reg:102=0" }, _ops);
        Assert.Equal(ThermostatLoopState.InputStale, _loop.State);
        Assert.Single(_loop.GetOngoingIssues(), i => i.Key == "stale-input");

        InvokeEvaluate(_loop);

        Assert.Equal(new[] { "reg:102=0" }, _ops);
    }

    [Fact]
    public void StaleInput_FreshInputResolvesAndResumes()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        MakeRoomTemperatureStale(_loop);
        InvokeEvaluate(_loop);
        _ops.Clear();

        _loop.SetRoomTemperature(25f);

        Assert.DoesNotContain(_loop.GetOngoingIssues(), i => i.Key == "stale-input");
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
        Assert.Contains("reg:102=1", _ops);
    }

    [Fact]
    public void StaleInput_HoldsTheCompressorOffWithoutReleasingControl()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(24.2f);
        MakeRoomTemperatureStale(_loop);

        InvokeEvaluate(_loop);
        InvokeEvaluate(_loop);

        Assert.Equal(new[] { "coil:1=1", "reg:102=0" }, _ops);
        Assert.True(_loop.Enabled);
        Assert.Equal(ThermostatLoopState.InputStale, _loop.State);
        Assert.Single(_loop.GetOngoingIssues(), i => i.Key == "stale-input");
    }

    [Fact]
    public void Waiting_WhileIdentityIsUnverified()
    {
        using var unverified = new TemperzoneUc8("Unverified HVAC", _mockClient.Object);
        using var loop = new ThermostatLoop("Unverified loop", unverified)
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
    public void Disable_ResolvesTheStaleInputIssue()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        MakeRoomTemperatureStale(_loop);
        InvokeEvaluate(_loop);

        _loop.Disable();

        Assert.DoesNotContain(_loop.GetOngoingIssues(), i => i.Key == "stale-input");
    }

    [Fact]
    public void Dispose_ReleasesControlOnceWithTheUnitAlsoDisposed()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _ops.Clear();

        _loop.Dispose();
        _uc8.Dispose();

        Assert.Equal(Released, _ops);
        Assert.False(_loop.Enabled);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);
    }

    [Fact]
    public void Enable_AfterDisable_TakesControlAgain()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _loop.Disable();
        _ops.Clear();

        _loop.Enable();

        Assert.Equal(ArmedForCooling, _ops);
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
    }

    [Fact]
    public void Enable_AfterAnOutsideRelease_TakesControlAgain()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _uc8.ReleaseControl();
        _ops.Clear();

        _loop.Enable();

        Assert.Equal(ArmedForCooling, _ops);
        Assert.Equal(ThermostatLoopState.Cooling, _loop.State);
    }

    [Fact]
    public void ControlReleasedHandlers_CanCallIntoTheLoopAndTheUnitFromAnotherThread()
    {
        var completed = new List<bool>();
        _uc8.ControlReleasedHandlers += () => completed.Add(Task.Run(() =>
        {
            _loop.SetRoomTemperature(25f);
            _ = _uc8.ControlEnableReadback;
        }).Wait(HandlerCallTimeout));
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);

        _uc8.ReleaseControl();
        _loop.Enable();
        _loop.Disable();

        Assert.Equal(new[] { true, true }, completed);
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

[Collection(LoggerFactoryCollection.Name)]
public class ThermostatLoopLogTest : IDisposable
{
    private const string OutsideReleaseWarning =
        "Control of Test HVAC was released outside the loop; the loop is disabled";
    private readonly ILoggerFactory _originalLoggerFactory = LogBase.LoggerFactory;
    private readonly ScopeCapturingLoggerFactory _logs = new();
    private readonly List<string> _ops = [];
    private readonly TemperzoneUc8 _uc8;
    private readonly ThermostatLoop _loop;

    public ThermostatLoopLogTest()
    {
        _uc8 = ThermostatLoopTest.CreateUnit(ThermostatLoopTest.CreateClient(_ops));
        LogBase.LoggerFactory = _logs;
        _loop = ThermostatLoopTest.CreateLoop(_uc8);
    }

    public void Dispose()
    {
        _loop.Dispose();
        _uc8.Dispose();
        LogBase.LoggerFactory = _originalLoggerFactory;
    }

    [Fact]
    public void Disable_ReleasesControlOnceThenStaysSilent()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _ops.Clear();

        _loop.Disable();

        Assert.Equal(ThermostatLoopTest.Released, _ops);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);
        Assert.DoesNotContain(_logs.Logs, log => log.Message == OutsideReleaseWarning);
        Assert.DoesNotContain(_loop.Events, e => e.Info == OutsideReleaseWarning);

        _loop.SetRoomTemperature(30f);
        _loop.SetCoolSetpoint(22f);
        ThermostatLoopTest.InvokeEvaluate(_loop);
        _loop.Disable();
        _loop.Dispose();

        Assert.Equal(ThermostatLoopTest.Released, _ops);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);
    }

    [Fact]
    public void OutsideRelease_WhileRunning_DisablesTheLoopAndSendsNothingMore()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        _ops.Clear();

        _uc8.ReleaseControl();
        _loop.SetRoomTemperature(30f);
        ThermostatLoopTest.InvokeEvaluate(_loop);

        Assert.Equal(ThermostatLoopTest.Released, _ops);
        Assert.False(_loop.Enabled);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);
        Assert.Single(_logs.Logs, log => log.Message == OutsideReleaseWarning);
        Assert.Contains(_loop.Events, e => e.Info == OutsideReleaseWarning);
    }

    [Fact]
    public void OutsideRelease_WhileWaiting_DisablesTheLoopAndSendsNothingMore()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();

        _uc8.ReleaseControl();
        _loop.SetRoomTemperature(30f);
        ThermostatLoopTest.InvokeEvaluate(_loop);

        Assert.Empty(_ops);
        Assert.False(_loop.Enabled);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);
        Assert.Single(_logs.Logs, log => log.Message == OutsideReleaseWarning);
        Assert.Contains(_loop.Events, e => e.Info == OutsideReleaseWarning);
    }

    [Fact]
    public void OutsideRelease_WhileInputStale_DisablesTheLoopAndResolvesTheStaleInputIssue()
    {
        _loop.SetMode(ThermostatLoopMode.Cool);
        _loop.Enable();
        _loop.SetRoomTemperature(25f);
        ThermostatLoopTest.MakeRoomTemperatureStale(_loop);
        ThermostatLoopTest.InvokeEvaluate(_loop);
        _ops.Clear();

        _uc8.ReleaseControl();
        ThermostatLoopTest.InvokeEvaluate(_loop);

        Assert.Equal(ThermostatLoopTest.Released, _ops);
        Assert.False(_loop.Enabled);
        Assert.Equal(ThermostatLoopState.Disabled, _loop.State);
        Assert.DoesNotContain(_loop.GetOngoingIssues(), i => i.Key == "stale-input");
        Assert.Single(_logs.Logs, log => log.Message == OutsideReleaseWarning);
    }
}
