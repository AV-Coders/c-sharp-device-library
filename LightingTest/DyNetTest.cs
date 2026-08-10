using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Lighting.Tests;

public class DyNetTest
{
    private readonly DyNet _dyNet;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();

    public DyNetTest()
    {
        _dyNet = new DyNet(_mockClient.Object, "name");
    }
    
    [Theory]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x00, 0x00, 0x00, 0xFF }, 0x60)]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x01, 0x00, 0x00, 0xFF }, 0x5F)]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x02, 0x00, 0x00, 0xFF }, 0x5E)]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x03, 0x00, 0x00, 0xFF }, 0x5D)]
    public void CalcualteChecksum_ReturnsTheChecksum(byte[] input, byte expected)
    {
        byte actual = DyNet.CalculateChecksum(input);
        
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(33, 1, 100, new byte[] { 0x1C, 0x21, 0x64, 0x00, 0x00, 0x00, 0xFF, 0x60 })]
    [InlineData(33, 2, 100, new byte[] { 0x1C, 0x21, 0x64, 0x01, 0x00, 0x00, 0xFF, 0x5F })]
    [InlineData(33, 3, 100, new byte[] { 0x1C, 0x21, 0x64, 0x02, 0x00, 0x00, 0xFF, 0x5E })]
    [InlineData(33, 4, 100, new byte[] { 0x1C, 0x21, 0x64, 0x03, 0x00, 0x00, 0xFF, 0x5D })]
    public void SelectCurrentPreset_SendsTheExpectedCommand(byte area, byte preset, byte rampTime, byte[] expectedCommand)
    {
        _dyNet.SelectCurrentPreset(area, preset, rampTime);

        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(new byte[] { 0x1C, 0x23, 0x64, 0x03, 0x00, 0x00, 0xFF, 0x5B }, "Area 35 recalled preset 4")]
    [InlineData(new byte[] { 0x1C, 0x23, 0x64, 0x02, 0x00, 0x00, 0xFF, 0x5C }, "Area 35 recalled preset 3")]
    [InlineData(new byte[] { 0x1C, 0x23, 0x64, 0x01, 0x00, 0x00, 0xFF, 0x5D }, "Area 35 recalled preset 2")]
    [InlineData(new byte[] { 0x1C, 0x23, 0x64, 0x00, 0x00, 0x00, 0xFF, 0x5E }, "Area 35 recalled preset 1")]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x00, 0x00, 0x00, 0xFF, 0x60 }, "Area 33 recalled preset 1")]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x0A, 0x00, 0x00, 0xFF, 0x56 }, "Area 33 recalled preset 5")]
    [InlineData(new byte[] { 0x1C, 0x21, 0x64, 0x01, 0x00, 0x01, 0xFF, 0x5E }, "Area 33 recalled preset 10")]
    [InlineData(new byte[] { 0x1C, 0x17, 0x00, 0x63, 0x00, 0x00, 0xFF, 0x6B }, "Current preset requested for area 23")]
    public void HandleResponse_AddsPresetEvents(byte[] response, string expectedInfo)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);

        Assert.Contains(_dyNet.Events, e => e.Type == EventType.Preset && e.Info == expectedInfo);
    }

    [Theory]
    [InlineData(33, 100, 100, new byte[] { 0x1C, 0x21, 0xFF, 0x71, 0x01, 0x64, 0xFF, 0xEF })]
    [InlineData(33, 50, 100, new byte[] { 0x1C, 0x21, 0xFF, 0x71, 0x80, 0x64, 0xFF, 0x70 })]
    [InlineData(33, 0, 100, new byte[] { 0x1C, 0x21, 0xFF, 0x71, 0xFF, 0x64, 0xFF, 0xF1 })]
    public void RampAreaToLevel_SendsInvertedLevels(byte area, int level, byte rampTime, byte[] expectedCommand)
    {
        _dyNet.RampAreaToLevel(area, level, rampTime);

        _mockClient.Verify(x => x.Send(expectedCommand));
    }

    [Theory]
    [InlineData(new byte[] { 0x1C, 0x23, 0xFD, 0x79, 0xFA, 0x00, 0xFF, 0x52 }, "Area 35 fading to 1% over 5s")]
    [InlineData(new byte[] { 0x1C, 0x23, 0x01, 0x79, 0xFA, 0x00, 0xFF, 0x4E }, "Area 35 fading to 100% over 5s")]
    [InlineData(new byte[] { 0x1C, 0x23, 0xFF, 0x76, 0x00, 0x00, 0xFF, 0x4D }, "Area 35 stopped fading")]
    public void HandleResponse_AddsLevelEvents(byte[] response, string expectedInfo)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);

        Assert.Contains(_dyNet.Events, e => e.Type == EventType.Level && e.Info == expectedInfo);
    }

    [Fact]
    public void HandleResponse_HandlesAFadeSequence()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0xFD, 0x79, 0xFA, 0x00, 0xFF, 0x52]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0xFF, 0x76, 0x00, 0x00, 0xFF, 0x4D]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0x01, 0x79, 0xFA, 0x00, 0xFF, 0x4E]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x5C, 0xFD, 0x04, 0x00, 0x03, 0x23, 0x00, 0x7D]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0xFF, 0x76, 0x00, 0x00, 0xFF, 0x4D]);

        var levelEvents = _dyNet.Events.Where(e => e.Type == EventType.Level).Select(e => e.Info).ToList();
        Assert.Equal([
            "Area 35 fading to 1% over 5s",
            "Area 35 stopped fading",
            "Area 35 fading to 100% over 5s",
            "Area 35 stopped fading"
        ], levelEvents);
    }

    [Fact]
    public void HandleResponse_AddsAnEventPerFrameInASequence()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0x64, 0x03, 0x00, 0x00, 0xFF, 0x5B]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0x64, 0x02, 0x00, 0x00, 0xFF, 0x5C]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0x64, 0x01, 0x00, 0x00, 0xFF, 0x5D]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0x64, 0x00, 0x00, 0x00, 0xFF, 0x5E]);

        var presetEvents = _dyNet.Events.Where(e => e.Type == EventType.Preset).Select(e => e.Info).ToList();
        Assert.Equal([
            "Area 35 recalled preset 4",
            "Area 35 recalled preset 3",
            "Area 35 recalled preset 2",
            "Area 35 recalled preset 1"
        ], presetEvents);
    }

    [Fact]
    public void HandleResponse_ReassemblesSplitFrames()
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x1C, 0x23, 0x64]);
        _mockClient.Object.ResponseByteHandlers!.Invoke([0x03, 0x00, 0x00, 0xFF, 0x5B]);

        Assert.Contains(_dyNet.Events, e => e.Type == EventType.Preset && e.Info == "Area 35 recalled preset 4");
    }

    [Theory]
    [InlineData(new byte[] { 0x1C, 0x23, 0x64, 0x03, 0x00, 0x00, 0xFF, 0x00 })] // Bad checksum
    [InlineData(new byte[] { 0x1C, 0x1C, 0xFF, 0x2E, 0x02, 0x1F, 0xFF, 0x7B })] // Unknown opcode
    [InlineData(new byte[] { 0x5C, 0xFD, 0x04, 0x00, 0x03, 0x23, 0x00, 0x7D })] // Physical addressing scheme
    public void HandleResponse_IgnoresOtherFrames(byte[] response)
    {
        _mockClient.Object.ResponseByteHandlers!.Invoke(response);

        Assert.Empty(_dyNet.Events);
    }
}