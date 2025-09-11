using AVCoders.Core;
using Serilog;

namespace AVCoders.Camera;

public class LumensCL511(string name, CommunicationClient client) : CameraBase(name, client, CommandStringFormat.Hex)
{
    public static readonly SerialSpec DefaultSpec = new SerialSpec(SerialBaud.Rate9600, SerialParity.None,
        SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public const uint DefaultPort = 52381;
    
    public override void PowerOn()
    {
        CommunicationClient.Send([0xA0, 0xB1, 0x01, 0x00, 0x00, 0xAF]);
    }

    public override void PowerOff()
    {
        CommunicationClient.Send([0xA0, 0xB1, 0x00, 0x00, 0x00, 0xAF]);
    }

    public override void ZoomStop()
    {
        CommunicationClient.Send([0xA0, 0x11, 0x00, 0x00, 0x00, 0xAF]);
    }

    public override void ZoomIn()
    {
        CommunicationClient.Send([0xA0, 0x11, 0x23, 0x00, 0x00, 0xAF]);
    }

    public override void ZoomOut()
    {
        CommunicationClient.Send([0xA0, 0x11, 0x33, 0x00, 0x00, 0xAF]);
    }

    public override void PanTiltStop()
    {
        Log.Error("LumensCL511 module doesn't support Pan / Tilt");
    }

    public override void PanTiltUp()
    {
        Log.Error("LumensCL511 module doesn't support Pan / Tilt");
    }

    public override void PanTiltDown()
    {
        Log.Error("LumensCL511 module doesn't support Pan / Tilt");
    }

    public override void PanTiltLeft()
    {
        Log.Error("LumensCL511 module doesn't support Pan / Tilt");
    }

    public override void PanTiltRight()
    {
        Log.Error("LumensCL511 module doesn't support Pan / Tilt");
    }

    public override void DoRecallPreset(int presetNumber)
    {
        CommunicationClient.Send([0xA0, 0x03, 0x00, 0x00, (byte)presetNumber, 0xAF]);
    }

    public override void SavePreset(int presetNumber)
    {
        CommunicationClient.Send([0xA0, 0x03, 0x00, 0x01, (byte) presetNumber, 0xAF]);
    }
}