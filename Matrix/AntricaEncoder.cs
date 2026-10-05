using System.Globalization;
using AVCoders.Core;

namespace AVCoders.Matrix;

public class AntricaEncoder : SyncStatus
{
    private const string VideoLossKey = "GIS_VIDEOLOSS1";
    private const string InputFormatKey = "VID_INPUTFORMAT";
    private const string VideoDetected = "0";
    private const string VideoLost = "1";
    private const string HexPrefix = "0x";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly char[] LineSeparators = ['\r', '\n'];

    private static readonly Dictionary<int, string> InputFormats = new()
    {
        [0x0000] = string.Empty,
        [0x0300] = "480p30",
        [0x0301] = "480p60",
        [0x0302] = "480i60",
        [0x0303] = "576p50",
        [0x0304] = "576i50",
        [0x0305] = "720p25",
        [0x0306] = "720p30",
        [0x0307] = "720p60",
        [0x0308] = "1080p25",
        [0x0309] = "1080p30",
        [0x030A] = "1080p60",
        [0x030B] = "1080i60",
        [0x030C] = "1080i50",
        [0x030D] = "1080p50",
        [0x030E] = "720p50",
        [0x030F] = "1280x1024p30",
        [0x0310] = "1280x960p30",
        [0x0311] = "1024x768p30",
        [0x0312] = "1280x1024p60",
        [0x0313] = "1280x960p60",
        [0x0314] = "1024x768p60",
        [0x0315] = "1080i59.94",
        [0x0316] = "1440x900p60",
        [0x0317] = "800x600p60",
        [0x0318] = "1080p23.98",
        [0x0319] = "1920x1200p60",
        [0x031A] = "1920x1200p50",
        [0x031B] = "3840x2160p25",
        [0x031C] = "3840x2160p30",
        [0x031D] = "4096x2160p25",
        [0x031E] = "4096x2160p30",
    };

    private readonly RestComms _restClient;
    private readonly Uri _videoLossUri = new($"/httpapi/GetState?action=getinput&{VideoLossKey}=0", UriKind.Relative);
    private readonly Uri _inputFormatUri = new($"/httpapi/ReadParam?action=readparam&{InputFormatKey}=0", UriKind.Relative);
    private readonly ThreadWorker _pollWorker;

    public AntricaEncoder(string name, RestComms restClient) : base(name, AVEndpointType.Encoder)
    {
        _restClient = restClient;
        InputHdcpStatus = HdcpStatus.NotSupported;
        _restClient.ResponseHandlers += HandleResponse;
        _restClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(_restClient.ConnectionState);
        _pollWorker = new ThreadWorker(Poll, PollInterval);
        _ = _pollWorker.Restart();
    }

    private async Task Poll(CancellationToken token)
    {
        await _restClient.Get(_videoLossUri);
        await _restClient.Get(_inputFormatUri);
    }

    private void HandleConnectionState(ConnectionState state)
    {
        DeviceConnectionState = state;
        if (state == ConnectionState.Connected)
            return;
        InputConnectionStatus = ConnectionState.Unknown;
        InputResolution = string.Empty;
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = line.Trim().Split('=', 2);
                switch (pair[0])
                {
                    case VideoLossKey:
                        HandleVideoLoss(pair[1]);
                        break;
                    case InputFormatKey:
                        HandleInputFormat(pair[1]);
                        break;
                    default:
                        LogWarning("The encoder replied {Reply}", line);
                        break;
                }
            }
        }
    }

    private void HandleVideoLoss(string value)
    {
        switch (value)
        {
            case VideoDetected:
                InputConnectionStatus = ConnectionState.Connected;
                break;
            case VideoLost:
                InputConnectionStatus = ConnectionState.Disconnected;
                break;
            default:
                LogWarning("The encoder replied {Key}={Value}", VideoLossKey, value);
                break;
        }
    }

    private void HandleInputFormat(string value)
    {
        if (!TryParseFormatCode(value, out var code))
        {
            LogWarning("The encoder replied {Key}={Value}", InputFormatKey, value);
            return;
        }

        if (InputFormats.TryGetValue(code, out var resolution))
        {
            InputResolution = resolution;
            return;
        }

        LogWarning("Unrecognised input format {InputFormat}", value);
        InputResolution = value;
    }

    private static bool TryParseFormatCode(string value, out int code) =>
        value.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(value[HexPrefix.Length..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out code);
}
