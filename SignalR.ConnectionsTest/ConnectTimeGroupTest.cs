using AVCoders.Core;
using AVCoders.Core.Tests;
using AVCoders.SignalR.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AVCoders.SignalR.Connections.Tests;

public class ConnectTimeGroupTest
{
    private const string HubPath = "/hubs/connecting";

    private static Issue? NoActorIssue() =>
        ConnectingTestHub.Connections.GetOngoingIssues().SingleOrDefault(issue => issue.Key == HubConnections.NoActorIssueKey);

    private static void ResolveAnEarlierIssue()
    {
        using (Actor.Begin(new Actor(ActorKind.Panel, "10.0.0.21", "TSW-1070", null, "earlier-connection")))
            ConnectingTestHub.Connections.ReportInvocation("earlier-connection", "OnConnectedAsync", null);
    }

    private static async Task ConnectAKeypad(bool registersTheFilter)
    {
        var room = $"keypad-room-{Guid.NewGuid()}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (registersTheFilter)
            builder.Services.AddSignalR(options => options.AddFilter<HubAuditFilter>());
        else
            builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.MapHub<ConnectingTestHub>(HubPath);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var keypad = new HubConnectionBuilder().WithUrl($"{address}{HubPath}?{ConnectingTestHub.RoomQuery}={room}").Build();

        await keypad.StartAsync();

        await Wait.UntilAsync(() => ConnectingTestHub.Connections.Count(room) == 1, "the keypad was never added to its room on connect");
    }

    [Fact]
    public async Task AddToGroupOnConnect_OnAHostWithHubAuditFilter_RaisesNoIssue()
    {
        ResolveAnEarlierIssue();

        await ConnectAKeypad(registersTheFilter: true);

        Assert.Null(NoActorIssue());
    }

    [Fact]
    public async Task AddToGroupOnConnect_OnAHostWithoutHubAuditFilter_RaisesTheNoActorIssue()
    {
        ResolveAnEarlierIssue();

        await ConnectAKeypad(registersTheFilter: false);

        Assert.StartsWith("OnConnectedAsync on ConnectingTestHub arrived with no actor", NoActorIssue()?.Message);
    }
}
