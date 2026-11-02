using System.Net;

namespace AVCoders.Core.Tests;

public class HostNamesTest
{
    private const string ResolvedName = "projector.example";
    private static readonly IPAddress PendingAddress = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress ResolvedAddress = IPAddress.Parse("192.0.2.2");

    private static Task<IPHostEntry> LookUp(IPAddress address) => Task.FromResult(new IPHostEntry { HostName = ResolvedName });

    [Fact]
    public void Get_ForAnAddressNotYetLookedUp_ReturnsNullWithoutWaitingForTheLookup()
    {
        var lookUp = new TaskCompletionSource<IPHostEntry>();

        var name = HostNames.Get(PendingAddress, _ => lookUp.Task);
        lookUp.SetResult(new IPHostEntry { HostName = ResolvedName });

        Assert.Null(name);
    }

    [Fact]
    public async Task Get_ReturnsTheNameOnceTheLookupFinishes()
    {
        await Wait.UntilAsync(() => HostNames.Get(ResolvedAddress, LookUp) != null, "The lookup never returned a name");

        Assert.Equal(ResolvedName, HostNames.Get(ResolvedAddress, LookUp));
    }
}
