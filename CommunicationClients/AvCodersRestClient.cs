using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AVCoders.CommunicationClients;

public class AvCodersRestClient : RestComms
{
    private readonly Dictionary<string, string> _headers;
    private readonly Uri _uri;
    
    public AvCodersRestClient(string host, ushort port, string protocol, string name = "") : base(host, port, name)
    {
        _headers = new Dictionary<string, string>();
        _uri = new Uri($"{protocol}://{host}:{port}", UriKind.Absolute);
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

    public override async Task Post(string payload, string contentType) => await Post(null, payload, contentType);
    
    public override async Task Post(Uri? endpoint, string payload, string contentType)
    {
        try
        {
            using HttpClientHandler handler = new();
            handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
            // Use HttpClient - HttpWebRequest seems to break after three requests.
            HttpClient httpClient = new HttpClient(handler);
            foreach (var (key, value) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }
            
            Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
            HttpResponseMessage response = await httpClient.PostAsync(uri, new StringContent(payload, Encoding.Default, contentType));
            await HandleResponse(response);
            ConnectionState = ConnectionState.Connected;
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
            using HttpClientHandler handler = new();
            handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
            // Use HttpClient - HttpWebRequest seems to break after three requests.
            HttpClient httpClient = new HttpClient(handler);
            foreach (var (key, value) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }
            Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
            HttpResponseMessage response = await httpClient.PutAsync(uri, new StringContent(content, Encoding.Default, contentType));
            await HandleResponse(response);
            ConnectionState = ConnectionState.Connected;
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
            using HttpClientHandler handler = new();
            handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
            
            // Use HttpClient - HttpWebRequest seems to break after three requests.
            HttpClient httpClient = new HttpClient(handler);
            foreach (var (key, value) in _headers)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
            }
            Uri uri = endpoint == null ? _uri : new Uri(_uri, endpoint);
            HttpResponseMessage response = await httpClient.GetAsync(uri);
            await HandleResponse(response);
            ConnectionState = ConnectionState.Connected;
        }
        catch (Exception e)
        {
            ConnectionState = ConnectionState.Error;
            LogException(e);
        }

    }
    
    private bool ValidateCertificate(HttpRequestMessage arg1, X509Certificate2? arg2, X509Chain? arg3, SslPolicyErrors arg4) => true;
}