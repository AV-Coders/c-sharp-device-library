using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.CommunicationClients;

public class AvCodersRestClient : RestComms
{
    private readonly Dictionary<string, string> _headers;
    private readonly Uri _uri;
    private readonly HttpClient _httpClient;

    public AvCodersRestClient(string host, ushort port, string protocol, string name = "", TimeSpan? requestTimeout = null) : base(host, port, name)
    {
        _headers = new Dictionary<string, string>();
        _uri = new Uri($"{protocol}://{host}:{port}", UriKind.Absolute);
        var handler = new HttpClientHandler();
        handler.UseCookies = true;
        handler.CookieContainer = new System.Net.CookieContainer();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
    }

    public override void Send(string message) => _ = Post(message, "application/json");
    public override void Send(byte[] bytes) => _ = Post(bytes.ToString() ?? string.Empty, "application/json");

    public override void AddDefaultHeader(string key, string value) => _headers[key] = value;

    public override void RemoveDefaultHeader(string key)
    {
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
        foreach (var (key, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
        return request;
    }

    public override async Task Post(string payload, string contentType) => await Post(null, payload, contentType);

    public override async Task Post(Uri? endpoint, string payload, string contentType)
    {
        try
        {
            Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
            using var request = CreateRequest(HttpMethod.Post, uri);
            request.Content = new StringContent(payload, Encoding.Default, contentType);
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

    public override async Task Put(string content, string contentType) => await Put(null, content, contentType);

    public override async Task Put(Uri? endpoint, string content, string contentType)
    {
        try
        {
            Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
            using var request = CreateRequest(HttpMethod.Put, uri);
            request.Content = new StringContent(content, Encoding.Default, contentType);
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

    public override async Task Get() => await Get(null);

    public override async Task Get(Uri? endpoint)
    {
        try
        {
            Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
            using var request = CreateRequest(HttpMethod.Get, uri);
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

    private bool ValidateCertificate(HttpRequestMessage arg1, X509Certificate2? arg2, X509Chain? arg3, SslPolicyErrors arg4) => true;
}