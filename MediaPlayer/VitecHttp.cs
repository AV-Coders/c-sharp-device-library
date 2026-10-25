using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AVCoders.Core;

namespace AVCoders.MediaPlayer;

public class VitecHttp : MediaPlayer, ISetTopBox
{
    private const int FirstTwoDigitChannel = 10;
    private const int LastSubtitleOption = 3;
    private static readonly TimeSpan DigitSpacing = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SubtitleKeySpacing = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan SubtitleMenuTime = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    private readonly Uri _remoteKeyUri;
    private readonly Uri _deviceStateUri;
    private readonly string _authInfo;
    private readonly object _sendLock = new();
    private Task _sends = Task.CompletedTask;
    private int _sendGeneration;
    private int _queuedPresses;
    private int _subtitleOption = 0;

    private readonly record struct Press(Uri Uri, string Content, TimeSpan SpacingAfter);

    private static readonly Dictionary<RemoteButton, string> RemoteButtonMap = new()
    {
        { RemoteButton.Enter, "enter" },
        { RemoteButton.Button1, "1" },
        { RemoteButton.Button2, "2" },
        { RemoteButton.Button3, "3" },
        { RemoteButton.Button4, "4" },
        { RemoteButton.Button5, "5" },
        { RemoteButton.Button6, "6" },
        { RemoteButton.Button7, "7" },
        { RemoteButton.Button8, "8" },
        { RemoteButton.Button9, "9" },
        { RemoteButton.Button0, "0" },
        { RemoteButton.Up, "up" },
        { RemoteButton.Down, "down" },
        { RemoteButton.Left, "left" },
        { RemoteButton.Right, "right" },
        { RemoteButton.Subtitle, "subtitle" },
        { RemoteButton.Back, "cancel" },
        { RemoteButton.ChannelUp, "chup" },
        { RemoteButton.ChannelDown, "chdown" },
        { RemoteButton.VolumeUp, "volup" },
        { RemoteButton.VolumeDown, "voldown" },
        { RemoteButton.Mute, "mute" },
        { RemoteButton.Power, "power" },
        { RemoteButton.Play, "play" },
        { RemoteButton.Pause, "pause" },
        { RemoteButton.Stop, "stop" },
        { RemoteButton.Rewind, "skipback" },
        { RemoteButton.FastForward, "skipfwd" },
        { RemoteButton.Previous, "skipback" },
        { RemoteButton.Next, "skipfwd" },
        { RemoteButton.Red, "red" },
        { RemoteButton.Green, "green" },
        { RemoteButton.Yellow, "yellow" },
        { RemoteButton.Blue, "blue" },
        { RemoteButton.Guide, "guide" },
        { RemoteButton.Home, "fn_home" },
        { RemoteButton.Menu, "menu" }
    };

    public VitecHttp(string host, string password, string name) 
        : base(name, CommunicationClient.None)
    {
        _remoteKeyUri = new Uri($"https://{host}:8080/irremote/key", UriKind.Absolute);
        _deviceStateUri = new Uri($"https://{host}:8080/devicestate", UriKind.Absolute);
        _authInfo = Convert.ToBase64String(Encoding.ASCII.GetBytes($"admin:{password}"));
    }

    public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;

    private bool ValidateCertificate(object arg1, X509Certificate? arg2, X509Chain? arg3,
        SslPolicyErrors arg4) => true;

    private async Task<bool> Put(Uri uri, string content)
    {
        using SocketsHttpHandler handler = new();
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
        handler.ActivityHeadersPropagator = null;

        // Use HttpClient - HttpWebRequest seems to break after three requests.
        using HttpClient httpClient = new HttpClient(handler);
        try
        {
            httpClient.Timeout = RequestTimeout;
            httpClient.DefaultRequestHeaders.Add("X-API-Version", "7");
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _authInfo);
            await httpClient.PutAsync(uri, new StringContent(content, Encoding.Default, "application/json"));
            return true;
        }
        catch (Exception e)
        {
            LogException(e);
            return false;
        }
    }

    private void Send(params Press[] presses)
    {
        lock (_sendLock)
        {
            var generation = _sendGeneration;
            _queuedPresses += presses.Length;
            foreach (var press in presses)
                _sends = _sends.ContinueWith(_ => SendPress(generation, press), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SendPress(int generation, Press press)
    {
        try
        {
            lock (_sendLock)
            {
                if (generation != _sendGeneration)
                    return;
                _queuedPresses--;
            }
            if (await Put(press.Uri, press.Content))
                await Task.Delay(press.SpacingAfter);
            else
                DropQueuedPresses();
        }
        catch (Exception e)
        {
            LogException(e);
        }
    }

    private void DropQueuedPresses()
    {
        int dropped;
        lock (_sendLock)
        {
            dropped = _queuedPresses;
            _queuedPresses = 0;
            _sendGeneration++;
        }
        if (dropped == 0)
            return;
        using (PushProperties("DropQueuedPresses"))
            LogWarning("The Vitec didn't answer, dropping {Dropped} queued presses", dropped);
    }

    private Press KeyPress(string key, TimeSpan spacingAfter) =>
        new(_remoteKeyUri, $"{{ \"key\": \"rm_{key}\" }}", spacingAfter);

    private void SimulateRemoteKeypress(string key) => Send(KeyPress(key, TimeSpan.Zero));

    public void ChannelUp() => SendIRCode(RemoteButton.ChannelUp);

    public void ChannelDown() => SendIRCode(RemoteButton.ChannelDown);

    public void VolumeUp() => SendIRCode(RemoteButton.VolumeUp);

    public void VolumeDown() => SendIRCode(RemoteButton.VolumeDown);

    public void SetChannel(int channel)
    {
        if (channel < 0)
        {
            using (PushProperties("SetChannel"))
                LogError("Channel {Channel} is invalid, it can't be negative", channel);
            return;
        }

        if (channel < FirstTwoDigitChannel)
            SimulateRemoteKeypress($"{channel}");
        else
            Send(channel.ToString().Select(digit => KeyPress($"{digit}", DigitSpacing)).ToArray());
    }

    public void ToggleSubtitles()
    {
        lock (_sendLock)
        {
            string[] menuKeys =
                [RemoteButtonMap[RemoteButton.Subtitle], ..Enumerable.Repeat(RemoteButtonMap[RemoteButton.Down], _subtitleOption)];
            Send([
                ..menuKeys[..^1].Select(key => KeyPress(key, SubtitleKeySpacing)),
                KeyPress(menuKeys[^1], SubtitleKeySpacing + SubtitleMenuTime),
                KeyPress(RemoteButtonMap[RemoteButton.Enter], TimeSpan.Zero)
            ]);

            _subtitleOption++;
            if (_subtitleOption > LastSubtitleOption)
                _subtitleOption = 0;
        }
    }

    public IReadOnlyCollection<RemoteButton> SupportedButtons => RemoteButtonMap.Keys;

    public void SendIRCode(RemoteButton button)
    {
        if (!RemoteButtonMap.TryGetValue(button, out var command))
        {
            using (PushProperties("SendIRCode"))
                LogError("Unsupported button - {UnsupportedRemoteButton}", button.ToString());
            return;
        }

        SimulateRemoteKeypress(command);
    }

    public override void PowerOn()
    {
        Send(new Press(_deviceStateUri, "{ \"devicestate\": \"on\" }", TimeSpan.Zero));
    }

    public override void PowerOff()
    {
        Send(new Press(_deviceStateUri, "{ \"devicestate\": \"off\" }", TimeSpan.Zero));
    }
}