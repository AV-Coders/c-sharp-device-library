using System.Net;
using AVCoders.Core;
using Moq;

namespace AVCoders.Motor.Tests;

public class GrandviewTest
{
    private readonly Mock<RestComms> _mockClient = new("host", (ushort)80, "RestClient");
    private readonly Grandview _screen;

    public GrandviewTest()
    {
        _screen = new Grandview("Screen", _mockClient.Object, RelayAction.Lower, 30);
    }

    [Fact]
    public void CommunicationClient_IsTheRestClient()
    {
        Assert.Same(_mockClient.Object, _screen.CommunicationClient);
    }

    [Fact]
    public void SuccessfulResponse_SetsTheCommunicationStateToOkay()
    {
        _mockClient.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(CommunicationState.Okay, _screen.CommunicationState);
    }

    [Fact]
    public void UnsuccessfulResponse_SetsTheCommunicationStateToError()
    {
        _mockClient.Object.HttpResponseHandlers?.Invoke(new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(CommunicationState.Error, _screen.CommunicationState);
    }

    [Fact]
    public void ClientError_SetsTheCommunicationStateToError()
    {
        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Error);

        Assert.Equal(CommunicationState.Error, _screen.CommunicationState);
    }
}
