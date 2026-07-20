using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.CommunicationClients;

public class AvCodersRestClient : RestComms
{
    private readonly Dictionary<string, string> _headers;
    private readonly object _headersLock = new();
    private readonly Uri _uri;
    private readonly HttpClient _httpClient;
    private readonly bool _allowUntrustedCertificates;
    private readonly string? _pinnedCertificateThumbprint;
    private readonly HashSet<string> _acceptedUntrustedThumbprints = new();

    /// <summary>
    /// Request and response payloads are excluded from logs by default because login
    /// bodies carry credentials. Enable only for bench debugging.
    /// </summary>
    public bool LogPayloads { get; set; }

    /// <param name="allowUntrustedCertificates">AV devices overwhelmingly use self-signed
    /// certificates, so untrusted certificates are accepted by default — each accepted
    /// thumbprint is logged once. Pass false to require a valid chain.</param>
    /// <param name="pinnedCertificateThumbprint">When set, an untrusted certificate is only
    /// accepted if its thumbprint matches — the hardened option for self-signed devices.</param>
    public AvCodersRestClient(string host, ushort port, string protocol, string name = "",
        TimeSpan? requestTimeout = null, bool allowUntrustedCertificates = true,
        string? pinnedCertificateThumbprint = null) : base(host, port, name)
    {
        _headers = new Dictionary<string, string>();
        _uri = new Uri($"{protocol}://{host}:{port}", UriKind.Absolute);
        _allowUntrustedCertificates = allowUntrustedCertificates;
        _pinnedCertificateThumbprint = pinnedCertificateThumbprint == null
            ? null
            : NormaliseThumbprint(pinnedCertificateThumbprint);
        var handler = new HttpClientHandler();
        handler.UseCookies = true;
        handler.CookieContainer = new System.Net.CookieContainer();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
    }

    private static string NormaliseThumbprint(string thumbprint) =>
        new(thumbprint.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    public override void Send(string message) => _ = Post(message, "application/json");
    public override void Send(byte[] bytes) => _ = Post(Encoding.UTF8.GetString(bytes), "application/json");

    public override void AddDefaultHeader(string key, string value)
    {
        lock (_headersLock)
            _headers[key] = value;
    }

    public override void RemoveDefaultHeader(string key)
    {
        lock (_headersLock)
            _headers.Remove(key);
    }

    private async Task HandleResponse(HttpResponseMessage response)
    {
        HttpResponseHandlers?.Invoke(response);
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            InvokeResponseHandlers(responseBody);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        // Snapshot under the lock - this enumerates on async continuations while
        // AddDefaultHeader/RemoveDefaultHeader can run on other threads.
        KeyValuePair<string, string>[] headers;
        lock (_headersLock)
            headers = _headers.ToArray();
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
        return request;
    }

    public override async Task Post(string payload, string contentType) => await Post(null, payload, contentType);

    public override async Task Post(Uri? endpoint, string payload, string contentType)
    {
        using (PushProperties("Post"))
        {
            try
            {
                Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
                using var request = CreateRequest(HttpMethod.Post, uri);
                request.Content = new StringContent(payload, Encoding.Default, contentType);
                if (LogPayloads)
                    LogVerbose("POST {Uri} - {Payload}", uri, payload);
                else
                    LogVerbose("POST {Uri} ({PayloadLength} chars)", uri, payload.Length);
                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                RequestHandlers?.Invoke(payload);
                await HandleResponse(response);
                ConnectionState = ConnectionState.Connected;
            }
            catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
            {
                ConnectionState = ConnectionState.Error;
                LogException(e, "POST request timed out");
            }
            catch (Exception e)
            {
                ConnectionState = ConnectionState.Error;
                LogException(e);
            }
        }
    }

    public override async Task Put(string content, string contentType) => await Put(null, content, contentType);

    public override async Task Put(Uri? endpoint, string content, string contentType)
    {
        using (PushProperties("Put"))
        {
            try
            {
                Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
                using var request = CreateRequest(HttpMethod.Put, uri);
                request.Content = new StringContent(content, Encoding.Default, contentType);
                if (LogPayloads)
                    LogVerbose("PUT {Uri} - {Payload}", uri, content);
                else
                    LogVerbose("PUT {Uri} ({PayloadLength} chars)", uri, content.Length);
                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                RequestHandlers?.Invoke(content);
                await HandleResponse(response);
                ConnectionState = ConnectionState.Connected;
            }
            catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
            {
                ConnectionState = ConnectionState.Error;
                LogException(e, "PUT request timed out");
            }
            catch (Exception e)
            {
                ConnectionState = ConnectionState.Error;
                LogException(e);
            }
        }
    }

    public override async Task Get() => await Get(null);

    public override async Task Get(Uri? endpoint)
    {
        using (PushProperties("Get"))
        {
            try
            {
                Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
                using var request = CreateRequest(HttpMethod.Get, uri);
                LogVerbose("GET {Uri}", uri);
                using HttpResponseMessage response = await _httpClient.SendAsync(request);
                RequestHandlers?.Invoke($"HTTP Get to {uri.AbsolutePath}");
                await HandleResponse(response);
                ConnectionState = ConnectionState.Connected;
            }
            catch (TaskCanceledException e) when (e.InnerException is TimeoutException)
            {
                ConnectionState = ConnectionState.Error;
                LogException(e, "GET request timed out");
            }
            catch (Exception e)
            {
                ConnectionState = ConnectionState.Error;
                LogException(e);
            }
        }
    }

    private bool ValidateCertificate(HttpRequestMessage request, X509Certificate2? certificate,
        X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        var thumbprint = certificate == null ? string.Empty : NormaliseThumbprint(certificate.Thumbprint);

        if (_pinnedCertificateThumbprint != null)
        {
            if (thumbprint == _pinnedCertificateThumbprint)
                return true;
            using (PushProperties("ValidateCertificate"))
                LogError("Rejected certificate {Thumbprint} - it does not match the pinned thumbprint", thumbprint);
            AddEvent(EventType.Error, $"Rejected certificate {thumbprint}, it does not match the pinned thumbprint");
            return false;
        }

        if (!_allowUntrustedCertificates)
        {
            using (PushProperties("ValidateCertificate"))
                LogError("Rejected untrusted certificate {Thumbprint}: {SslPolicyErrors}", thumbprint, sslPolicyErrors);
            AddEvent(EventType.Error, $"Rejected untrusted certificate {thumbprint}: {sslPolicyErrors}");
            return false;
        }

        // Accepted, but visibly: log each distinct thumbprint once so the acceptance is
        // auditable and the thumbprint is available for pinning.
        lock (_acceptedUntrustedThumbprints)
        {
            if (_acceptedUntrustedThumbprints.Add(thumbprint))
            {
                using (PushProperties("ValidateCertificate"))
                    LogWarning(
                        "Accepting untrusted certificate {Thumbprint} ({SslPolicyErrors}) - pass pinnedCertificateThumbprint to harden this device",
                        thumbprint, sslPolicyErrors);
                AddEvent(EventType.Connection, $"Accepted untrusted certificate {thumbprint}");
            }
        }
        return true;
    }
}