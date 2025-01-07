using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public class Navigator : DeviceBase
{
    public static readonly ushort DefaultPort = 22023;
    public readonly SshClient SshClient;
    private Dictionary<string, Action<string>> _callbacks;
    private readonly Regex _deviceResponseParser;
    private readonly string EscapeHeader = "\x1b";
    

    public Navigator(SshClient sshClient)
    {
        SshClient = sshClient;
        SshClient.ResponseHandlers += HandleResponse;
        SshClient.ConnectionStateHandlers += HandleConnectionState;
        _callbacks = new Dictionary<string, Action<string>>();
        
        string responsePattern = @"\{(?<device>.*?)\}(?<response>.*?)";
        _deviceResponseParser = new Regex(responsePattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private void HandleConnectionState(ConnectionState connectionstate)
    {
        if (connectionstate != ConnectionState.Connected)
            return;
        SshClient.Send($"{EscapeHeader}3CV\r");
    }

    private void HandleResponse(string response)
    {
        if (response.StartsWith('{'))
        {
            ForwardDeviceResponse(response);
            return;
        }
    }

    public void SendCommandToDevice(string deviceId, string command)
    {
        SshClient.Send($"{{{deviceId}:{command}}}\r");
    }

    private void ForwardDeviceResponse(string response)
    {
        var hostEndIndex = response.IndexOf('}');
        if (hostEndIndex == -1)
        {
            Error("} was not found");
            return;
        }
        var respondant = response.Substring(0, hostEndIndex).Trim('{').Trim('}');
        if (_callbacks.TryGetValue(respondant, out Action<string>? action))
        {
            action.Invoke(response.Substring(hostEndIndex + 1, response.Length - hostEndIndex - 1));
        }
        else
            Error($"Nav has returned a response for a device that's not registered to this module: {respondant}");
    }

    public virtual void RegisterDevice(string deviceId, Action<string> responseHandler)
    {
        _callbacks.Add(deviceId, responseHandler);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
}