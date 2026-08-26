using System.Reflection;
using AVCoders.Core;
using Moq;

namespace AVCoders.Climate.Tests;

public class TemperzoneUc8Test
{
    private readonly TemperzoneUc8 _uc8;
    private readonly Mock<ModbusRtuClient> _mockClient = new("Test client", "host", (ushort)502);
    private readonly List<string> _ops = [];
    private const byte DeviceId = 44;

    public TemperzoneUc8Test()
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
        VerifyIdentity();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private void VerifyIdentity() =>
        InvokePrivate("ProcessIdentity", new ushort[] { 210, 209 }, new ushort[] { DeviceId });

    private void InvokePrivate(string methodName, params object[] args)
    {
        var method = _uc8.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method!.Invoke(_uc8, args);
    }

    private static ushort[] TemperatureBlock()
    {
        var values = new ushort[14];
        for (var i = 0; i < values.Length; i++)
            values[i] = unchecked((ushort)-10000);
        values[12] = unchecked((ushort)-200);
        values[13] = unchecked((ushort)-200);
        return values;
    }

    private static ushort[] OutputBlock(ushort unitMode = 1)
    {
        var values = new ushort[7];
        values[6] = unitMode;
        return values;
    }

    private static ushort[] ControlBlock(ushort readback = 0)
    {
        var values = new ushort[21];
        values[0] = readback;
        values[108 - 101] = 550;
        values[109 - 101] = 65;
        values[110 - 101] = 1;
        values[111 - 101] = 1;
        values[112 - 101] = 0;
        values[115 - 101] = 1;
        values[118 - 101] = 1200;
        values[119 - 101] = 3600;
        return values;
    }

    private static ushort[] SafetyTimerBlock(ushort minimumRun = 0, ushort minimumOff = 0, ushort coolingHoldOff = 0)
    {
        var values = new ushort[16];
        values[0] = minimumRun;
        values[1] = minimumOff;
        values[14] = coolingHoldOff;
        return values;
    }

