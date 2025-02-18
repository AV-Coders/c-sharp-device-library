using AVCoders.Core;

namespace AVCoders.Camera.Tests;

public class SonyViscaSerialTest
{
    private readonly SonyVisca _viscaCamera;
    private readonly Mock<CommunicationClient> _mockClient;

    public SonyViscaSerialTest()
    {
        _mockClient = new Mock<CommunicationClient>("foo");
        _viscaCamera = new SonyVisca(_mockClient.Object, false);
    }

    [Fact]
    public void PowerOn_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x00, 0x02, 0xFF };
        _viscaCamera.PowerOn();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOff_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x00, 0x03, 0xFF };
        _viscaCamera.PowerOff();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void ZoomStop_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x07, 0x00, 0xFF };
        _viscaCamera.ZoomStop();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void ZoomIn_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x07, 0x23, 0xFF };
        _viscaCamera.ZoomIn();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void ZoomOut_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x07, 0x33, 0xFF };
        _viscaCamera.ZoomOut();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltStop_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x03, 0x03, 0xFF };
        _viscaCamera.PanTiltStop();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltUp_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x03, 0x01, 0xFF };
        _viscaCamera.PanTiltUp();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltDown_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x03, 0x02, 0xFF };
        _viscaCamera.PanTiltDown();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltLeft_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x01, 0x03, 0xFF };
        _viscaCamera.PanTiltLeft();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PanTiltRight_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x02, 0x03, 0xFF };
        _viscaCamera.PanTiltRight();

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void RecallPreset_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x3f, 0x02, 0x01, 0xFF };
        _viscaCamera.DoRecallPreset(1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void RecallPreset_SendsThePresetNumber()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x3f, 0x02, 0x06, 0xFF };
        _viscaCamera.DoRecallPreset(6);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SavePreset_SendsTheCommand()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x3f, 0x01, 0x01, 0xFF };
        _viscaCamera.SavePreset(1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SavePreset_SendsThePresetNumber()
    {
        byte[] expectedCommand = { 0x81, 0x01, 0x04, 0x3f, 0x01, 0x06, 0xFF };
        _viscaCamera.SavePreset(6);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
}

public class SonyViscaIpTest
{
    private readonly SonyVisca _viscaCamera;
    private readonly Mock<CommunicationClient> _mockClient;

    public SonyViscaIpTest()
    {
        _mockClient = new Mock<CommunicationClient>("foo");
        _viscaCamera = new SonyVisca(_mockClient.Object, true);
    }

    [Fact]
    public void PowerOn_SendsTheIpViscaCommandHeader()
    {
        byte[] expectedCommand = { 0x01, 0x00, 0x00, 0x06, 0xff, 0xff, 0xff, 0x00,  0x81, 0x01, 0x04, 0x00, 0x02, 0xFF };
        _viscaCamera.PowerOn();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PowerOn_IncrementsTheSequenceNumber()
    {
        byte[] expectedCommand = { 0x01, 0x00, 0x00, 0x06, 0xff, 0xff, 0xff, 0x01, 0x81, 0x01, 0x04, 0x00, 0x02, 0xFF };

        //_mockClient.Setup(client => client.Send(It.IsAny<byte[]>())).Throws(new IOException("Oh No!"));
        _viscaCamera.PowerOn();
        _viscaCamera.PowerOn();
            
        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void PTZRight_SendsTheCommand()
    {
        byte[] expectedString =
            { 0x01, 0x00, 0x00, 0x09, 0xff, 0xff, 0xff, 0x00, 0x81, 0x01, 0x06, 0x01, 0x04, 0x04, 0x02, 0x03, 0xff };

        _viscaCamera.PanTiltRight();
            
        _mockClient.Verify(x => x.Send(expectedString), Times.Once);
    }

}