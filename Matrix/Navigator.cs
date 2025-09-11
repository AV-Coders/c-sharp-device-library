using System.Text.RegularExpressions;
using AVCoders.Core;
using Serilog;

namespace AVCoders.Matrix;

public class Navigator : DeviceBase
{
    public static readonly ushort DefaultPort = 22023;
    public readonly SshClient SshClient;
    private readonly Dictionary<string, Action<string>> _callbacks;
    private readonly Regex _deviceResponseParser;
    public const string EscapeHeader = "\x1b";
    private int _unansweredDeviceForwards = 0;
    

    public Navigator(string name, SshClient sshClient) : base(name, sshClient, CommandStringFormat.Ascii)
    {
        SshClient = sshClient;
        CommunicationClient.ResponseHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        _callbacks = new Dictionary<string, Action<string>>();
        
        string responsePattern = @"\{(?<device>.*?)\}(?<response>.*?)";
        _deviceResponseParser = new Regex(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        CommunicationClient.Send($"{EscapeHeader}3CV\r");
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            if (response.StartsWith('{'))
            {
                ForwardDeviceResponse(response);
                _unansweredDeviceForwards = 0;
                CommunicationState = CommunicationState.Okay;
            }
        }
    }
    public virtual void RouteAV(uint input, uint output) => CommunicationClient.Send($"{EscapeHeader}{input}*{output}!\r");
    public void RouteAudio(uint input, uint output) => CommunicationClient.Send($"{EscapeHeader}{input}*{output}$\r");
    public void RouteVideo(uint input, uint output) => CommunicationClient.Send($"{EscapeHeader}{input}*{output}%\r");

    public void SendCommandToDevice(string deviceId, string command) => CommunicationClient.Send($"{{{deviceId}:{command}}}\r");

    private void ForwardDeviceResponse(string response)
    {
        var hostEndIndex = response.IndexOf('}');
        if (hostEndIndex == -1)
        {
            Log.Error("} was not found");
            return;
        }
        var respondant = response.Substring(0, hostEndIndex).Trim('{').Trim('}');
        if (_callbacks.TryGetValue(respondant, out Action<string>? action))
        {
            action.Invoke(response.Substring(hostEndIndex + 1, response.Length - hostEndIndex - 1));
        }
        else
            Log.Error("Nav has returned a response for a device that's not registered to this module: {Respondant}", respondant);
        _unansweredDeviceForwards++;
        if (_unansweredDeviceForwards > 5)
            CommunicationState = CommunicationState.Error;
    }

    public virtual void RegisterDevice(string deviceId, Action<string> responseHandler)
    {
        _callbacks.Add(deviceId, responseHandler);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
}