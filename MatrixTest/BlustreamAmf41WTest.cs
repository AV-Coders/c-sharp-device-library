using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

public class BlustreamAmf41WTest
{
    private readonly BlustreamAmf41W _matrix;
    private readonly Mock<TcpClient> _mockClient = TestFactory.CreateTcpClient();

    public BlustreamAmf41WTest()
    {
        _matrix = new BlustreamAmf41W(_mockClient.Object, "test matrix");
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
    public void HandleResponse_IgnoresRepliesThatAreNeitherSuccessNorFailure()
    {
        _mockClient.Object.ResponseHandlers?.Invoke("[SUCCESS] Start Showing HDMI1");

        _mockClient.Object.ResponseHandlers?.Invoke("config --source-select hdmi1\r\n");

        Assert.Equal(CommunicationState.Okay, _matrix.CommunicationState);
    }

    [Fact]
    public void RouteAV_ReportsCommunicationHasFailed()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        _matrix.RouteAV(1, 0);

        Assert.Equal(CommunicationState.Error, _matrix.CommunicationState);
    }

    [Theory]
    [InlineData(1, "config --source-select hdmi1\r")]
    [InlineData(2, "config --source-select hdmi2\r")]
    [InlineData(3, "config --source-select hdmi3\r")]
    [InlineData(4, "config --source-select hdmi4\r")]
    public void RouteAV_SetsTheHdmiInput(int input, string expectedCommand)
    {
        _matrix.RouteAV(input, 0);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void RouteAV_SendsToTheWindow()
    {
        _matrix.RouteAV(4, 1);

        _mockClient.Verify(x => x.Send("config --source-select hdmi4 1\r"), Times.Once);
    }
}