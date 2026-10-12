using System.Text;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Lighting.Tests;

public class CBusLightTest
{
    private readonly CBusLight _light;
    private readonly CBusSerialInterface _interface;
    private readonly ManualTimeProvider _time = new();
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly List<string> _sent = [];

    public CBusLightTest()
    {
        _mockClient.Setup(x => x.Send(It.IsAny<string>())).Callback<string>(_sent.Add);
        _interface = new CBusSerialInterface(_mockClient.Object, _time);
        _light = new CBusLight("Test Light", _interface, 0x19, CBusRampTime.EightSeconds, 0x74, 0x01, timeProvider: _time);
    }

    private void Receive(string text)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(Encoding.ASCII.GetBytes(text));
    }

    private void Confirm() => Receive("r.");

    [Fact]
    public void PowerOn_SendsTheCommandAndSetsFullLevel()
    {
        _light.PowerOn();

        Assert.Equal("\\0538007919r\r", _sent.Last());
        Assert.Equal(0xFF, _light.Level);
        Assert.Equal(100u, _light.Brightness);
        Assert.Equal(PowerState.On, _light.PowerState);
    }

    [Fact]
    public void PowerOff_SendsTheCommandAndSetsZeroLevel()
    {
        _light.PowerOn();
        Confirm();
        _light.PowerOff();

        Assert.Equal("\\0538000119r\r", _sent.Last());
        Assert.Equal(0, _light.Level);
        Assert.Equal(PowerState.Off, _light.PowerState);
    }

    [Theory]
    [InlineData(0, "\\053800121900r\r", 0x00)]
    [InlineData(2, "\\053800121905r\r", 0x05)]
    [InlineData(50, "\\053800121980r\r", 0x80)]
    [InlineData(100, "\\0538001219FFr\r", 0xFF)]
    public void SetLevel_ScalesPercentToTheFullByteRange(int level, string expected, byte expectedLevel)
    {
        _light.SetLevel(level);

        Assert.Equal(expected, _sent.Last());
        Assert.Equal(expectedLevel, _light.Level);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void SetLevelWithRampTime_OutOfRange_SendsNothing(int level)
    {
        _light.SetLevel(level, CBusRampTime.Instant);

        Assert.Empty(_sent);
    }

    [Fact]
    public void SetLevelWithRampTime_UsesTheRampCode()
    {
        _light.SetLevel(2, CBusRampTime.Instant);

        Assert.Equal("\\053800021905r\r", _sent.Last());
    }

    [Fact]
    public void RampUp_RampsToFullOverTheDefaultRampTime()
    {
        _light.RampUp();

        Assert.Equal("\\0538001219FFr\r", _sent.Last());
    }

    [Fact]
    public void RampDown_RampsToZeroOverTheDefaultRampTime()
    {
        _light.RampDown();

        Assert.Equal("\\053800121900r\r", _sent.Last());
    }

    [Fact]
    public void StopRamping_SendsTerminateRamp()
    {
        _light.RampUp();
        Confirm();
        _light.StopRamping();

        Assert.Equal("\\0538000919r\r", _sent.Last());
    }

    [Fact]
    public void StopRamping_DuringASetLevelRamp_SendsTerminateRampAndLeavesTheLevel()
    {
        _light.SetLevel(50, CBusRampTime.OneMinute);
        Confirm();
        _time.Advance(TimeSpan.FromSeconds(4));
        _light.StopRamping();

        Assert.Equal("\\0538000919r\r", _sent.Last());
        Assert.Equal(0x80, _light.Level);
    }

    [Fact]
    public void StopRampingAtTrackedLevel_AfterHalfARampUp_SetsTheTrackedLevelInstantly()
    {
        _light.RampUp();
        Confirm();
        _time.Advance(TimeSpan.FromSeconds(4));
        _light.StopRampingAtTrackedLevel();

        Assert.Equal("\\053800021980r\r", _sent.Last());
        Assert.Equal(0x80, _light.Level);
    }

    [Fact]
    public void StopRampingAtTrackedLevel_AfterAFullRampDown_SetsZero()
    {
        _light.PowerOn();
        Confirm();
        _light.RampDown();
        Confirm();
        _time.Advance(TimeSpan.FromSeconds(10));
        _light.StopRampingAtTrackedLevel();

        Assert.Equal("\\053800021900r\r", _sent.Last());
        Assert.Equal(0, _light.Level);
    }

    [Fact]
    public void RequestStatus_AsksTheOutputUnitChannel()
    {
        _light.RequestStatus();

        Assert.Equal("\\0674002A0101r\r", _sent.Last());
    }

    [Fact]
    public void BusOn_ForThisGroup_SetsFullLevel()
    {
        Receive("05533800791936\r\n");

        Assert.Equal(0xFF, _light.Level);
    }

    [Fact]
    public void BusOn_ForAnotherGroup_IsIgnored()
    {
        Receive("0553380079C532\r\n");

        Assert.Equal(0, _light.Level);
    }

    [Fact]
    public void BusRamp_SetsTheTargetLevel()
    {
        Receive("05533800121980E5\r\n");

        Assert.Equal(0x80, _light.Level);
    }

    [Fact]
    public void StatusReply_ForThisChannel_SetsTheLevel()
    {
        Receive("867400008201406A\r\n");

        Assert.Equal(0x40, _light.Level);
    }

    [Fact]
    public void StatusReply_ForAnotherChannel_IsIgnored()
    {
        Receive("867400008202406A\r\n");

        Assert.Equal(0, _light.Level);
    }

    [Fact]
    public void LevelChange_InvokesTheHandlers()
    {
        int reported = -1;
        _light.LevelChangeHandlers += level => reported = level;

        _light.PowerOn();

        Assert.Equal(0xFF, reported);
    }
}
