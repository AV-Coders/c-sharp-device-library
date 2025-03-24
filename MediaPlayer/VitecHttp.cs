using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
namespace AVCoders.MediaPlayer;

public class VitecHttp : MediaPlayer, ISetTopBox
{
    private readonly Uri _remoteKeyUri;
    private readonly Uri _deviceStateUri;
    private readonly string _authInfo;
    private int _subtitleOption = 0;

    private static readonly List<RemoteButton> UnsupportedButtons = new()
    {
    };

    public VitecHttp(string host, string password)
    {
        _remoteKeyUri = new Uri($"https://{host}:8080/irremote/key", UriKind.Absolute);
        _deviceStateUri = new Uri($"https://{host}:8080/devicestate", UriKind.Absolute);
        _authInfo = Convert.ToBase64String(Encoding.ASCII.GetBytes($"admin:{password}"));
    }

    private bool ValidateCertificate(HttpRequestMessage arg1, X509Certificate2? arg2, X509Chain? arg3,
        SslPolicyErrors arg4) => true;

    private async Task Put(Uri uri, string content)
    {
        using HttpClientHandler handler = new();
        handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        
        // Use HttpClient - HttpWebRequest seems to break after three requests.
        using HttpClient httpClient = new HttpClient(handler);
        try
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Version", "7");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _authInfo);
            Verbose($"Sending request: {uri}");
            await httpClient.PutAsync(uri, new StringContent(content, Encoding.Default, "application/json"));
        }
        catch (Exception e)
        {
            Verbose(e.Message);
            Verbose(e.StackTrace?? "No stack trace available");
            if (e.InnerException == null)
                return;
            Verbose(e.InnerException.Message);
            Verbose(e.InnerException.StackTrace?? "No stack trace available");
        }
    }

    private void SimulateRemoteKeypress(string key) => Put(_remoteKeyUri, $"{{ \"key\": \"rm_{key}\" }}");

    public void ChannelUp() => SendIRCode(RemoteButton.ChannelUp);

    public void ChannelDown() => SendIRCode(RemoteButton.ChannelDown);

    public void VolumeUp() => SendIRCode(RemoteButton.VolumeUp);

    public void VolumeDown() => SendIRCode(RemoteButton.VolumeDown);

    public void SetChannel(int channel)
    {
        if (channel < 10)
        {
            SimulateRemoteKeypress($"{channel}");
        }
        else
        {
            var channelString = channel.ToString();
            foreach (var c in channelString)
            {
                SimulateRemoteKeypress($"{c}");
                Thread.Sleep(50);
            }
        }   
    }

    public void ToggleSubtitles()
    {
        int i = 0;
        SendIRCode(RemoteButton.Subtitle);
        Thread.Sleep(25);
        while (i < _subtitleOption)
        {
            SendIRCode(RemoteButton.Down);
            Thread.Sleep(25);
            i++;
        }
        Thread.Sleep(700);
        SendIRCode(RemoteButton.Enter);
        
        _subtitleOption++;
        if (_subtitleOption > 3)
            _subtitleOption = 0;
    }

    public void SendIRCode(RemoteButton button)
    {
        if (UnsupportedButtons.Contains(button))
        {
            Verbose($"Unsupported button - {button.ToString()}");
            return;
        }
        string command = button switch
        {
            RemoteButton.Enter => "enter",
            RemoteButton.Button1 => "1",
            RemoteButton.Button2 => "2",
            RemoteButton.Button3 => "3",
            RemoteButton.Button4 => "4",
            RemoteButton.Button5 => "5",
            RemoteButton.Button6 => "6",
            RemoteButton.Button7 => "7",
            RemoteButton.Button8 => "8",
            RemoteButton.Button9 => "9",
            RemoteButton.Button0 => "0",
            RemoteButton.Up => "up",
            RemoteButton.Down => "down",
            RemoteButton.Left => "left",
            RemoteButton.Right => "right",
            RemoteButton.Subtitle => "subtitle",
            RemoteButton.Back => "cancel",
            RemoteButton.ChannelUp => "chup",
            RemoteButton.ChannelDown => "chdown",
            RemoteButton.VolumeUp => "volup",
            RemoteButton.VolumeDown => "voldown",
            RemoteButton.Mute => "mute",
            RemoteButton.Power => "power",
            RemoteButton.Play => "play",
            RemoteButton.Pause => "pause",
            RemoteButton.Stop => "stop",
            RemoteButton.Rewind => "skipback",
            RemoteButton.FastForward => "skipfwd",
            RemoteButton.Previous => "skipback",
            RemoteButton.Next => "skipfwd",
            RemoteButton.Red => "red",
            RemoteButton.Green => "green",
            RemoteButton.Yellow => "yellow",
            RemoteButton.Blue => "blue",
            RemoteButton.Guide => "guide",
            RemoteButton.Home  => "fn_home",
            RemoteButton.Menu => "menu",
            
            _ => throw new ArgumentOutOfRangeException()
        };
        
        SimulateRemoteKeypress(command);
    }

    public override void PowerOn()
    {
        Put(_deviceStateUri, "{ \"devicestate\": \"on\" }");
    }

    public override void PowerOff()
    {
        Put(_deviceStateUri, "{ \"devicestate\": \"off\" }");
    }
}