using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersRestClientTest : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ushort _port;

    public AvCodersRestClientTest()
    {
        // HttpListener cannot bind port 0, so probe for a free one.
        for (var attempt = 0; attempt < 10; attempt++)
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
                // port was taken between probe and bind - try another
            }
        }

        throw new InvalidOperationException("Could not find a free port for HttpListener");
    }

    private AvCodersRestClient CreateClient(TimeSpan? timeout = null) =>
        new("127.0.0.1", _port, "http", "TestRestClient", timeout);

    public void Dispose()
    {
        try { _listener.Close(); } catch { /* test teardown */ }
    }

    private sealed record CapturedRequest(string Method, string Body, NameValueCollectionSnapshot Headers);

    public sealed class NameValueCollectionSnapshot(Dictionary<string, string> values)
    {
        public string? Get(string key) => values.GetValueOrDefault(key);
    }

    /// <summary>Serves exactly one request, capturing it and replying with the given status/body.</summary>
    private async Task<CapturedRequest> ServeOneAsync(int statusCode = 200, string responseBody = "",
        TimeSpan? delayBeforeResponding = null)
    {
        var context = await _listener.GetContextAsync();
        // Capture everything before Response.Close() - on Windows (http.sys) closing the
        // response disposes the request, and reading it afterwards throws.
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

        if (delayBeforeResponding != null)
            await Task.Delay(delayBeforeResponding.Value);

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
        // Regression test: Send(byte[]) used to post the literal string "System.Byte[]".
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

        await TestNetwork.WaitUntilAsync(() => response != null, 10, "response handler never invoked");
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

        await TestNetwork.WaitUntilAsync(() => httpResponse != null, 10, "HTTP response handler never invoked");
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
    public async Task RequestTimeout_SetsErrorState()
    {
        var serve = ServeOneAsync(delayBeforeResponding: TimeSpan.FromSeconds(5));
        var client = CreateClient(timeout: TimeSpan.FromMilliseconds(500));

        await client.Get();

        Assert.Equal(ConnectionState.Error, client.ConnectionState);
        await serve; // let the listener finish so teardown is clean
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string commonName = "RestClientTest")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static bool Validate(AvCodersRestClient client, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        var method = typeof(AvCodersRestClient).GetMethod("ValidateCertificate",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var request = new HttpRequestMessage();
        return (bool)method.Invoke(client, [request, certificate, null, errors])!;
    }

    [Fact]
    public void CertificateValidation_AcceptsUntrustedCertificates_ByDefault()
    {
        using var certificate = CreateSelfSignedCertificate();
        var client = CreateClient();

        Assert.True(Validate(client, certificate, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void CertificateValidation_RejectsUntrustedCertificates_InStrictMode()
    {
        using var certificate = CreateSelfSignedCertificate();
        var client = new AvCodersRestClient("127.0.0.1", _port, "https", "StrictClient",
            allowUntrustedCertificates: false);

        Assert.False(Validate(client, certificate, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(Validate(client, certificate, SslPolicyErrors.None));
    }

    [Fact]
    public void CertificateValidation_WithAPinnedThumbprint_AcceptsOnlyTheMatchingCertificate()
    {
        using var pinned = CreateSelfSignedCertificate("PinnedDevice");
        using var imposter = CreateSelfSignedCertificate("Imposter");
        // Deliberately messy formatting - the thumbprint should be normalised.
        var thumbprint = string.Join(":", pinned.Thumbprint.ToLowerInvariant().Chunk(2).Select(c => new string(c)));
        var client = new AvCodersRestClient("127.0.0.1", _port, "https", "PinnedClient",
            pinnedCertificateThumbprint: thumbprint);

        Assert.True(Validate(client, pinned, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(Validate(client, imposter, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}
