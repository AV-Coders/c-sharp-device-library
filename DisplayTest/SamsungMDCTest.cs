using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Microsoft.Extensions.Logging;
using Moq;

namespace AVCoders.Display.Tests;

[Collection(LoggerFactoryCollection.Name)]
public class SamsungMDCTest
{
    private readonly SamsungMdc _samsungMdc;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();
    private readonly byte _displayId = 0x00;

    public SamsungMDCTest()
    {
        _samsungMdc = new SamsungMdc(_mockClient.Object, _displayId, "Test display", Input.Hdmi2);
    }

    [Fact]
    public void SendByteArray_DoesNotManipulateInput()
    {
        byte[] input = [0x41, 0x0A];

        var method = _samsungMdc.GetType().GetMethod("SendByteArray", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_samsungMdc, [input]);
        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendByteArray_ReportsCommunicationIsOkay()
    {
        byte[] input = [0x41, 0x0A];

        var method = _samsungMdc.GetType().GetMethod("SendByteArray", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_samsungMdc, [input]);

        Assert.Equal(CommunicationState.Okay, _samsungMdc.CommunicationState);
    }

    [Fact]
    public void SendByteArray_ReportsCommunicationHasFailed()
    {
        byte[] input = [0x41, 0x0A];

        _mockClient.Setup(client => client.Send(It.IsAny<byte[]>())).Throws(new IOException("Oh No!"));
        var method = _samsungMdc.GetType().GetMethod("SendByteArray", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_samsungMdc, [input]);

        Assert.Equal(CommunicationState.Error, _samsungMdc.CommunicationState);
    }

    [Fact]
    public void PowerOn_SendsThePowerOnCommand()
    {
        byte[] expectedPowerOnCommand = [0xAA, 0x11, 0x00, 0x01, 0x01, 0x13];
        _samsungMdc.PowerOn();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_UsesTheCorrectDisplayId()
    {
        SamsungMdc samsungMdcForDisplay2 = new SamsungMdc(_mockClient.Object, 0x02, "Test display", Input.Hdmi1);
        byte[] expectedPowerOnCommand = [0xAA, 0x11, 0x02, 0x01, 0x01, 0x15];

        samsungMdcForDisplay2.PowerOn();
        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsThePowerOffCommand()
    {
        byte[] expectedPowerOnCommand = [0xAA, 0x11, 0x00, 0x01, 0x00, 0x12];
        _samsungMdc.PowerOff();

        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_UsesTheCorrectDisplayId()
    {
        SamsungMdc samsungMdcForDisplay2 = new SamsungMdc(_mockClient.Object, 0x03, "Test display", Input.Hdmi1);
        byte[] expectedPowerOnCommand = [0xAA, 0x11, 0x03, 0x01, 0x00, 0x15];

        samsungMdcForDisplay2.PowerOff();
        _mockClient.Verify(x => x.Send(expectedPowerOnCommand), Times.Once);
    }

    [Fact]
    public void GenerateChecksum_AddsAllValues()
    {
        byte[] input = [0x11, 0x00, 0x01, 0x01];

        var method = _samsungMdc.GetType()
            .GetMethod("GenerateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_samsungMdc, [input]) ?? 0x00);
        Assert.Equal(0x13, result);
    }

    [Fact]
    public void GenerateChecksum_IgnoresLeading0xAA()
    {
        byte[] input = [0xAA, 0x11, 0x00, 0x01, 0x00];

        var method = _samsungMdc.GetType()
            .GetMethod("GenerateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_samsungMdc, [input]) ?? 0x00);
        Assert.Equal(0x12, result);
    }

    [Fact]
    public void GenerateChecksum_ReturnsOnlyTheLastTwoDigits()
    {
        byte[] input = [0xAA, 0xF1, 0xF0, 0xF1, 0x00];

        var method = _samsungMdc.GetType()
            .GetMethod("GenerateChecksum", BindingFlags.Instance | BindingFlags.NonPublic);

        byte result = (byte)(method?.Invoke(_samsungMdc, [input]) ?? 0x00);
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

    [Theory]
    [InlineData(new byte[] { 0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x11, 0x01, 0x55 }, PowerState.On)]
    [InlineData(new byte[] { 0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x11, 0x00, 0x54 }, PowerState.Off)]
    public void HandleResponse_UpdatesThePowerState(byte[] response, PowerState expectedState)
    {
        _samsungMdc.HandleResponse(response);
        
        Assert.Equal(expectedState, _samsungMdc.PowerState);
    }

    [Fact]
    public void HandleResponse_DoesntForceAnUnknownPowerState()
    {
        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x11, 0x00, 0x54]);
        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x11, 0x01, 0x55]);
        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x11, 0x02, 0x56]);
        
        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }

    [Theory]
    [InlineData(new byte[] { 0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x12, 0x01, 0x56 }, 1)]
    [InlineData(new byte[] { 0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x12, 0x0D, 0x62 }, 13)]
    public void HandleResponse_UpdatesTheVolumeLevel(byte[] response, int expectedState)
    {
        Mock<VolumeLevelHandler> mockHandler = new Mock<VolumeLevelHandler>();

        _samsungMdc.VolumeLevelHandlers += mockHandler.Object;
        _samsungMdc.HandleResponse(response);
        
        mockHandler.Verify(x => x.Invoke(expectedState));
    }
    
    [Theory]
    [InlineData(new byte[] { 0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x13, 0x00, 0x56 }, MuteState.Off)]
    [InlineData(new byte[] { 0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x13, 0x01, 0x57 }, MuteState.On)]
    public void HandleResponse_UpdatesTheMuteState(byte[] response, MuteState expectedState)
    {
        Mock<MuteStateHandler> mockHandler = new Mock<MuteStateHandler>();

        _samsungMdc.MuteStateHandlers += mockHandler.Object;
        _samsungMdc.HandleResponse(response);
        
        mockHandler.Verify(x => x.Invoke(expectedState));
    }

    [Fact]
    public void HandleResponse_GivenTheDtvInputCode_ReportsDvbtTuner()
    {
        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x14, 0x40, 0x97]);

