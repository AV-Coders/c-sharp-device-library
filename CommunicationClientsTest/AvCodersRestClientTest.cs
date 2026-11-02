using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersRestClientTest : IDisposable
{
    private const int PortAttempts = 10;
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly X509Certificate2 ServerCertificate = CreateServerCertificate();
    private static readonly X509Certificate2 OtherCertificate = CreateSelfSignedCertificate("Other");
    private readonly HttpListener _listener;
    private readonly ushort _port;

    public AvCodersRestClientTest()
    {
        for (var attempt = 0; attempt < PortAttempts; attempt++)
        {
            var port = TestNetwork.GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                _port = port;
                return;
            }
            catch (HttpListenerException)
            {
            }
        }

        throw new InvalidOperationException("Could not find a free port for HttpListener");
    }

    private AvCodersRestClient CreateClient(TimeSpan? timeout = null) =>
        new("127.0.0.1", _port, "http", "TestRestClient", timeout);

    public void Dispose()
    {
        try { _listener.Close(); } catch { }
    }

    private sealed record CapturedRequest(string Method, string Body, NameValueCollectionSnapshot Headers);

    public sealed class NameValueCollectionSnapshot(Dictionary<string, string> values)
    {
        public string? Get(string key) => values.GetValueOrDefault(key);
    }

    private async Task<CapturedRequest> ServeOneAsync(int statusCode = 200, string responseBody = "",
        Task? respondAfter = null)
    {
        var context = await _listener.GetContextAsync();
        string method = context.Request.HttpMethod;
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
            body = await reader.ReadToEndAsync();

        var headers = new Dictionary<string, string>();
        foreach (var key in context.Request.Headers.AllKeys)
        {
            if (key != null && context.Request.Headers[key] != null)
                headers[key] = context.Request.Headers[key]!;
        }

        if (respondAfter != null)
            await respondAfter;

        context.Response.StatusCode = statusCode;
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();

        return new CapturedRequest(method, body, new NameValueCollectionSnapshot(headers));
    }

    [Fact]
    public async Task SendBytes_PostsTheUtf8DecodedPayload()
    {
        var serve = ServeOneAsync();
        var client = CreateClient();

        client.Send(Encoding.UTF8.GetBytes("{\"power\":\"on\"}"));

        var request = await serve;
        Assert.Equal("POST", request.Method);
        Assert.Equal("{\"power\":\"on\"}", request.Body);
        Assert.DoesNotContain("System.Byte[]", request.Body);
    }

    [Fact]
    public async Task SendString_PostsThePayload()
    {
        var serve = ServeOneAsync();
        var client = CreateClient();

        client.Send("{\"input\":2}");

        var request = await serve;
        Assert.Equal("POST", request.Method);
        Assert.Equal("{\"input\":2}", request.Body);
    }

    [Fact]
    public async Task Get_InvokesResponseHandlers_AndReportsConnected()
    {
        var serve = ServeOneAsync(responseBody: "device status ok");
        string? response = null;
        HttpResponseMessage? httpResponse = null;
        var client = CreateClient();
        client.ResponseHandlers += message => response = message;
        client.HttpResponseHandlers += message => httpResponse = message;

        await client.Get();
        await serve;

        await Wait.UntilAsync(() => response != null, "response handler never invoked");
        Assert.Equal("device status ok", response);
        Assert.NotNull(httpResponse);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task FailedRequest_InvokesHttpHandlerButNotResponseHandlers()
    {
        var serve = ServeOneAsync(statusCode: 500);
        string? response = null;
        HttpResponseMessage? httpResponse = null;
        var client = CreateClient();
        client.ResponseHandlers += message => response = message;
        client.HttpResponseHandlers += message => httpResponse = message;

        await client.Get();
        await serve;

        await Wait.UntilAsync(() => httpResponse != null, "HTTP response handler never invoked");
        Assert.Equal(HttpStatusCode.InternalServerError, httpResponse!.StatusCode);
        Assert.Null(response);
    }

    [Fact]
    public async Task DefaultHeaders_AreSent_AndRemovable()
    {
        var client = CreateClient();
        client.AddDefaultHeader("X-Test-Token", "secret-1");

        var serve = ServeOneAsync();
        await client.Get();
        var withHeader = await serve;
        Assert.Equal("secret-1", withHeader.Headers.Get("X-Test-Token"));

        client.RemoveDefaultHeader("X-Test-Token");
        serve = ServeOneAsync();
        await client.Get();
        var withoutHeader = await serve;
        Assert.Null(withoutHeader.Headers.Get("X-Test-Token"));
    }

    [Fact]
    public async Task Get_UnderAnActivity_SendsNoTraceContextHeaders()
    {
        var serve = ServeOneAsync();
        var client = CreateClient();
        using var activity = new Activity("test").Start();
        activity.TraceStateString = "vendor=value";
        activity.AddBaggage("key", "value");

        await client.Get();
        var request = await serve;

        Assert.All(TraceContext.Headers, header => Assert.Null(request.Headers.Get(header)));
    }

    [Fact]
    public async Task RequestTimeout_SetsErrorState()
    {
        var gaveUp = new TaskCompletionSource();
        var serve = ServeOneAsync(respondAfter: gaveUp.Task);
        var client = CreateClient(timeout: ClientTimeout);

        await client.Get();
        gaveUp.SetResult();

        Assert.Equal(ConnectionState.Error, client.ConnectionState);
        await serve;
    }

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    public async Task RejectedCredentials_AreCredentialsRejected_AndRaiseTheConnectionIssue(int status, string reasonPhrase)
    {
        var client = CreateClient();

        var serve = ServeOneAsync(statusCode: status);
        await client.Get(new Uri("/api/login", UriKind.Relative));
        await serve;

        var reason = client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.CredentialsRejected, reason.Category);
        Assert.Equal($"127.0.0.1:{_port} rejected the credentials for /api/login: {status} {reasonPhrase}", reason.Description);
        Assert.Null(reason.RawException);
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        var issue = Assert.Single(client.GetIssues());
        Assert.Equal(CommunicationClient.ConnectionIssueKey, issue.Key);
        Assert.Equal(reason.Description, issue.Message);
    }

    [Fact]
    public async Task RejectedCredentials_EscalateLikeAnyOtherConnectionFailure()
    {
        var client = CreateClient();
        client.ConnectionIssueThreshold = TimeSpan.Zero;

        var serve = ServeOneAsync(statusCode: 401);
        await client.Get();
        await serve;

        var ongoing = Assert.Single(client.GetOngoingIssues());
        Assert.Equal(IssueSeverity.Critical, ongoing.Severity);
        Assert.EndsWith($"127.0.0.1:{_port} rejected the credentials for /: 401 Unauthorized", ongoing.Message);
    }

    [Fact]
    public async Task ASuccessfulRequestAfterRejectedCredentials_ClearsTheReasonAndResolvesTheIssue()
    {
        var client = CreateClient();
        client.ConnectionIssueThreshold = TimeSpan.Zero;
        var serve = ServeOneAsync(statusCode: 401);
        await client.Get();
        await serve;

        serve = ServeOneAsync();
        await client.Get();
        await serve;

        Assert.Null(client.ConnectionReason);
        Assert.Empty(client.GetOngoingIssues());
        Assert.Contains(client.GetIssues(), issue => issue.Key == CommunicationClient.ConnectionIssueKey && issue.Status == IssueStatus.Resolved);
    }

    [Fact]
    public async Task AServerErrorAfterRejectedCredentials_KeepsTheReason()
    {
        var client = CreateClient();
        var serve = ServeOneAsync(statusCode: 401);
        await client.Get();
        await serve;

        serve = ServeOneAsync(statusCode: 500);
        await client.Get();
        await serve;

        Assert.Equal(ConnectionReasonCategory.CredentialsRejected, client.ConnectionReason!.Category);
    }

    [Fact]
    public async Task AFailedRequest_SetsTheReasonFromItsException()
    {
        var client = await GetOverTls(ServerCertificate, false, null);

        var reason = client.ConnectionReason!;
        Assert.Equal(ConnectionReasonCategory.Other, reason.Category);
        Assert.StartsWith($"The request to 127.0.0.1:{client.Port} failed: ", reason.Description);
        Assert.StartsWith("System.Net.Http.HttpRequestException: ", reason.RawException);
        Assert.Contains("System.Security.Authentication.AuthenticationException: ", reason.RawException);
    }

    [Fact]
    public async Task ARequestTimeout_IsTimedOut()
    {
        var gaveUp = new TaskCompletionSource();
        var serve = ServeOneAsync(respondAfter: gaveUp.Task);
        var client = CreateClient(timeout: ClientTimeout);

        await client.Get();
        gaveUp.SetResult();

        Assert.Equal(ConnectionReasonCategory.TimedOut, client.ConnectionReason!.Category);
        Assert.Equal($"The GET request to 127.0.0.1:{_port} timed out", client.ConnectionReason.Description);
        await serve;
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string commonName = "RestClientTest")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var ephemeral = CreateSelfSignedCertificate("127.0.0.1");
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null);
#else
        return new X509Certificate2(pfx);
