using AVCoders.Core;
using Moq;

namespace AVCoders.Matrix.Tests;

public class ExtronSwTest
{
    private readonly ExtronSw _switcher;
    private readonly Mock<CommunicationClient> _mockClient = new("foo", "bar", (ushort)1);

    public ExtronSwTest()
    {
        _switcher = new ExtronSw(_mockClient.Object, 4, "Test switch");
    }

    [Theory]
    [InlineData(1, "1!")]
    [InlineData(2, "2!")]
    [InlineData(3, "3!")]
    [InlineData(4, "4!")]
    public void RouteAV_SendsTheCommand(int input, string expectedRouteCommand)
    {
        _switcher.RouteAV(input, 3);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)] // Constructed as an SW4
    [InlineData(9)]
    public void RouteAV_IgnoresInvalidInputNumbers(int input)
    {
        _switcher.RouteAV(input, 0);

        _mockClient.Verify(x => x.Send(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RouteAV_IgnoresOutputParameter()
    {
        string expectedRouteCommand = "2!";
        _switcher.RouteAV(2, 1);

        _mockClient.Verify(x => x.Send(expectedRouteCommand), Times.Once);
    }
}