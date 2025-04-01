using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class BlustreamAmf41WTest
{
    private BlustreamAmf41W _matrix;
    private readonly Mock<TcpClient> _mockClient = new Mock<TcpClient>("foo", BlustreamAmf41W.DefaultPort, "bar");

    public BlustreamAmf41WTest()
    {
        _matrix = new BlustreamAmf41W(_mockClient.Object, "test matrix");
    }

    [Fact]
    public void Constructor_SetsPortTo23()
    {
        _mockClient.Verify(x => x.SetPort(23), Times.Once);
    }

    [Fact]
    public void HandleResponse_ReportsOkayOnSuccess()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("[SUCCESS] Start Showing HDMI1");

        Assert.Equal(CommunicationState.Okay, _matrix.CommunicationState);
    }

    [Fact]
    public void HandleResponse_ReportsErrorOnFail()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("[FAIL] Invalid Argument");

        Assert.Equal(CommunicationState.Error, _matrix.CommunicationState);
    }

    [Fact]
    public void RouteAV_SetsHdmi1()
    {
        _matrix.RouteAV(1, 0);

        _mockClient.Verify(x => x.Send("config --source-select hdmi1\r"), Times.Once);
    }

    [Fact]
    public void RouteAV_SetsHdmi2()
    {
        _matrix.RouteAV(2, 0);

        _mockClient.Verify(x => x.Send("config --source-select hdmi2\r"), Times.Once);
    }

    [Fact]
    public void RouteAV_SetsHdmi3()
    {
        _matrix.RouteAV(3, 0);

        _mockClient.Verify(x => x.Send("config --source-select hdmi3\r"), Times.Once);
    }

    [Fact]
    public void RouteAV_SetsHdmi4()
    {
        _matrix.RouteAV(4, 0);

        _mockClient.Verify(x => x.Send("config --source-select hdmi4\r"), Times.Once);
    }

    [Fact]
    public void RouteAV_SendsToTheWindow()
    {
        _matrix.RouteAV(4, 1);

        _mockClient.Verify(x => x.Send("config --source-select hdmi4 1\r"), Times.Once);
    }
}