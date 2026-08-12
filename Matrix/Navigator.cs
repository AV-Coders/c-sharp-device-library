using System.Text.RegularExpressions;
using AVCoders.Core;

namespace AVCoders.Matrix;

public class Navigator : VideoMatrix
{
    public static readonly ushort DefaultPort = 22023;
    public readonly SshClient SshClient;
    private readonly Dictionary<string, Action<string>> _callbacks;
    private readonly Regex _deviceResponseParser;
    public const string EscapeHeader = "\x1b";
    private int _unansweredDeviceForwards = 0;

    private readonly List<NavEncoder> _inputs = [];
    private readonly List<NavDecoder> _outputs = [];

    private static readonly Dictionary<string, string> ErrorDescriptions = new()
    {
        { "E10", "Invalid command" },
        { "E12", "Invalid port number" },
        { "E13", "Invalid parameter" },
        { "E14", "Invalid for this port configuration" },
        { "E17", "Invalid command for signal type" },
        { "E22", "Busy" },
        { "E24", "Privilege violation" },
        { "E25", "Device not present" },
        { "E28", "Bad file name or file not found" },
    };


    public Navigator(string name, SshClient sshClient) : base(0, sshClient, name)
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
            else if (response.StartsWith("Out"))
                HandleTieResponse(response);
            else if (response.StartsWith("Devp"))
                HandleDevicePresenceResponse(response);
            else if (response.Length == 3 && response.StartsWith('E') && char.IsDigit(response[1]) && char.IsDigit(response[2]))
                LogWarning("The Navigator rejected a command: {ErrorCode} - {ErrorMeaning}", response,
                    ErrorDescriptions.GetValueOrDefault(response, "Unknown error"));
        }
    }

    private void HandleTieResponse(string response)
    {
        var parts = response.Split(' ');
        if (parts.Length != 3 || !int.TryParse(parts[0][3..], out var outputNumber))
            return;
        CommunicationState = CommunicationState.Okay;
        if (_callbacks.TryGetValue($"{outputNumber:D4}o", out Action<string>? action))
            action.Invoke($"{parts[1]} {parts[2]}");
    }

    private void HandleDevicePresenceResponse(string response)
    {
        var parts = response.Split('*');
        if (parts.Length != 3)
            return;
        CommunicationState = CommunicationState.Okay;
        var endpoint = FindEndpoint(parts[1]);
        if (endpoint == null)
            return;
        switch (parts[0])
        {
            case "DevpP":
            case "DevpC":
                var tunnel = (NavigatorTunnel)endpoint.CommunicationClient;
                tunnel.SetConnectionState(parts[2] == "1" ? ConnectionState.Connected : ConnectionState.Disconnected);
                break;
        }
    }

    private NavDeviceBase? FindEndpoint(string endpointId)
    {
        if (endpointId.Length < 2 || !int.TryParse(endpointId[..^1], out var deviceNumber))
            return null;
        return endpointId[^1] switch
        {
            'i' => _inputs.FirstOrDefault(x => x.DeviceNumber == deviceNumber),
            'o' => _outputs.FirstOrDefault(x => x.DeviceNumber == deviceNumber),
            _ => null
        };
    }
    public override void RouteAV(int input, int output)
    {
        FindDecoder(output)?.UpdateDesiredRoute(input, input);
        CommunicationClient.Send($"{EscapeHeader}{input}*{output}!\r");
    }

    public override void RouteAudio(int input, int output)
    {
        FindDecoder(output)?.UpdateDesiredRoute(null, input);
        CommunicationClient.Send($"{EscapeHeader}{input}*{output}$\r");
    }

    public override void RouteVideo(int input, int output)
    {
        FindDecoder(output)?.UpdateDesiredRoute(input, null);
        CommunicationClient.Send($"{EscapeHeader}{input}*{output}%\r");
    }

    private NavDecoder? FindDecoder(int outputNumber) => _outputs.FirstOrDefault(x => x.DeviceNumber == outputNumber);
    
    public void RouteUsb(NavDeviceBase host, NavDeviceBase device)
    {
        var inputString = $"{host.DeviceNumber}{host.GetLetterForDeviceType()}";
        var outputString = $"{device.DeviceNumber}{device.GetLetterForDeviceType()}";
        CommunicationClient.Send($"{EscapeHeader}{inputString}*{outputString}^\r");
    }
    
    public void DerouteUsb(NavDeviceBase device)
    {
        var outputString = $"{device.DeviceNumber}{device.GetLetterForDeviceType()}";
        CommunicationClient.Send($"{EscapeHeader}0i*{outputString}^\r");
    }
    
    public void DerouteAllUsb()
    {
        CommunicationClient.Send($"{EscapeHeader}0i*^\r");
    }


    public void SendCommandToDevice(string deviceId, string command) => CommunicationClient.Send($"{{{deviceId}:{command}}}\r");

    private void ForwardDeviceResponse(string response)
    {
        using (PushProperties("ForwardDeviceResponse"))
        {
            var hostEndIndex = response.IndexOf('}');
            if (hostEndIndex == -1)
            {
                LogWarning("} was not found");
                return;
            }
            var respondant = response.Substring(0, hostEndIndex).Trim('{').Trim('}');
            if (_callbacks.TryGetValue(respondant, out Action<string>? action))
            {
                action.Invoke(response.Substring(hostEndIndex + 1, response.Length - hostEndIndex - 1));
            }
            else
                LogVerbose("Navigator has returned a response for a device that's not registered to this module: {Respondant}", respondant);
            _unansweredDeviceForwards++;
            if (_unansweredDeviceForwards > 5)
                CommunicationState = CommunicationState.Error;
        }
    }

    public virtual void RegisterDevice(string deviceId, Action<string> responseHandler)
    {
        _callbacks.Add(deviceId, responseHandler);
    }

    public override void PowerOn() { }

    public override void PowerOff() { }
    public override int NumberOfOutputs { get => _outputs.Count; }
    public override int NumberOfInputs { get => _inputs.Count; }
    public override bool RequiresOutputSpecification { get => true; }
    public override bool SupportsVideoBreakaway { get => true; }
    public override bool SupportsAudioBreakaway { get => true; }
    
    public override List<SyncStatus> GetInputs() => [.._inputs];
    public override List<SyncStatus> GetOutputs() => [.._outputs];

    public void AddEndpoint(NavDeviceBase navDeviceBase)
    {
        switch (navDeviceBase.DeviceType)
        {
            case AVEndpointType.Encoder:
                _inputs.Add((NavEncoder)navDeviceBase);
                break;
            case AVEndpointType.Decoder:
                _outputs.Add((NavDecoder)navDeviceBase);
                break;
        }
    }
}