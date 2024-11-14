using AVCoders.Core;

namespace AVCoders.Camera;

public class AverVisca : SonyVisca
{
    public static readonly SerialSpec DefaultSerialConfig = new SerialSpec(
        SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232
    );
    public AverVisca(CommunicationClient client, bool useIpHeaders = false, byte cameraId = 1) : base(client, useIpHeaders, cameraId)
    {
    }

    public void SetTracking(CameraTrackingMode mode)
    {
        switch (mode)
        {
            case CameraTrackingMode.Auto:
                SendCommand(new byte[] { _header, 0x01, 0x04, 0x7D, 0x02, 0x00, CommandFooter }); 
                Log("Tracking Mode: Auto");
                break;
            case CameraTrackingMode.Disabled:
                SendCommand(new byte[] { _header, 0x01, 0x04, 0x7D, 0x01, 0x00, CommandFooter });
                Log("Tracking Mode: Disabled");
                break;
            case CameraTrackingMode.TriggerOnce:
                SendCommand(new byte[] { _header, 0x01, 0x04, 0x7D, 0x00, 0x00, CommandFooter });
                Log("Tracking Mode: Trigger Once");
                break;
            case CameraTrackingMode.Manual:
                SendCommand(new byte[] { _header, 0x01, 0x04, 0x7D, 0x03, 0x00, CommandFooter });
                Log("Tracking Mode: Manual");
                break;
        }
    }
}