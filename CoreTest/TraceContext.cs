namespace AVCoders.Core.Tests;

public static class TraceContext
{
    public static readonly string[] Headers = ["traceparent", "tracestate", "baggage", "Correlation-Context", "Request-Id"];
}