        Assert.Equal(Input.DvbtTuner, _samsungMdc.Input);
    }

    [Theory]
    [InlineData((byte)0x22, (byte)0x79, Input.Hdmi1)]
    [InlineData((byte)0x24, (byte)0x7B, Input.Hdmi2)]
    [InlineData((byte)0x32, (byte)0x89, Input.Hdmi3)]
    [InlineData((byte)0x34, (byte)0x8B, Input.Hdmi4)]
    public void HandleResponse_GivenAnHdmiPcInputCode_ReportsTheHdmiInput(byte code, byte checksum, Input expected)
    {
        var (display, logs) = CreateDisplayWithCapturedLogs();

        display.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x14, code, checksum]);

        Assert.Equal(expected, display.Input);
        Assert.DoesNotContain(logs.Logs, l => l.Message.StartsWith("Unknown input code"));
    }

    [Fact]
    public void HandleResponse_GivenAnUnhandledInputCode_ReportsUnknownAndWarnsOnce()
    {
        var (display, logs) = CreateDisplayWithCapturedLogs();
        byte[] unhandled = [0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x14, 0x60, 0xB7];
        var message = $"Unknown input code 0x60 in {Convert.ToHexString(unhandled)}";
        display.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x14, 0x21, 0x78]);

        display.HandleResponse(unhandled);
        display.HandleResponse(unhandled);

        Assert.Equal(Input.Unknown, display.Input);
        Assert.Equal([LogLevel.Warning, LogLevel.Debug],
            logs.Logs.Where(l => l.Message == message).Select(l => l.Level));
    }

    [Fact]
    public void HandleResponse_GivenAnUnhandledInputCode_ForcesTheDesiredInput()
    {
        _samsungMdc.PowerOn();
        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x14, 0x23, 0x7A]);

        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x14, 0x50, 0xA7]);

        _mockClient.Verify(x => x.Send(new byte[] { 0xAA, 0x14, 0x00, 0x01, 0x23, 0x38 }), Times.Once);
    }

    [Fact]
    public void HandleResponse_HandlesPartialResponses()
    {
        Mock<VolumeLevelHandler> mockHandler = new Mock<VolumeLevelHandler>();
        byte[] response1 = [0xAA, 0xFF, 0x00, 0x03];
        byte[] response2 = [(byte)'A', 0x12, 0x0D, 0x62];
        
        // payload is 8, data length is 3?

        _samsungMdc.VolumeLevelHandlers += mockHandler.Object;
        _samsungMdc.HandleResponse(response1);
        _samsungMdc.HandleResponse(response2);
        
        mockHandler.Verify(x => x.Invoke(13));
    }

    [Fact]
    public void HandleResponse_GivenAWrongReplyHeader_IgnoresTheReply()
    {
        _samsungMdc.HandleResponse([0xAA, 0x00, 0x00, 0x03, (byte)'A', 0x11, 0x01, 0x56]);

        Assert.Equal(PowerState.Unknown, _samsungMdc.PowerState);
        Assert.Contains(_samsungMdc.Events, e => e.Type == EventType.Error && e.Info == "The response does not have the correct header");
    }

    [Fact]
    public void HandleResponse_GivenAWrongChecksum_IgnoresTheReply()
    {
        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x11, 0x01, 0x00]);

        Assert.Equal(PowerState.Unknown, _samsungMdc.PowerState);
        Assert.Contains(_samsungMdc.Events, e => e.Type == EventType.Error && e.Info == "The response checksum is wrong");
    }

    [Fact]
    public void HandleResponse_GivenAShortFrame_ReportsItWithoutThrowing()
    {
        var exception = Record.Exception(() => _samsungMdc.HandleResponse([0xAA, 0xFF, 0x00, 0x00, 0xFF]));

        Assert.Null(exception);
        Assert.Contains(_samsungMdc.Events, e => e.Type == EventType.Error && e.Info == "The response is too short");
    }

    [Fact]
    public void HandleResponse_HandlesMoreThan1024BytesOfWholeFramesInOneReceive()
    {
        List<int> volumes = [];
        _samsungMdc.VolumeLevelHandlers += volumes.Add;
        byte[] burst = Enumerable.Range(1, 129)
            .SelectMany(volume => new byte[] { 0xAA, 0xFF, 0x00, 0x03, (byte)'A', 0x12, (byte)volume, (byte)(0x55 + volume) })
            .ToArray();

        _samsungMdc.HandleResponse(burst);

        Assert.True(burst.Length > 1024);
        Assert.Equal(Enumerable.Range(1, 129), volumes);
    }

    [Fact]
    public void HandleResponse_IgnoresAReplyFromAnotherDisplay()
    {
        _samsungMdc.HandleResponse([0xAA, 0xFF, 0x01, 0x03, (byte)'A', 0x11, 0x01, 0x56]);

        Assert.Equal(PowerState.Unknown, _samsungMdc.PowerState);
        Assert.Equal(CommunicationState.NotAttempted, _samsungMdc.CommunicationState);
    }

    [Fact]
    public void HandleResponse_HandlesAReplyFromTheConfiguredDisplay()
    {
        SamsungMdc display5 = new SamsungMdc(_mockClient.Object, 0x05, "Test display", Input.Hdmi1);

        display5.HandleResponse([0xAA, 0xFF, 0x05, 0x03, (byte)'A', 0x11, 0x01, 0x5A]);

        Assert.Equal(PowerState.On, display5.PowerState);
        Assert.Equal(CommunicationState.Okay, display5.CommunicationState);
    }

    [Theory]
    [InlineData((byte)0xFE)]
    [InlineData((byte)0xFF)]
    public void HandleResponse_GivenAnIdForEveryDisplay_AcceptsAReplyFromAnyDisplay(byte displayId)
    {
        SamsungMdc allDisplays = new SamsungMdc(_mockClient.Object, displayId, "Test display", Input.Hdmi1);

        allDisplays.HandleResponse([0xAA, 0xFF, 0x01, 0x03, (byte)'A', 0x11, 0x01, 0x56]);

        Assert.Equal(PowerState.On, allDisplays.PowerState);
    }

    [Fact]
    public async Task Poll_GivenASendFailure_ReportsCommunicationHasFailed()
    {
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, ConnectionState.Connected);
        _mockClient.Setup(client => client.Send(It.IsAny<byte[]>())).Throws(new IOException("Oh No!"));

        await (Task)typeof(SamsungMdc).GetMethod("DoPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_samsungMdc, [CancellationToken.None])!;

        Assert.Equal(CommunicationState.Error, _samsungMdc.CommunicationState);
    }

    [Fact]
    public async Task Poll_GivenASuccessfulSend_LeavesTheCommunicationStateAlone()
    {
        typeof(CommunicationClient).GetField("_connectionState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_mockClient.Object, ConnectionState.Connected);

        await (Task)typeof(SamsungMdc).GetMethod("DoPoll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_samsungMdc, [CancellationToken.None])!;

        _mockClient.Verify(x => x.Send(new byte[] { 0xAA, 0x11, 0x00, 0x00, 0x11 }), Times.Once);
        Assert.Equal(CommunicationState.NotAttempted, _samsungMdc.CommunicationState);
    }

    [Fact]
    public void SetAudioMute_Unknown_SendsNothing()
    {
        var exception = Record.Exception(() => _samsungMdc.SetAudioMute(MuteState.Unknown));

        Assert.Null(exception);
        _mockClient.Verify(x => x.Send(It.IsAny<byte[]>()), Times.Never);
    }

    private (SamsungMdc Display, ScopeCapturingLoggerFactory Logs) CreateDisplayWithCapturedLogs()
    {
        var logs = new ScopeCapturingLoggerFactory();
        var originalLoggerFactory = LogBase.LoggerFactory;
        LogBase.LoggerFactory = logs;
        try
        {
            return (new SamsungMdc(_mockClient.Object, _displayId, "Test display", Input.Hdmi2), logs);
        }
        finally
        {
            LogBase.LoggerFactory = originalLoggerFactory;
        }
    }
}