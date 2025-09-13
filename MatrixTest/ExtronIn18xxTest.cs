using System.Reflection;
using AVCoders.Core;
using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronIn18XxTest
{
    private readonly ExtronIn18Xx _switcher;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();

    public ExtronIn18XxTest()
    {
        _switcher = new ExtronIn18Xx(_mockClient.Object, 6, "test matrix");
    }

    [Fact]
    public void SendCommand_DoesNotManipulateInput()
    {
        string input = "Foo";

        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, [input]);
        _mockClient.Verify(x => x.Send(input), Times.Once);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationIsOkay()
    {
        string input = "Foo";

        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, [input]);

        Assert.Equal(CommunicationState.Okay, _switcher.CommunicationState);
    }

    [Fact]
    public void SendCommand_ReportsCommunicationHasFailed()
    {
        string input = "Foo";
        _mockClient.Setup(client => client.Send(It.IsAny<string>())).Throws(new IOException("Oh No!"));

        var method = _switcher.GetType().GetMethod("SendCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(_switcher, [input]);

        Assert.Equal(CommunicationState.Error, _switcher.CommunicationState);
    }

    [Theory]
    [InlineData(1, "1*1%")]
    [InlineData(2, "2*1%")]
    [InlineData(3, "3*1%")]
    [InlineData(4, "4*1%")]
    [InlineData(5, "5*1%")]
    [InlineData(6, "6*1%")]
    public void RouteVideo_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteVideo(input, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)] // Constructed as an in1606
    [InlineData(9)]
    public void RouteVideo_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteVideo(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RouteVideo_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "3*1%";
        _switcher.RouteVideo(3, 6);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(1, "1*1$")]
    [InlineData(2, "2*1$")]
    [InlineData(3, "3*1$")]
    [InlineData(4, "4*1$")]
    [InlineData(5, "5*1$")]
    [InlineData(6, "6*1$")]
    public void RouteAudio_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteAudio(input, 0);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void RouteAudio_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "1*1$";
        _switcher.RouteAudio(1, 10);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)] // Constructed as an in1606
    [InlineData(9)]
    public void RouteAudio_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAudio(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(1, "1*1!")]
    [InlineData(2, "2*1!")]
    [InlineData(3, "3*1!")]
    [InlineData(4, "4*1!")]
    [InlineData(5, "5*1!")]
    [InlineData(6, "6*1!")]
    public void RouteAV_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)] // Constructed as an in1606
    [InlineData(9)]
    public void RouteAV_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RouteAV_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "2*1!";
        _switcher.RouteAV(2, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_SendsTheCommand()
    {
        string expectedCommand = "\u001bT0SSAV\u0027";
        _switcher.SetSyncTimeout(0);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Fact]
    public void SetSyncTimeout_IgnoresInvalidTimeouts()
    {
        _switcher.SetSyncTimeout(502);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void SetSyncTimeout_SetsToNeverDropSync()
    {
        string expectedCommand = "\u001bT501SSAV\u0027";
        _switcher.SetSyncTimeout(501);

        _mockClient.Verify(x => x.Send(expectedCommand), Times.Once);
    }

    [Theory]
    [InlineData("IN1808\r", 8)]
    [InlineData("IN1806\r", 6)]
    public void ResponseHandler_HandlesModelNumber(string response, int expectedInputs)
    {
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedInputs, _switcher.Inputs.Count);
    }
    
    [Theory]
    [InlineData("VnamI3*BluRay\r", 2, "BluRay")]
    [InlineData("VnamI1*Doc Cam\r", 0, "Doc Cam")]
    [InlineData("VnamI7*Left Laptop\r", 6, "Left Laptop")]
    [InlineData("VnamI7*\r", 6, "")]
    public void HandleResponse_UpdatesInputName(string response, int index, string expectedName)
    {
        _mockClient.Object.ResponseHandlers!.Invoke("IN1808\r");
        _mockClient.Object.ResponseHandlers!.Invoke(response);
        
        Assert.Equal(expectedName, _switcher.Inputs[index].Name);
    }
}