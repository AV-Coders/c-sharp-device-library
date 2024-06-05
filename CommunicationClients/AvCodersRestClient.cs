using System.Net.Http.Headers;
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

    public override void Send(string message) => Post(message);
    public override void Send(byte[] bytes) => Post(bytes.ToString() ?? string.Empty);
    public override void AddDefaultHeader(string key, string value) => _headers.Add(key, value);

    public override void RemoveDefaultHeader(string key)
    {
        if (_headers.ContainsKey(key))
            _headers.Remove(key);
    }

    public override async Task Post(string payload)
    {
        using HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
        // Use HttpClient - HttpWebRequest seems to break after three requests.
        using HttpClient httpClient = new HttpClient(handler);
        foreach (var (key, value) in _headers)
        {
            httpClient.DefaultRequestHeaders.Add(key, value);
        }
        try
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Version", "7");
            Log($"Sending request {payload} to {_uri}");
            HttpResponseMessage response = await httpClient.PostAsync(_uri, new StringContent(payload, Encoding.Default, "application/json"));
            Log($"Response status code: {response.StatusCode.ToString()}");
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                Log(responseBody);
                ResponseHandlers?.Invoke(responseBody);
            }
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
    
    public override async Task Put(string content)
    {
        using HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
        // Use HttpClient - HttpWebRequest seems to break after three requests.
        using HttpClient httpClient = new HttpClient(handler);
        foreach (var (key, value) in _headers)
        {
            httpClient.DefaultRequestHeaders.Add(key, value);
        }
        try
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Version", "7");
            Log($"Sending request: {_uri}");
            HttpResponseMessage response = await httpClient.PutAsync(_uri, new StringContent(content, Encoding.Default, "application/json"));
            Log($"Response status code: {response.StatusCode.ToString()}");
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                Log(responseBody);
            }
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