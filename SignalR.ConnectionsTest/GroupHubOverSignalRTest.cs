using AVCoders.Core.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Connections.Tests;

[Collection(nameof(TestHub))]
public class GroupHubOverSignalRTest : IAsyncLifetime
{
    private const string HubPath = "/hubs/test";
    private readonly string _groupName = $"room-{Guid.NewGuid()}";
    private WebApplication _app = null!;
    private HubConnection _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        _app = builder.Build();
        _app.MapHub<TestHub>(HubPath);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HubConnectionBuilder().WithUrl(address + HubPath).Build();
        await _client.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task JoinGroup_CountsTheClientUntilItDisconnects()
    {
        TestHub.RegisterGroup(_groupName);

        await _client.InvokeAsync("JoinGroup", _groupName);
        Assert.Equal(1, TestHub.Connections.Count(_groupName));

        await _client.StopAsync();
        await Wait.UntilAsync(() => TestHub.Connections.Count(_groupName) == 0, "The closed connection was still counted");
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_CompletesNormallyForTheClientAndIsReported()
    {
        await _client.InvokeAsync("JoinGroup", _groupName);

        Assert.Contains(TestHub.Connections.GetOngoingIssues(), issue => issue.Key == HubConnections.UnknownGroupIssuePrefix + _groupName);
        Assert.Contains(TestHub.Connections.GetSnapshot().UnknownGroupRequests, request => request.Group == _groupName);
        Assert.Equal(HubConnectionState.Connected, _client.State);
    }

    [Fact]
    public async Task JoinGroup_UnknownGroup_ResolvesTheIssueWhenTheClientDisconnects()
    {
        await _client.InvokeAsync("JoinGroup", _groupName);
        Assert.Contains(TestHub.Connections.GetOngoingIssues(), issue => issue.Key == HubConnections.UnknownGroupIssuePrefix + _groupName);

        await _client.StopAsync();

        await Wait.UntilAsync(
            () => TestHub.Connections.GetOngoingIssues().All(issue => issue.Key != HubConnections.UnknownGroupIssuePrefix + _groupName),
            "The unknown-group issue stayed ongoing after the client disconnected");
    }

    [Theory]
    [InlineData("AddToGroup")]
    [InlineData("ReportUnknownGroup")]
    public async Task GroupHubHelpers_AreNotHubMethods(string method)
    {
        var error = await Assert.ThrowsAsync<HubException>(() => _client.InvokeAsync(method, _groupName));

        Assert.Contains("Method does not exist", error.Message);
    }
}
