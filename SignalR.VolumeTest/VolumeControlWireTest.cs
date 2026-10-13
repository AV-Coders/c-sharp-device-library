using System.Buffers;
using System.Text;
using System.Text.Json;
using AVCoders.Core;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace AVCoders.SignalR.Volume.Tests;

public class VolumeControlWireTest
{
    private const char RecordSeparator = '\u001e';

    private class LevelOnlyVolumeControl() : TestVolumeControl("Program")
    {
        public override bool SupportsMute => false;
    }

    [Fact]
    public void OnVolumeControlsChanged_SendsWhichHalvesEachControlSupports()
    {
        List<VolumeControl> controls = [new LevelOnlyVolumeControl()];
        var buffer = new ArrayBufferWriter<byte>();

        new JsonHubProtocol().WriteMessage(new InvocationMessage(nameof(IVolumeHub.OnVolumeControlsChanged), [controls]), buffer);

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan).TrimEnd(RecordSeparator));
        var control = document.RootElement.GetProperty("arguments")[0][0];
        Assert.True(control.GetProperty("supportsLevel").GetBoolean());
        Assert.False(control.GetProperty("supportsMute").GetBoolean());
    }
}
