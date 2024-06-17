using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.CommunicationClients;

public class AvCodersRestClient : RestComms
{
    private readonly Dictionary<string, string> _headers;
    private readonly Uri _uri;
    
    public AvCodersRestClient(string host, ushort port, string protocol) : base(host, port)
    {
        _headers = new Dictionary<string, string>();
        _uri = new Uri($"{protocol}://{host}:{port}", UriKind.Absolute);
    }

    public override void Send(string message) => _ = Post(message, "application/json");
    public override void Send(byte[] bytes) => _ = Post(bytes.ToString() ?? string.Empty, "application/json");
    public override void AddDefaultHeader(string key, string value) => _headers.Add(key, value);

    public override void RemoveDefaultHeader(string key)
    {
        if (_headers.ContainsKey(key))
            _headers.Remove(key);
    }

    private HttpClient CreateHttpClient()
    {
        using HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
        // Use HttpClient - HttpWebRequest seems to break after three requests.
        HttpClient httpClient = new HttpClient(handler);
        foreach (var (key, value) in _headers)
        {
            httpClient.DefaultRequestHeaders.Add(key, value);
        }

        return httpClient;
    }

    private async Task HandleResponse(HttpResponseMessage response)
    {
        HttpResponseHandlers?.Invoke(response);
        Log($"Response status code: {response.StatusCode.ToString()}");
        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            Log(responseBody);
            ResponseHandlers?.Invoke(responseBody);
        }
    }

    public override async Task Post(string payload, string contentType)
    {
        try
        {
            using HttpClient httpClient = CreateHttpClient();
            Log($"Actioning Post of {payload} to {_uri}");
            HttpResponseMessage response = await httpClient.PostAsync(_uri, new StringContent(payload, Encoding.Default, contentType));
            await HandleResponse(response);
        }
        catch (Exception e)
        {
            Log(e.Message);
            Log(e.StackTrace);
            if (e.InnerException == null)
                return;
            Log(e.InnerException.Message);
            Log(e.InnerException.StackTrace);
        }
    }
    
    public async Task Post(Uri endpoint, string payload, string contentType)
    {
        try
        {
            using HttpClient httpClient = CreateHttpClient();
            Uri uri = new Uri(_uri, endpoint);
            Log($"Actioning Post of {payload} to {uri}");
            HttpResponseMessage response = await httpClient.PostAsync(uri, new StringContent(payload, Encoding.Default, contentType));
            await HandleResponse(response);
        }
        catch (Exception e)
        {
            Log(e.Message);
            Log(e.StackTrace);
            if (e.InnerException == null)
                return;
            Log(e.InnerException.Message);
            Log(e.InnerException.StackTrace);
        }
    }
    
    public override async Task Put(string content, string contentType)
    {
        try
        {
            using HttpClient httpClient = CreateHttpClient();
            Log($"Actioning Put to {_uri}");
            HttpResponseMessage response = await httpClient.PutAsync(_uri, new StringContent(content, Encoding.Default, contentType));
            await HandleResponse(response);
        }
        catch (Exception e)
        {
            Log(e.Message);
            Log(e.StackTrace);
            if (e.InnerException == null)
                return;
            Log(e.InnerException.Message);
            Log(e.InnerException.StackTrace);
        }
    }
    
    public async Task Put(Uri endpoint, string content, string contentType)
    {
        try
        {
            using HttpClient httpClient = CreateHttpClient();
            Uri uri = new Uri(_uri, endpoint);
            Log($"Actioning Put to {uri}");
            HttpResponseMessage response = await httpClient.PutAsync(uri, new StringContent(content, Encoding.Default, contentType));
            await HandleResponse(response);
        }
        catch (Exception e)
        {
            Log(e.Message);
            Log(e.StackTrace);
            if (e.InnerException == null)
                return;
            Log(e.InnerException.Message);
            Log(e.InnerException.StackTrace);
        }
    }
    
    private bool ValidateCertificate(HttpRequestMessage arg1, X509Certificate2? arg2, X509Chain? arg3, SslPolicyErrors arg4) => true;
}