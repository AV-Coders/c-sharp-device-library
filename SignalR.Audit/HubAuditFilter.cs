using System.Collections;
using System.Globalization;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Audit;

public class HubAuditFilter : IHubFilter
{
    private const string HubSuffix = "Hub";
    private const string HiddenValue = "[redacted]";
    private const string JoinGroupMethod = "JoinGroup";
    private const string QueryPrefix = "Get";
    private static readonly HashSet<string> SecretParameters = new(StringComparer.OrdinalIgnoreCase) { "pin" };

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        Actor.Current = CreateActor(invocationContext.Context);
        Trigger.Start();
        var methodName = invocationContext.HubMethodName;
        if (methodName != JoinGroupMethod && !methodName.StartsWith(QueryPrefix, StringComparison.Ordinal))
            RecordInvocation(invocationContext);
        return await next(invocationContext);
    }

    private static Actor CreateActor(HubCallerContext context)
    {
        var httpContext = context.GetHttpContext();
        var address = httpContext?.Connection.RemoteIpAddress;
        if (address is { IsIPv4MappedToIPv6: true })
            address = address.MapToIPv4();
        return new Actor(
            ActorKind.Panel,
            address?.ToString() ?? string.Empty,
            httpContext?.Request.Headers.UserAgent.ToString() ?? string.Empty,
            address == null ? null : HostNames.Get(address),
            context.ConnectionId);
    }

    private static void RecordInvocation(HubInvocationContext invocationContext)
    {
        var hubName = invocationContext.Hub.GetType().Name;
        if (hubName.EndsWith(HubSuffix))
            hubName = hubName[..^HubSuffix.Length];
        var arguments = invocationContext.HubMethodArguments;
        var parameters = invocationContext.HubMethod.GetParameters();
        var hasGroup = arguments.Count > 0 && arguments[0] is string && !SecretParameters.Contains(parameters[0].Name!);
        var target = hasGroup ? (string)arguments[0]! : hubName;
        var firstDetail = hasGroup ? 1 : 0;
        var detail = string.Join(" ", Enumerable.Range(firstDetail, arguments.Count - firstDetail)
            .Select(i => $"{parameters[i].Name}={Describe(parameters[i].Name!, arguments[i])}"));
        AVCoders.Core.Audit.Record(target, $"{hubName}.{invocationContext.HubMethodName}", detail);
    }

    private static string Describe(string parameterName, object? value) => value switch
    {
        _ when SecretParameters.Contains(parameterName) => HiddenValue,
        null => "null",
        IDictionary dictionary => $"[{string.Join(", ", dictionary.Keys.Cast<object>())}]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