#endif
    }

    private static async Task ServeOneTlsAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var connection = await TestNetwork.AcceptAsync(listener, 10);
        await using var tls = new SslStream(connection.GetStream());
        await tls.AuthenticateAsServerAsync(certificate);
        var head = new StringBuilder();
        var buffer = new byte[1024];
        while (!head.ToString().Contains("\r\n\r\n"))
        {
            int read = await tls.ReadAsync(buffer);
            if (read == 0)
                break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
    }

    private static async Task<AvCodersRestClient> GetOverTls(X509Certificate2 certificate,
        bool allowUntrustedCertificates, string? pinnedThumbprint)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = ServeOneTlsAsync(listener, certificate);
            var client = new AvCodersRestClient("127.0.0.1", port, "https", "TlsClient",
                allowUntrustedCertificates: allowUntrustedCertificates, pinnedCertificateThumbprint: pinnedThumbprint);
            await client.Get();
            await Record.ExceptionAsync(() => serve);
            return client;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Get_OverHttpsToASelfSignedDevice_SucceedsByDefault()
    {
        var client = await GetOverTls(ServerCertificate, true, null);

        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task Get_OverHttpsToASelfSignedDevice_FailsInStrictMode()
    {
        var client = await GetOverTls(ServerCertificate, false, null);

        Assert.Equal(ConnectionState.Error, client.ConnectionState);
    }

    [Fact]
    public async Task Get_OverHttpsToThePinnedCertificate_Succeeds()
    {
        var client = await GetOverTls(ServerCertificate, true, ServerCertificate.Thumbprint);

        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    [Fact]
    public async Task Get_OverHttpsToADifferentCertificateThanThePinnedOne_Fails()
    {
        var client = await GetOverTls(ServerCertificate, true, OtherCertificate.Thumbprint);

        Assert.Equal(ConnectionState.Error, client.ConnectionState);
    }

    private static bool Validate(AvCodersRestClient client, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        var method = typeof(AvCodersRestClient).GetMethod("ValidateCertificate",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var request = new HttpRequestMessage();
        return (bool)method.Invoke(client, [request, certificate, null, errors])!;
    }

    [Fact]
    public void CertificateValidation_InStrictMode_AcceptsAValidChain()
    {
        var client = new AvCodersRestClient("127.0.0.1", _port, "https", "StrictClient",
            allowUntrustedCertificates: false);

        Assert.True(Validate(client, ServerCertificate, SslPolicyErrors.None));
    }

    [Fact]
    public void CertificateValidation_WithAPinnedThumbprint_AcceptsOnlyTheMatchingCertificate()
    {
        var pinned = ServerCertificate;
        var imposter = OtherCertificate;
        var thumbprint = string.Join(":", pinned.Thumbprint.ToLowerInvariant().Chunk(2).Select(c => new string(c)));
        var client = new AvCodersRestClient("127.0.0.1", _port, "https", "PinnedClient",
            pinnedCertificateThumbprint: thumbprint);

        Assert.True(Validate(client, pinned, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(Validate(client, imposter, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
