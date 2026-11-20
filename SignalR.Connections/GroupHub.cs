using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Connections;

public abstract class GroupHub<T>(HubConnections connections) : Hub<T> where T : class
{
    public override async Task OnConnectedAsync()
    {
        connections.Open(Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Close(Context.ConnectionId, exception);
        await base.OnDisconnectedAsync(exception);
    }

    protected async Task AddToGroup(string groupName, [CallerMemberName] string methodName = "")
    {
        ReportInvocation(methodName);
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        connections.Join(Context.ConnectionId, groupName);
    }

    protected void ReportUnknownGroup(string groupName, [CallerMemberName] string methodName = "")
    {
        ReportInvocation(methodName);
        connections.ReportUnknownGroup(Context.ConnectionId, methodName, groupName);
    }

    protected void ReportInvocation([CallerMemberName] string methodName = "") =>
        connections.ReportInvocation(Context.ConnectionId, methodName, Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString());
}
