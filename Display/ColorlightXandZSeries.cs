using AVCoders.Core;

namespace AVCoders.Display;

public class ColorlightXandZSeries : Display
{
    public const ushort TcpPort = 9999;
    public const ushort UdpPort = 9099;
    private readonly TcpClient _comms;
    private readonly int? _powerOnPreset;
    private readonly int? _powerOffPreset;
    private readonly byte[] _heartbeat = [0x99, 0x99, 0x04, 0x00];


    public ColorlightXandZSeries(TcpClient comms, string name, int? powerOnPreset, int? powerOffPreset) : base(new List<Input>(), name, Input.Unknown, 1)
    {
        _comms = comms;
        _powerOnPreset = powerOnPreset;
        _powerOffPreset = powerOffPreset;
    }

    private void RecallPreset(int preset)
    {
        _comms.Send(new byte[] { 0x74, 0x00, 0x11, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0x00, 0x00, });
    }

    protected override Task Poll(CancellationToken token)
    {
        _comms.Send(_heartbeat);
        return Task.CompletedTask;
    }

    protected override void DoPowerOn()
    {
        if (_powerOnPreset != null)
            RecallPreset(_powerOnPreset.Value);
    }

    protected override void DoPowerOff()
    {
        if(_powerOffPreset != null)
            RecallPreset(_powerOffPreset.Value);
    }

    protected override void DoSetInput(Input input) => Debug("This module does not support input select");

    protected override void DoSetVolume(int percentage) => Debug("This device does not support volume");

    protected override void DoSetAudioMute(MuteState state) => Debug("This device does not support audio mute");
}