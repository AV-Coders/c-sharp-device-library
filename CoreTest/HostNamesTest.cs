using System.Net;

namespace AVCoders.Core.Tests;

public class HostNamesTest
{
    [Fact]
    public void Get_ForAnAddressNotYetLookedUp_ReturnsNullWithoutWaitingForTheLookup()
    {
        Assert.Null(HostNames.Get(IPAddress.IPv6Loopback));
    }

    [Fact]
    public async Task Get_ForLoopback_ReturnsTheNameOnceTheLookupFinishes()
    {
        await Wait.UntilAsync(() => HostNames.Get(IPAddress.Loopback) != null, "The loopback lookup never returned a name");
    }
}