    [Fact]
    public void Identity_VerifiesWhenIdAndAddressMatch()
    {
        Assert.True(_uc8.IdentityVerified);
        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "identity");
    }

    [Fact]
    public void Identity_IsNotVerifiedByDefault()
    {
        var fresh = new TemperzoneUc8("Fresh HVAC", _mockClient.Object);

        Assert.False(fresh.IdentityVerified);
        fresh.SetCapacity(65);
        _mockClient.Verify(x => x.WriteRegister(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<ushort>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Identity_WrongIdCodeBlocksControlWrites()
    {
        InvokePrivate("ProcessIdentity", new ushort[] { 123, 209 }, new ushort[] { DeviceId });

        Assert.False(_uc8.IdentityVerified);
        var issue = Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "identity");
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
        _uc8.SetCapacity(65);
        Assert.Empty(_ops);
    }

    [Fact]
    public void Identity_WrongAddressBlocksControlWrites()
    {
        InvokePrivate("ProcessIdentity", new ushort[] { 210, 209 }, new ushort[] { 45 });

        Assert.False(_uc8.IdentityVerified);
        _uc8.PowerOn();
        Assert.Empty(_ops);
    }

    [Fact]
    public void Identity_RecoveryResolvesTheIssueAndUnblocksWrites()
    {
        InvokePrivate("ProcessIdentity", new ushort[] { 123, 209 }, new ushort[] { DeviceId });
        VerifyIdentity();

        Assert.True(_uc8.IdentityVerified);
        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "identity");
        _uc8.SetCapacity(65);
        Assert.Equal(new[] { "coil:8=1", "reg:109=65" }, _ops);
    }

    [Fact]
    public void ProcessTemperatures_ScalesPositiveTemperatures()
    {
        var handler = new Mock<FloatHandler>();
        _uc8.OutdoorAmbientTemperatureHandler += handler.Object;
        var values = TemperatureBlock();
        values[2] = 2345;

        InvokePrivate("ProcessTemperatures", values);

        handler.Verify(x => x.Invoke(23.45f), Times.Once);
    }

    [Fact]
    public void ProcessTemperatures_ScalesNegativeTemperatures()
    {
        var handler = new Mock<FloatHandler>();
        _uc8.OutdoorCoilTemperatureHandler += handler.Object;
        var values = TemperatureBlock();
        values[0] = unchecked((ushort)-550);

        InvokePrivate("ProcessTemperatures", values);

        handler.Verify(x => x.Invoke(-5.5f), Times.Once);
    }

    [Fact]
    public void ProcessTemperatures_SkipsAbsentSensors()
    {
        var handler = new Mock<FloatHandler>();
        _uc8.IndoorCoilTemperatureHandler += handler.Object;

        InvokePrivate("ProcessTemperatures", TemperatureBlock());

        handler.Verify(x => x.Invoke(It.IsAny<float>()), Times.Never);
    }

    [Fact]
    public void ProcessTemperatures_SkipsAbsentPressureTransducers()
    {
        var handler = new Mock<IntHandler>();
        _uc8.SuctionPressureHandler += handler.Object;

        InvokePrivate("ProcessTemperatures", TemperatureBlock());

        handler.Verify(x => x.Invoke(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void ProcessTemperatures_ReportsPressures()
    {
        var handler = new Mock<IntHandler>();
        _uc8.DischargePressureHandler += handler.Object;
        var values = TemperatureBlock();
        values[13] = 1850;

        InvokePrivate("ProcessTemperatures", values);

        handler.Verify(x => x.Invoke(1850), Times.Once);
    }

    [Fact]
    public void ProcessTemperatures_RecordsIntoHistoryAndSkipsSentinels()
    {
        var values = TemperatureBlock();
        values[2] = unchecked((ushort)-550);

        InvokePrivate("ProcessTemperatures", values);

        Assert.Equal(-5.5f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.OutdoorAmbientTemperaturePoint)).Value);
        Assert.Empty(_uc8.History.GetSamples(TemperzoneUc8History.OutdoorCoilTemperaturePoint));
        Assert.Empty(_uc8.History.GetSamples(TemperzoneUc8History.SuctionLinePressurePoint));
    }

    [Fact]
    public void ProcessTemperatures_SentinelsDoNotUpdateStaleness()
    {
        var values = TemperatureBlock();
        values[2] = 2345;

        InvokePrivate("ProcessTemperatures", values);

        var lastUpdated = _uc8.History.GetLastUpdated();
        Assert.True(lastUpdated.ContainsKey(TemperzoneUc8History.OutdoorAmbientTemperaturePoint));
        Assert.False(lastUpdated.ContainsKey(TemperzoneUc8History.OutdoorCoilTemperaturePoint));
    }

    [Fact]
    public void ProcessIndoorTemperatures_ReportsSupplyAndReturnAir()
    {
        var supplyHandler = new Mock<FloatHandler>();
        var returnHandler = new Mock<FloatHandler>();
        _uc8.SupplyAirTemperatureHandler += supplyHandler.Object;
        _uc8.ReturnAirTemperatureHandler += returnHandler.Object;
        ushort[] values = [1200, 1250, 1300, 1350, 1450, 2250];

        InvokePrivate("ProcessIndoorTemperatures", values);

        supplyHandler.Verify(x => x.Invoke(14.5f), Times.Once);
        returnHandler.Verify(x => x.Invoke(22.5f), Times.Once);
    }

    [Fact]
    public void ProcessThermostatBlock_ReportsSetpointAndRoomTemperature()
    {
        var setpointHandler = new Mock<FloatHandler>();
        var roomHandler = new Mock<FloatHandler>();
        _uc8.SetpointTemperatureHandler += setpointHandler.Object;
        _uc8.RoomTemperatureHandler += roomHandler.Object;

        InvokePrivate("ProcessThermostatBlock", new ushort[] { 2250, 2310 });

        setpointHandler.Verify(x => x.Invoke(22.5f), Times.Once);
        roomHandler.Verify(x => x.Invoke(23.1f), Times.Once);
        Assert.Equal(22.5f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.SetpointTemperaturePoint)).Value);
        Assert.Equal(23.1f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.RoomTemperaturePoint)).Value);
    }

    [Fact]
    public void ProcessThermostatBlock_SkipsAbsentValues()
    {
        var setpointHandler = new Mock<FloatHandler>();
        _uc8.SetpointTemperatureHandler += setpointHandler.Object;

        InvokePrivate("ProcessThermostatBlock", new ushort[] { unchecked((ushort)-10000), 2310 });

        setpointHandler.Verify(x => x.Invoke(It.IsAny<float>()), Times.Never);
        Assert.Empty(_uc8.History.GetSamples(TemperzoneUc8History.SetpointTemperaturePoint));
    }

    [Fact]
    public void SetEconomyMode_WritesToRegister115()
    {
        _uc8.SetEconomyMode(true);

        Assert.Equal(new[] { "coil:12=1", "reg:115=1" }, _ops);
    }

    [Theory]
    [InlineData(TemperzoneUc8FanSpeed.Off, 0)]
    [InlineData(TemperzoneUc8FanSpeed.Low, 100)]
    [InlineData(TemperzoneUc8FanSpeed.Medium, 550)]
    [InlineData(TemperzoneUc8FanSpeed.High, 1000)]
    public void SetFanSpeed_WritesTheEnumValueToRegister108(TemperzoneUc8FanSpeed speed, int expectedValue)
    {
        _uc8.SetFanSpeed(speed);

        _mockClient.Verify(x => x.WriteRegister(DeviceId, 108, (ushort)expectedValue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(650, 650)]
    [InlineData(1500, 1000)]
    public void SetFanSpeed_ClampsRawValues(int speed, int expectedValue)
    {
        _uc8.SetFanSpeed(speed);

        _mockClient.Verify(x => x.WriteRegister(DeviceId, 108, (ushort)expectedValue, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void SetFanSpeed_ArmsFanModeAndWritesFixedSpeedDefaults()
    {
        _uc8.SetFanSpeed(550);

        Assert.Equal(new[] { "coil:4=1", "coil:7=1", "reg:105=15", "reg:108=550" }, _ops);
    }

    [Fact]
    public void SetFanMode_WritesTheRequestedBits()
    {
        _uc8.SetFanMode(true, false, false, false, true);

        Assert.Equal(new[] { "coil:4=1", "reg:105=17" }, _ops);
    }

    [Fact]
    public void SetFanSpeed_PreservesACommandedFanMode()
    {
        _uc8.SetFanMode(true, false, false, false, true);
        _uc8.SetFanSpeed(550);

        Assert.Equal(new[] { "coil:4=1", "reg:105=17", "coil:7=1", "reg:105=17", "reg:108=550" }, _ops);
    }

    [Fact]
    public void SetCapacity_WritesTheEnableCoilBeforeTheControlRegister()
    {
        _uc8.SetCapacity(65);

        Assert.Equal(new[] { "coil:8=1", "reg:109=65" }, _ops);
    }

    [Fact]
    public void PowerOn_WritesRemoteOnAndCompressorOn()
    {
        _uc8.PowerOn();

        Assert.Equal(new[] { "coil:1=1", "coil:3=1", "reg:104=1", "reg:102=1" }, _ops);
    }

    [Fact]
    public void PowerOff_WritesCompressorOffThenRemoteOff()
    {
        _uc8.PowerOff();

        Assert.Equal(new[] { "coil:1=1", "coil:3=1", "reg:102=0", "reg:104=0" }, _ops);
    }

    [Fact]
    public void SetMode_Cool_WritesZeroToRegister103()
    {
        _uc8.SetMode(HvacMode.Cool);

        Assert.Equal(new[] { "coil:2=1", "reg:103=0" }, _ops);
    }

    [Fact]
    public void SetMode_Heat_WritesOneToRegister103()
    {
        _uc8.SetMode(HvacMode.Heat);

        Assert.Equal(new[] { "coil:2=1", "reg:103=1" }, _ops);
    }

    [Fact]
    public void SetMode_Dry_WritesOneToRegister112()
    {
        _uc8.SetMode(HvacMode.Dry);

        Assert.Equal(new[] { "coil:11=1", "reg:112=1" }, _ops);
    }

    [Fact]
    public void SetMode_FanOnly_StopsTheCompressorAndRunsTheFan()
    {
        _uc8.SetMode(HvacMode.FanOnly);

        Assert.Equal(new[] { "coil:1=1", "coil:7=1", "reg:102=0", "reg:108=100" }, _ops);
    }

    [Fact]
    public void SetMode_CoolAfterFanOnly_RestartsTheCompressor()
    {
        _uc8.SetMode(HvacMode.FanOnly);
        _uc8.SetMode(HvacMode.Cool);

        Assert.Equal(new[] { "coil:1=1", "coil:7=1", "reg:102=0", "reg:108=100", "coil:2=1", "reg:103=0", "reg:102=1" },
            _ops);
    }

    [Fact]
    public void SetCompressor_WritesToRegister102()
    {
        _uc8.SetCompressor(true);

        Assert.Equal(new[] { "coil:1=1", "reg:102=1" }, _ops);
    }

    [Fact]
    public void SetCompressors_WritesTheSlaveBitmask()
    {
        _uc8.SetCompressors(true, slave1: true, slave3: true);

        Assert.Equal(new[] { "coil:1=1", "reg:102=11" }, _ops);
    }

    [Fact]
    public void SetCompressor_PreservesSlaveBits()
    {
        _uc8.SetCompressors(true, slave1: true);
        _uc8.SetCompressor(false);

        Assert.Equal(new[] { "coil:1=1", "reg:102=3", "reg:102=2" }, _ops);
    }

    [Fact]
    public void SetQuietMode_WritesToRegister111()
    {
        _uc8.SetQuietMode(true);

        Assert.Equal(new[] { "coil:10=1", "reg:111=1" }, _ops);
    }

    [Fact]
    public void SetDeIcePermission_WritesToRegister110()
    {
        _uc8.SetDeIcePermission(true);

        Assert.Equal(new[] { "coil:9=1", "reg:110=1" }, _ops);
    }

    [Fact]
    public void ForceDeIce_AlwaysGrantsPermissionWithTheForceBit()
    {
        _uc8.ForceDeIce();

        Assert.Equal(new[] { "coil:9=1", "reg:110=17" }, _ops);
        Assert.DoesNotContain("reg:110=16", _ops);
    }

    [Fact]
    public void ForceDeIce_ClearsTheForceBitOnceDeIceIsActive()
    {
        _uc8.ForceDeIce();
        var values = OutputBlock(9);
        values[5] = 1 << 12;

        InvokePrivate("ProcessOutputs", values);

        Assert.Equal("reg:110=0", _ops.Last());
    }

    [Fact]
    public void ForceDeIce_ClearsTheForceBitAfterTheTimeout()
    {
        _uc8.ForceDeIceTimeout = TimeSpan.Zero;
        _uc8.ForceDeIce();

        InvokePrivate("ProcessOutputs", OutputBlock(9));

        Assert.Equal("reg:110=0", _ops.Last());
    }

    [Fact]
    public void ForceDeIce_RestoresAnAllowedPermissionWhenCleared()
    {
        _uc8.SetDeIcePermission(true);
        _uc8.ForceDeIce();
        var values = OutputBlock(9);
        values[5] = 1 << 12;

        InvokePrivate("ProcessOutputs", values);

        Assert.Equal(new[] { "coil:9=1", "reg:110=1", "reg:110=17", "reg:110=1" }, _ops);
    }

    [Fact]
    public void ForceDeIce_RestoresADeniedPermissionWhenCleared()
    {
        _uc8.SetDeIcePermission(false);
        _uc8.ForceDeIce();
        var values = OutputBlock(9);
        values[5] = 1 << 12;

        InvokePrivate("ProcessOutputs", values);

        Assert.Equal(new[] { "coil:9=1", "reg:110=0", "reg:110=17", "reg:110=0" }, _ops);
    }

    [Fact]
    public void ForceDeIce_StaysLatchedUntilStatusOrTimeout()
    {
        _uc8.ForceDeIce();

        InvokePrivate("ProcessOutputs", OutputBlock(9));

        Assert.Equal(new[] { "coil:9=1", "reg:110=17" }, _ops);
    }

    [Fact]
    public void ProcessControlBlock_ReportsTheDeIcePermissionReadback()
    {
        var handler = new Mock<BoolHandler>();
        _uc8.DeIcePermissionHandler += handler.Object;

        InvokePrivate("ProcessControlBlock", ControlBlock());

        Assert.True(_uc8.DeIcePermission);
        handler.Verify(x => x.Invoke(true), Times.Once);
    }

    [Fact]
    public void SecondControlCall_DoesNotRewriteTheEnableCoil()
    {
        _uc8.SetCapacity(65);
        _uc8.SetCapacity(70);

        Assert.Equal(new[] { "coil:8=1", "reg:109=65", "reg:109=70" }, _ops);
    }

    [Fact]
    public void ControlCalls_AccumulateEnableCoils()
    {
        _uc8.SetCapacity(65);
        _uc8.SetQuietMode(true);

        Assert.Equal(new[] { "coil:8=1", "reg:109=65", "coil:10=1", "reg:111=1" }, _ops);
    }

    [Fact]
    public void MonitoringOnly_NeverWritesCoilsOrRegisters()
    {
        InvokePrivate("ProcessControlEnable", (ushort)0);
        InvokePrivate("ProcessControlBlock", new ushort[21]);
        InvokePrivate("ProcessTemperatures", TemperatureBlock());
        InvokePrivate("ProcessOutputs", OutputBlock(3));

        Assert.Empty(_ops);
    }

    [Fact]
    public void ProcessControlEnable_ReappliesEverythingWhenBitsAreLost()
    {
        _uc8.SetCapacity(65);
        _uc8.SetFanSpeed(550);
        _ops.Clear();

        InvokePrivate("ProcessControlEnable", (ushort)0);

        Assert.Equal(new[] { "coil:4=1", "coil:7=1", "coil:8=1", "reg:105=15", "reg:108=550", "reg:109=65" }, _ops);
    }

    [Fact]
    public void ProcessControlEnable_DoesNothingWhenBitsAreIntact()
    {
        _uc8.SetCapacity(65);
        _ops.Clear();

        InvokePrivate("ProcessControlEnable", (ushort)128);

        Assert.Empty(_ops);
    }

    [Fact]
    public void ReleaseControl_ClearsOnlyTheArmedCoils()
    {
        _uc8.SetCapacity(65);
        _uc8.SetQuietMode(true);
        _ops.Clear();

        _uc8.ReleaseControl();
        InvokePrivate("ProcessControlEnable", (ushort)0);

        Assert.Equal(new[] { "coil:8=0", "coil:10=0" }, _ops);
    }

    [Fact]
    public void Dispose_ReleasesControlAndUnsubscribes()
    {
        _uc8.SetCapacity(65);
        _ops.Clear();

        _uc8.Dispose();

        Assert.Contains("coil:8=0", _ops);
        Assert.Null(_mockClient.Object.ConnectionStateHandlers);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForTheReleaseCoilWrites()
    {
        _uc8.SetCapacity(65);
        _ops.Clear();
        _mockClient.Setup(client => client.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns<byte, ushort, bool, CancellationToken>(async (_, coil, value, _) =>
            {
                await Task.Delay(100);
                _ops.Add($"coil:{coil}={(value ? 1 : 0)}");
            });

        await _uc8.DisposeAsync();

        Assert.Contains("coil:8=0", _ops);
    }

    [Fact]
    public void Dispose_BlocksUntilTheReleaseCoilWrites()
    {
        _uc8.SetCapacity(65);
        _ops.Clear();
        _mockClient.Setup(client => client.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns<byte, ushort, bool, CancellationToken>(async (_, coil, value, _) =>
            {
                await Task.Delay(100);
                _ops.Add($"coil:{coil}={(value ? 1 : 0)}");
            });

        _uc8.Dispose();

        Assert.Contains("coil:8=0", _ops);
    }

    [Fact]
    public void ReleaseControl_MidArming_AbortsTheArmingSequence()
    {
        var released = false;
        _mockClient.Setup(client => client.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, bool, CancellationToken>((_, coil, value, _) =>
            {
                _ops.Add($"coil:{coil}={(value ? 1 : 0)}");
                if (coil != 1 || !value || released)
                    return;
                released = true;
                _uc8.ReleaseControl();
            })
            .Returns(Task.CompletedTask);

        _uc8.PowerOn();

        Assert.Equal(new[] { "coil:1=1", "coil:1=0", "coil:3=0" }, _ops);
    }

    [Fact]
    public async Task ReleaseControl_DuringArming_PreventsTheStaleRegisterWrite()
    {
        var gate = new TaskCompletionSource();
        _mockClient.Setup(client => client.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, bool, CancellationToken>((_, coil, value, _) =>
                _ops.Add($"coil:{coil}={(value ? 1 : 0)}"))
            .Returns<byte, ushort, bool, CancellationToken>((_, _, value, _) =>
                value ? gate.Task : Task.CompletedTask);

        _uc8.SetCapacity(65);
        _uc8.ReleaseControl();
        gate.SetResult();
        await Task.Delay(200);

        Assert.Equal(new[] { "coil:8=1", "coil:8=0" }, _ops);
        Assert.DoesNotContain("reg:109=65", _ops);
    }

    [Fact]
    public async Task ReapplyControl_RunsSingleFlight()
    {
        _uc8.SetCapacity(65);
        _ops.Clear();
        var gate = new TaskCompletionSource();
        _mockClient.Setup(client => client.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, bool, CancellationToken>((_, coil, value, _) =>
                _ops.Add($"coil:{coil}={(value ? 1 : 0)}"))
            .Returns(gate.Task);
        var method = _uc8.GetType().GetMethod("ReapplyControl", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var first = (Task)method.Invoke(_uc8, [])!;
        var second = (Task)method.Invoke(_uc8, [])!;

        Assert.True(second.IsCompleted);
        Assert.False(first.IsCompleted);
        gate.SetResult();
        await first;

        Assert.Equal(new[] { "coil:8=1" }, _ops.Where(op => op.StartsWith("coil")).ToList());
        Assert.Contains("reg:109=65", _ops);
    }

    [Fact]
    public void ProcessControlEnable_AdoptsOrphanedArming()
    {
        InvokePrivate("ProcessControlEnable", (ushort)(128 | 512));

        var issue = Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "adopted-control");
        Assert.Contains("Capacity", issue.Message);
        Assert.Contains("Quiet Mode", issue.Message);
        Assert.Empty(_ops);

        InvokePrivate("UpdateWatchdogRisk", false);

        Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "watchdog-risk");
    }

    [Fact]
    public void ReleaseControl_ClearsAdoptedArming()
    {
        InvokePrivate("ProcessControlEnable", (ushort)(128 | 512));

        _uc8.ReleaseControl();

        Assert.Equal(new[] { "coil:8=0", "coil:10=0" }, _ops);
        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "adopted-control");
    }

    [Fact]
    public void ReleaseControl_ClearsAnOrphanedUnitUsingTheReadback()
    {
        InvokePrivate("ProcessControlBlock", ControlBlock(64));

        _uc8.ReleaseControl();

        Assert.Contains("coil:7=0", _ops);
    }

    [Fact]
    public async Task Adoption_DoesNotFireDuringAnInFlightSequence()
    {
        var gate = new TaskCompletionSource();
        _mockClient.Setup(client => client.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, bool, CancellationToken>((_, coil, value, _) =>
                _ops.Add($"coil:{coil}={(value ? 1 : 0)}"))
            .Returns(gate.Task);
        _uc8.SetCapacity(65);

        InvokePrivate("ProcessControlEnable", (ushort)512);

        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "adopted-control");
        gate.SetResult();
        await WaitUntil(() => _ops.Contains("reg:109=65"));

        InvokePrivate("ProcessControlEnable", (ushort)(128 | 512));

        Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "adopted-control");
    }

    [Fact]
    public void OverrideIdentityCheck_UnblocksControlWrites()
    {
        InvokePrivate("ProcessIdentity", new ushort[] { 123, 209 }, new ushort[] { DeviceId });

        _uc8.OverrideIdentityCheck();

        Assert.True(_uc8.IdentityVerified);
        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "identity");
        _uc8.SetCapacity(65);
        Assert.Equal(new[] { "coil:8=1", "reg:109=65" }, _ops);
    }

    [Fact]
    public void ExpectedIdCode_OverridesTheDefault()
    {
        InvokePrivate("ProcessIdentity", new ushort[] { 123, 209 }, new ushort[] { DeviceId });
        Assert.False(_uc8.IdentityVerified);

        _uc8.ExpectedIdCode = 123;
        InvokePrivate("ProcessIdentity", new ushort[] { 123, 209 }, new ushort[] { DeviceId });

        Assert.True(_uc8.IdentityVerified);
    }

    [Fact]
    public void ResetLockout_IsNotGatedByIdentity()
    {
        InvokePrivate("ProcessIdentity", new ushort[] { 123, 209 }, new ushort[] { DeviceId });

        _uc8.ResetLockout();

        Assert.Equal(new[] { "reg:1901=21930", "reg:1901=3855" }, _ops);
    }

    [Fact]
    public void SetMode_Cool_PreservesSlaveCompressorBits()
    {
        _uc8.SetCompressors(true, slave1: true, slave2: true);

        _uc8.SetMode(HvacMode.Cool);

        Assert.Equal(new[] { "coil:1=1", "reg:102=7", "coil:2=1", "reg:103=0", "reg:102=7" }, _ops);
    }

    [Fact]
    public void PowerOn_PreservesSlaveCompressorBits()
    {
        _uc8.SetCompressors(true, slave1: true);

        _uc8.PowerOn();

        Assert.Equal(new[] { "coil:1=1", "reg:102=3", "coil:3=1", "reg:104=1", "reg:102=3" }, _ops);
    }

    [Fact]
    public async Task ConcurrentCompressorAndModeWrites_DoNotLoseTheCompressorMask()
    {
        _uc8.SetCompressors(true, slave1: true);
        var gate = new TaskCompletionSource();
        _mockClient.Setup(client => client.WriteRegister(DeviceId, 103, It.IsAny<ushort>(),
                It.IsAny<CancellationToken>()))
            .Callback<byte, ushort, ushort, CancellationToken>((_, register, value, _) =>
                _ops.Add($"reg:{register}={value}"))
            .Returns(gate.Task);

        _uc8.SetMode(HvacMode.Cool);
        _uc8.SetCompressor(false);
        gate.SetResult();
        await WaitUntil(() => _ops.Count(op => op.StartsWith("reg:102")) == 3);

        Assert.Equal("reg:102=2", _ops.Last(op => op.StartsWith("reg:102")));
    }

    [Fact]
    public void SetFanSpeed_DoesNotForceFixedSpeedOverACommandedFanMode()
    {
        _uc8.SetFanMode(false, true, true, true, false);

        _uc8.SetFanSpeed(550);

        Assert.Equal(new[] { "coil:4=1", "reg:105=14", "coil:7=1", "reg:105=14", "reg:108=550" }, _ops);
    }

    [Fact]
    public void ProcessControlBlock_SurfacesTheArmedState()
    {
        var handler = new Mock<IntHandler>();
        _uc8.ControlEnableHandler += handler.Object;

        InvokePrivate("ProcessControlBlock", ControlBlock(65));

        Assert.Equal(65, _uc8.ControlEnableReadback);
        handler.Verify(x => x.Invoke(65), Times.Once);
        Assert.Equal(new[] { "Compressor", "Fan Speed" }, _uc8.GetArmedFunctions());
    }

    [Fact]
    public void ProcessControlBlock_InvokesTheControlHandlers()
    {
        var quietHandler = new Mock<BoolHandler>();
        var dryHandler = new Mock<BoolHandler>();
        var economyHandler = new Mock<BoolHandler>();
        var coolingHandler = new Mock<FloatHandler>();
        var heatingHandler = new Mock<FloatHandler>();
        var fanSpeedHandler = new Mock<IntHandler>();
        var capacityHandler = new Mock<IntHandler>();
        _uc8.QuietModeHandler += quietHandler.Object;
        _uc8.DryModeHandler += dryHandler.Object;
        _uc8.EconomyModeHandler += economyHandler.Object;
        _uc8.CoolingTargetHandler += coolingHandler.Object;
        _uc8.HeatingTargetHandler += heatingHandler.Object;
        _uc8.FanSpeedRequestHandler += fanSpeedHandler.Object;
        _uc8.CapacityRequestHandler += capacityHandler.Object;

        InvokePrivate("ProcessControlBlock", ControlBlock());

        quietHandler.Verify(x => x.Invoke(true), Times.Once);
        dryHandler.Verify(x => x.Invoke(false), Times.Once);
        economyHandler.Verify(x => x.Invoke(true), Times.Once);
        coolingHandler.Verify(x => x.Invoke(12f), Times.Once);
        heatingHandler.Verify(x => x.Invoke(36f), Times.Once);
        fanSpeedHandler.Verify(x => x.Invoke(550), Times.Once);
        capacityHandler.Verify(x => x.Invoke(65), Times.Once);
    }

    [Fact]
    public void ProcessControlBlock_RecordsRequestsAndTargetsIntoHistory()
    {
        InvokePrivate("ProcessControlBlock", ControlBlock());

        Assert.Equal(550f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.FanSpeedRequestPoint)).Value);
        Assert.Equal(65f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.CapacityRequestPoint)).Value);
        Assert.Equal(1f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.QuietModePoint)).Value);
        Assert.Equal(0f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.DryModePoint)).Value);
        Assert.Equal(1f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.EconomyModePoint)).Value);
        Assert.Equal(12f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.CoolingSupplyAirTargetPoint)).Value);
        Assert.Equal(36f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.HeatingSupplyAirTargetPoint)).Value);
    }

    [Fact]
    public void ProcessSafetyTimers_ReportsAndRecordsTheTimers()
    {
        var runHandler = new Mock<IntHandler>();
        var coolingHandler = new Mock<IntHandler>();
        _uc8.MinimumRunTimerHandler += runHandler.Object;
        _uc8.CoolingHoldOffTimerHandler += coolingHandler.Object;

        InvokePrivate("ProcessSafetyTimers", SafetyTimerBlock(minimumRun: 30, coolingHoldOff: 120));

        runHandler.Verify(x => x.Invoke(30), Times.Once);
        coolingHandler.Verify(x => x.Invoke(120), Times.Once);
        Assert.Equal(30f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.MinimumRunTimerPoint)).Value);
        Assert.Equal(120f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.CoolingHoldOffTimerPoint)).Value);
    }

    [Fact]
    public void EnforcePowerState_HoldsOffWhileASafetyTimerIsActive()
    {
        _uc8.PowerOn();
        var opCount = _ops.Count;
        InvokePrivate("ProcessSafetyTimers", SafetyTimerBlock(minimumOff: 60));

        InvokePrivate("ProcessOutputs", OutputBlock(1));

        Assert.Equal(opCount, _ops.Count);
        var issue = Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "power-enforcement-hold");
        Assert.Contains("minimum off", issue.Message);
    }

    [Fact]
    public void EnforcePowerState_EnforcesOnceTimersClearWithBackoff()
    {
        _uc8.PowerOn();
        InvokePrivate("ProcessSafetyTimers", SafetyTimerBlock(minimumOff: 60));
        InvokePrivate("ProcessOutputs", OutputBlock(1));
        InvokePrivate("ProcessSafetyTimers", SafetyTimerBlock());

        InvokePrivate("ProcessOutputs", OutputBlock(1));
        InvokePrivate("ProcessOutputs", OutputBlock(1));

        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "power-enforcement-hold");
        Assert.Equal(2, _ops.Count(op => op == "reg:104=1"));
    }

    [Fact]
    public void UpdateWatchdogRisk_RaisesWhileArmedAndFailing()
    {
        _uc8.SetCapacity(65);

        InvokePrivate("UpdateWatchdogRisk", false);

        var issue = Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "watchdog-risk");
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
    }

    [Fact]
    public void UpdateWatchdogRisk_ResolvesOnRecovery()
    {
        _uc8.SetCapacity(65);
        InvokePrivate("UpdateWatchdogRisk", false);

        InvokePrivate("UpdateWatchdogRisk", true);

        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "watchdog-risk");
    }

    [Fact]
    public void UpdateWatchdogRisk_StaysQuietWhileNothingIsArmed()
    {
        InvokePrivate("UpdateWatchdogRisk", false);

        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "watchdog-risk");
    }

    [Fact]
    public void ProcessFaults_RaisesACriticalIssueForAProtectionTrip()
    {
        InvokePrivate("ProcessFaults", new ushort[] { 1, 0, 0, 0, 1 });

        var issue = Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "fault-hp");
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
    }

    [Fact]
    public void ProcessFaults_RaisesAMajorIssueForASensorFault()
    {
        InvokePrivate("ProcessFaults", new ushort[] { 0, 1 << 5, 0, 0, 21 });

        var issue = Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "fault-thermostat-comms");
        Assert.Equal(IssueSeverity.Major, issue.Severity);
    }

    [Fact]
    public void ProcessFaults_ResolvesTheIssueWhenTheBitClears()
    {
        InvokePrivate("ProcessFaults", new ushort[] { 1, 0, 0, 0, 1 });
        InvokePrivate("ProcessFaults", new ushort[] { 0, 0, 0, 0, 0 });

        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "fault-hp");
        Assert.Contains(_uc8.GetIssues(), i => i.Key == "fault-hp" && i.Status == IssueStatus.Resolved);
    }

    [Fact]
    public void ProcessFaults_ReportsTheFaultNumber()
    {
        var handler = new Mock<IntHandler>();
        _uc8.FaultNumberHandler += handler.Object;

        InvokePrivate("ProcessFaults", new ushort[] { 2, 0, 0, 0, 2 });

        handler.Verify(x => x.Invoke(2), Times.Once);
        Assert.Equal(2f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.FaultNumberPoint)).Value);
    }

    [Fact]
    public void ProcessFaults_RecordsFaultBanksIntoHistory()
    {
        InvokePrivate("ProcessFaults", new ushort[] { 3, 0, 0, 0, 1 });

        Assert.Equal(3f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.FaultBank1Point)).Value);
        Assert.Equal(0f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.FaultBank2Point)).Value);
    }

    [Fact]
    public void ProcessOutputs_UnitMode12RaisesALockoutIssue()
    {
        InvokePrivate("ProcessOutputs", OutputBlock(12));

        var issue = Assert.Single(_uc8.GetOngoingIssues(), i => i.Key == "lockout");
        Assert.Equal(IssueSeverity.Critical, issue.Severity);
    }

    [Fact]
    public void ProcessOutputs_ResolvesTheLockoutIssueWhenTheUnitRecovers()
    {
        InvokePrivate("ProcessOutputs", OutputBlock(12));
        InvokePrivate("ProcessOutputs", OutputBlock(1));

        Assert.DoesNotContain(_uc8.GetOngoingIssues(), i => i.Key == "lockout");
    }

    [Theory]
    [InlineData(1, PowerState.Off)]
    [InlineData(3, PowerState.On)]
    [InlineData(6, PowerState.On)]
    [InlineData(12, PowerState.Off)]
    public void ProcessOutputs_MapsUnitModeToPowerState(int unitMode, PowerState expectedState)
    {
        InvokePrivate("ProcessOutputs", OutputBlock((ushort)unitMode));

        Assert.Equal(expectedState, _uc8.PowerState);
    }

    [Theory]
    [InlineData(3, HvacMode.Cool)]
    [InlineData(6, HvacMode.Heat)]
    public void ProcessOutputs_MapsUnitModeToHvacMode(int unitMode, HvacMode expectedMode)
    {
        var handler = new Mock<HvacModeHandler>();
        _uc8.ModeHandlers += handler.Object;

        InvokePrivate("ProcessOutputs", OutputBlock((ushort)unitMode));

        Assert.Equal(expectedMode, _uc8.Mode);
        handler.Verify(x => x.Invoke(expectedMode), Times.Once);
    }

    [Fact]
    public void ProcessOutputs_ReportsTheRawUnitMode()
    {
        var handler = new Mock<IntHandler>();
        _uc8.UnitModeHandler += handler.Object;

        InvokePrivate("ProcessOutputs", OutputBlock(9));

        handler.Verify(x => x.Invoke(9), Times.Once);
    }

    [Fact]
    public void ProcessOutputs_ScalesCapacityToPercent()
    {
        var handler = new Mock<IntHandler>();
        _uc8.CapacityHandler += handler.Object;
        ushort[] values = [800, 550, 0, 0, 650, 0, 3];

        InvokePrivate("ProcessOutputs", values);

        handler.Verify(x => x.Invoke(65), Times.Once);
        Assert.Equal(65f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.CapacityPoint)).Value);
    }

    [Fact]
    public void ProcessOutputs_ReportsFanSpeeds()
    {
        var outdoorHandler = new Mock<IntHandler>();
        var indoorHandler = new Mock<IntHandler>();
        _uc8.OutdoorFanSpeedHandler += outdoorHandler.Object;
        _uc8.IndoorFanSpeedHandler += indoorHandler.Object;
        ushort[] values = [800, 550, 0, 0, 650, 0, 3];

        InvokePrivate("ProcessOutputs", values);

        outdoorHandler.Verify(x => x.Invoke(800), Times.Once);
        indoorHandler.Verify(x => x.Invoke(550), Times.Once);
    }

    [Fact]
    public void ProcessOutputs_ReportsDeIceRequestAndStatus()
    {
        var requestHandler = new Mock<BoolHandler>();
        var statusHandler = new Mock<BoolHandler>();
        _uc8.DeIceRequestHandler += requestHandler.Object;
        _uc8.DeIceStatusHandler += statusHandler.Object;
        var values = OutputBlock(9);
        values[5] = 1 << 11;

        InvokePrivate("ProcessOutputs", values);

        requestHandler.Verify(x => x.Invoke(true), Times.Once);
        statusHandler.Verify(x => x.Invoke(false), Times.Once);
    }

    [Fact]
    public void ProcessOutputs_ReportsTheRelayStates()
    {
        var compressorHandler = new Mock<BoolHandler>();
        var valveHandler = new Mock<BoolHandler>();
        var dredHandler = new Mock<BoolHandler>();
        var oilHandler = new Mock<BoolHandler>();
        _uc8.CompressorRelayHandler += compressorHandler.Object;
        _uc8.ReverseValveHandler += valveHandler.Object;
        _uc8.DredHoldOffHandler += dredHandler.Object;
        _uc8.OilRecoveryHandler += oilHandler.Object;
        var values = OutputBlock(6);
        values[5] = 1 | 2 | (1 << 14);

        InvokePrivate("ProcessOutputs", values);

        compressorHandler.Verify(x => x.Invoke(true), Times.Once);
        valveHandler.Verify(x => x.Invoke(true), Times.Once);
        dredHandler.Verify(x => x.Invoke(false), Times.Once);
        oilHandler.Verify(x => x.Invoke(true), Times.Once);
        Assert.Equal(1f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.CompressorRelayPoint)).Value);
        Assert.Equal(1f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.OilRecoveryPoint)).Value);
    }

    [Fact]
    public void ProcessOutputs_RecordsStableCodesIntoHistory()
    {
        InvokePrivate("ProcessOutputs", OutputBlock(3));

        Assert.Equal(TemperzoneUc8History.PowerOnCode,
            Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.PowerStatePoint)).Value);
        Assert.Equal(TemperzoneUc8History.HvacCoolCode,
            Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.HvacModePoint)).Value);
    }

    [Fact]
    public void ProcessOutputs_DoesNotRecordDuplicatePolls()
    {
        InvokePrivate("ProcessOutputs", OutputBlock(3));
        InvokePrivate("ProcessOutputs", OutputBlock(3));

        Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.UnitModePoint));
        Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.IndoorFanSpeedPoint));
    }

    [Fact]
    public void ProcessExpansionValves_ReportsAndRecordsPositions()
    {
        var valve1Handler = new Mock<IntHandler>();
        var valve2Handler = new Mock<IntHandler>();
        _uc8.Exv1PositionHandler += valve1Handler.Object;
        _uc8.Exv2PositionHandler += valve2Handler.Object;

        InvokePrivate("ProcessExpansionValves", new ushort[] { 45, 80 });

        valve1Handler.Verify(x => x.Invoke(45), Times.Once);
        valve2Handler.Verify(x => x.Invoke(80), Times.Once);
        Assert.Equal(45f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.Exv1PositionPoint)).Value);
        Assert.Equal(80f, Assert.Single(_uc8.History.GetSamples(TemperzoneUc8History.Exv2PositionPoint)).Value);
    }

    [Fact]
    public void ProcessCounters_RecordsTheDocumentedCounters()
    {
        var block1 = new ushort[24];
        block1[1003 - 1001] = 100;
        var block2 = new ushort[14];
        block2[1025 - 1025] = 7;
        block2[1026 - 1025] = 5;

        InvokePrivate("ProcessCounters", block1, (ushort)1001);
        InvokePrivate("ProcessCounters", block2, (ushort)1025);

        Assert.Equal(100f, Assert.Single(_uc8.History.GetSamples("Total Hours Cooling")).Value);
        Assert.Equal(7f, Assert.Single(_uc8.History.GetSamples("Indoor Coil Sensor Faults")).Value);
        Assert.Equal(5f, Assert.Single(_uc8.History.GetSamples("Outdoor Coil Sensor Faults")).Value);
        Assert.Equal(0f, Assert.Single(_uc8.History.GetSamples("Total Minutes Cooling")).Value);
    }

    [Fact]
    public void ResetLockout_WritesTheUnlockSequenceToRegister1901()
    {
        _uc8.ResetLockout();

        Assert.Equal(new[] { "reg:1901=21930", "reg:1901=3855" }, _ops);
    }

    [Theory]
    [InlineData(2f, 50f, 400, 4500)]
    [InlineData(12f, 36f, 1200, 3600)]
    [InlineData(30f, 10f, 2000, 2000)]
    public void SetSupplyAirTargets_ClampsToTheSection11Point2Ranges(float cooling, float heating,
        int expectedCooling, int expectedHeating)
    {
        _uc8.SetSupplyAirTargets(cooling, heating);

        Assert.Equal(new[] { $"reg:118={expectedCooling}", $"reg:119={expectedHeating}" }, _ops);
    }

    [Fact]
    public void SetSupplyAirTargets_DoesNotArmAnyControlEnableBits()
    {
        _uc8.SetSupplyAirTargets(12f, 36f);

        _mockClient.Verify(x => x.WriteCoil(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
