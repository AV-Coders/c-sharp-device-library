using System.Net;
using AVCoders.Core;
using Moq;

namespace AVCoders.Power.Tests;

public class ServerEdgePduTest
{
    private readonly Mock<RestComms> _restComms;
    private readonly ServerEdgePdu _serverEdgePdu;

    public ServerEdgePduTest()
    {
        _restComms = new Mock<RestComms>("host", (ushort)1, "test");
        _serverEdgePdu = new ServerEdgePdu(_restComms.Object, "test", ServerEdgePdu.DefaultUser,
            ServerEdgePdu.DefaultPassword, 8);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 6)]
    [InlineData(3, 9)]
    [InlineData(4, 12)]
    [InlineData(5, 15)]
    [InlineData(6, 18)]
    [InlineData(7, 21)]
    
    [InlineData(8, 1)]
    [InlineData(9, 4)]
    [InlineData(10, 7)]
    [InlineData(11, 10)]
    [InlineData(12, 13)]
    [InlineData(13, 16)]
    [InlineData(14, 19)]
    [InlineData(15, 22)]
    
    [InlineData(16, 2)]
    [InlineData(17, 5)]
    [InlineData(18, 8)]
    [InlineData(19, 11)]
    [InlineData(20, 14)]
    [InlineData(21, 17)]
    [InlineData(22, 20)]
    [InlineData(23, 23)]
    public void GetNameOutletIndex_ReturnsTheIndex(int outletIndex, int expectedIndex)
    {
        Assert.Equal(expectedIndex, _serverEdgePdu.GetNameIndex(outletIndex));
    }
}