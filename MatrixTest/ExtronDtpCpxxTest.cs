using System.Reflection;
using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronDtpCpxxTest
{
    private readonly ExtronDtpCpxx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = new Mock<CommunicationClient>("foo");

    public ExtronDtpCpxxTest()
    {
        _switcher = new ExtronDtpCpxx(_mockClient.Object, 8, "test matrix");
    }

    [Fact]
    public void SendCommand_DoesNotManipulateInput()
    {
        String input = "Foo";

        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, new[] { input });
        
        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationIsOkay()
    {
        String input = "Foo";
        
        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, new[] { input });

        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        String input = "Foo";

        _mockClient.Setup(client => client.Send(It.IsAny<String>())).Throws(new IOException("Oh No!"));
        
        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, new[] { input });

        Assert.Equal(CommunicationState.Error, _switcher.CommunicationState);
    }

    [Fact]
    public void RouteVideo_SendsTheCommand()
    {
        String expectedRouteCommand = "1*3%";
        _switcher.RouteVideo(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteVideo_RoutesToAllWithOutput0()
    {
        String expectedRouteCommand = "3*%";
        _switcher.RouteVideo(3, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_SendsTheCommand()
    {
        String expectedRouteCommand = "1*3$";
        _switcher.RouteAudio(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_RoutesToAllWithOutput0()
    {
        String expectedRouteCommand = "1*$";
        _switcher.RouteAudio(1, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAV_SendsTheCommand()
    {
        String expectedRouteCommand = "1*3!";
        _switcher.RouteAV(1, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAV_RoutesToAllWithOutput0()
    {
        String expectedRouteCommand = "1*!";
        _switcher.RouteAV(1, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_SendsTheCommand()
    {
        String expectedCommand = "\u001bT0*3SSAV\u0027";
        _switcher.SetSyncTimeout(0, 3);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_IgnoresInvalidTimeouts()
    {
        _switcher.SetSyncTimeout(502, 1);

        _mockClient.Verify(x => x.Send(It.IsAny<String>()), Times.Never);
    }

    [Fact]
    public void SetSyncTimeout_SetsToNeverDropSync()
    {
        String expectedCommand = "\u001bT501*1SSAV\u0027";
        _switcher.SetSyncTimeout(501, 1);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }
}