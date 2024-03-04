using System.Reflection;
using AVCoders.Core;
using Moq;

namespace AVCoders.Display.Tests;

public class SamsungMDCTest
{
    private SamsungMdc _samsungMdc;
    private readonly Mock<TcpClient> _mockClient;

    public SamsungMDCTest()
    {
        _mockClient = new Mock<TcpClient>("foo", (ushort)1);
        _samsungMdc = new SamsungMdc(0x00, _mockClient.Object);
    }

    [Fact]
    public void Constructor_SetsPortTo1515()
    {
        _mockClient.Verify(x => x.SetPort(1515), Times.Once);
    }

    [Fact]
    public void SendByteArray_DoesNotManipulateInput()
    {
        byte[] input = { 0x41, 0x0A };

        var method = _samsungMdc.GetType().GetMethod("SendByteArray", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_samsungMdc, new[] { input });
        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendByteArray_ReportsCommunicationIsOkay()
    {
        byte[] input = { 0x41, 0x0A };

        var method = _samsungMdc.GetType().GetMethod("SendByteArray", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_samsungMdc, new[] { input });

        Assert.Equal(CommunicationState.Okay, _samsungMdc.GetCurrentCommunicationState());
    }

    [Fact]
    public void SendByteArray_ReportsCommunicationHasFailed()
    {
        byte[] input = { 0x41, 0x0A };

        _mockClient.Setup(client => client.Send(It.IsAny<byte[]>())).Throws(new IOException("Oh No!"));
        var method = _samsungMdc.GetType().GetMethod("SendByteArray", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_samsungMdc, new[] { input });

        Assert.Equal(CommunicationState.Error, _samsungMdc.GetCurrentCommunicationState());
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x00, 0x01, 0x01, 0x13 };
        _samsungMdc.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_UsesTheCorrectDisplayId()
    {
        SamsungMdc samsungMdcForDisplay2 = new SamsungMdc(0x02, _mockClient.Object);
        byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x02, 0x01, 0x01, 0x15 };

        samsungMdcForDisplay2.PowerOn();
        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_UpdatesInternalPowerState()
    {
        _samsungMdc.PowerOn();

        Assert.Equal(PowerState.On, _samsungMdc.GetCurrentPowerState());
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x00, 0x01, 0x00, 0x12 };
        _samsungMdc.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_UsesTheCorrectDisplayId()
    {
        SamsungMdc samsungMdcForDisplay2 = new SamsungMdc(0x03, _mockClient.Object);
        byte[] expectedPowerOnCommand = { 0xAA, 0x11, 0x03, 0x01, 0x00, 0x15 };

        samsungMdcForDisplay2.PowerOff();
        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_UpdatesInternalPowerState()
    {
        _samsungMdc.PowerOff();

        Assert.Equal(PowerState.Off, _samsungMdc.GetCurrentPowerState());
    }

    [Fact]
    public void GenerateChecksum_AddsAllValues()
    {
        byte[] input = { 0x11, 0x00, 0x01, 0x01 };

        var method = _samsungMdc.GetType()
            .GetMethod("GenerateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_samsungMdc, new[] { input }) ?? 0x00);
        Assert.Equal(0x13, result);
    }

    [Fact]
    public void GenerateChecksum_IgnoresLeading0xAA()
    {
        byte[] input = { 0xAA, 0x11, 0x00, 0x01, 0x00 };

        var method = _samsungMdc.GetType()
            .GetMethod("GenerateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_samsungMdc, new[] { input }) ?? 0x00);
        Assert.Equal(0x12, result);
    }

    [Fact]
    public void GenerateChecksum_ReturnsOnlyTheLastTwoDigits()
    {
        byte[] input = { 0xAA, 0xF1, 0xF0, 0xF1, 0x00 };

        var method = _samsungMdc.GetType()
            .GetMethod("GenerateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_samsungMdc, new[] { input }) ?? 0x00);
        Assert.Equal(0xD2, result);
    }

    [Fact]
    public void HandleResponse_DoesntThrow()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("foo");
    }

    [Theory]
    [InlineData(Input.Hdmi1, new byte[] { 0xAA, 0x14, 0x00, 0x01, 0x21, 0x36 })]
    [InlineData(Input.Hdmi2, new byte[] { 0xAA, 0x14, 0x00, 0x01, 0x23, 0x38 })]
    [InlineData(Input.Hdmi3, new byte[] { 0xAA, 0x14, 0x00, 0x01, 0x31, 0x46 })]
    [InlineData(Input.Hdmi4, new byte[] { 0xAA, 0x14, 0x00, 0x01, 0x33, 0x48 })]
    [InlineData(Input.DvbtTuner, new byte[] { 0xAA, 0x14, 0x00, 0x01, 0x40, 0x55 })]
    public void SetInput_SendsTheExpectedCommand(Input source, byte[] command)
    {
        _samsungMdc.SetInput(source);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Fact]
    public void SetInput_IgnoresMissingInputs()
    {
        _samsungMdc.SetInput(Input.Unknown);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }

    [Theory]
    [InlineData(0, new byte[] { 0xAA, 0x12, 0x00, 0x01, 0x00, 0x13 })]
    [InlineData(100, new byte[] { 0xAA, 0x12, 0x00, 0x01, 0x64, 0x77 })]
    public void SetVolume_SendsTheExpectedCommand(int volume, byte[] command)
    {
        _samsungMdc.SetVolume(volume);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(256)]
    public void SetVolume_IgnoresInvalidValues(int volume)
    {
        _samsungMdc.SetVolume(volume);

        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }

    [Theory]
    [InlineData(MuteState.On, new byte[] { 0xAA, 0x13, 0x00, 0x01, 0x01, 0x15 })]
    [InlineData(MuteState.Off, new byte[] { 0xAA, 0x13, 0x00, 0x01, 0x00, 0x14 })]
    public void SetMute_SendsTheExpectedCommand(MuteState state, byte[] command)
    {
        _samsungMdc.SetAudioMute(state);

        _mockClient.Verify(x => x.Send(command), Times.Once);
    }
}