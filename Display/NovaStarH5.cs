using AVCoders.Core;
using Newtonsoft.Json;

namespace AVCoders.Display;

public record NovastarH5BasePayload(
    [property: JsonProperty("cmd")] string Command,
    [property: JsonProperty("deviceId")] int DeviceId,
    [property: JsonProperty("screenId")] int ScreenId,
    [property: JsonProperty("presetId", NullValueHandling=NullValueHandling.Ignore)] int? PresetId,
    [property: JsonProperty("brightness", NullValueHandling=NullValueHandling.Ignore)] int? Brightness
);

public class NovaStarH5 : Display
{
    public const ushort DefaultPort = 6000;
    private readonly UdpClient _client;
    private readonly int _deviceId;
    private readonly List<int> _screens;
    private readonly int? _powerOnPreset;
    private readonly int? _powerOffPreset;
    private readonly NovastarH5BasePayload _basePayload;

    public NovaStarH5(UdpClient client, int deviceId, List<int> screens, string name, int? powerOnPreset, int? powerOffPreset) : base(new List<Input>(), name)
    {
        _client = client;
        _deviceId = deviceId;
        _screens = screens;
        _powerOnPreset = powerOnPreset;
        _powerOffPreset = powerOffPreset;
        _basePayload = new NovastarH5BasePayload("", deviceId, 0, null, null);
    }

    public void RecallPreset(int preset) => _screens.ForEach(x => RecallPreset(preset, x));

    private void SendCommand(NovastarH5BasePayload payload) =>
        _client.Send(JsonConvert.SerializeObject(new List<NovastarH5BasePayload> { payload }));

    public void RecallPreset(int preset, int screen)
    {
        SendCommand(_basePayload with
        {
            Command = "W0605",
            ScreenId = screen,
            PresetId = preset
        });
    }

    public void SetBrightness(int brightness) => _screens.ForEach(x => SetBrightness(brightness, x));

    public void SetBrightness(int brightness, int screen)
    {
        SendCommand(_basePayload with
        {
            Command = "W0410",
            ScreenId = screen,
            Brightness = brightness
        });
    }

    protected override Task Poll(CancellationToken token) => PollWorker.Stop();

    protected override void DoPowerOn()
    {
        if (_powerOnPreset != null)
            RecallPreset((int)_powerOnPreset);
    }

    protected override void DoPowerOff()
    {
        if(_powerOffPreset != null)
            RecallPreset((int)_powerOffPreset);
    }

    protected override void DoSetInput(Input input) => Log("This module does not support input select");

    protected override void DoSetVolume(int percentage) => Log("This device does not support volume");

    protected override void DoSetAudioMute(MuteState state) => Log("This device does not support audio mute");
}