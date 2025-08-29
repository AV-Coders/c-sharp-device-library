using AVCoders.Core;
using Serilog;

namespace AVCoders.Camera;

public class SonyVisca : CameraBase
{
    private enum PayloadType
    {
        ViscaCommand, ViscaInquiry, ViscaReply, DeviceSetting, ControlCommand, ControlReply
    }
    private readonly bool _useIpHeaders;
    private byte _panSpeed;
    private byte _tiltSpeed;
    private byte _zoomInSpeed;
    private byte _zoomOutSpeed;
    private byte _sequenceNumber;
    private static readonly byte[] SequenceHeader = [0xFF, 0xFF, 0xFF];
    protected byte _header;
    protected static readonly byte CommandFooter = 0xFF;
    private readonly Dictionary<PayloadType, byte[]> _ipHeaders = new Dictionary<PayloadType, byte[]>();

    public SonyVisca(CommunicationClient client, bool useIpHeaders, string name, byte cameraId = 0x01) : base(name, client)
    {
        _useIpHeaders = useIpHeaders;
        SetCameraId(cameraId);
        SetCameraId(cameraId);
        CommunicationClient.ResponseHandlers += HandleResponse;
        _panSpeed = 0x04;
        _tiltSpeed = 0x04;
        _zoomInSpeed = 0x23;
        _zoomOutSpeed = 0x33;
        CommunicationState = CommunicationState.NotAttempted;
        _sequenceNumber = 0x00;
        _useIpHeaders = useIpHeaders;
        _ipHeaders.Add(PayloadType.ViscaCommand, [0x01, 0x00]);
        _ipHeaders.Add(PayloadType.ViscaInquiry, [0x01, 0x10]);
        _ipHeaders.Add(PayloadType.ViscaReply, [0x01, 0x11]);
        _ipHeaders.Add(PayloadType.DeviceSetting, [0x01, 0x10]);
        _ipHeaders.Add(PayloadType.ControlCommand, [0x02, 0x00]);
        _ipHeaders.Add(PayloadType.ControlReply, [0x02, 0x01]);
    }

    protected void SendCommand(byte[] bytes)
    {
        try
        {
            CommunicationState = CommunicationState.Okay;
            if (_useIpHeaders)
            {
                CommunicationClient.Send(PayloadWithIpHeader(PayloadType.ViscaCommand, bytes));
                return;
            }
            CommunicationClient.Send(bytes);
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
            
    }

    private byte[] PayloadWithIpHeader(PayloadType payloadType, byte[] payload)
    {
        List<byte> bytes = [];
        foreach (var theByte in _ipHeaders[payloadType])
        { bytes.Add(theByte); }
        bytes.Add(0x00);
        bytes.Add((byte)payload.Length);
        bytes.Add(0xFF);
        bytes.Add(0xFF);
        bytes.Add(0xFF);
        bytes.Add(_sequenceNumber);
        _sequenceNumber++;
        foreach (byte b in payload)
        {
            bytes.Add(b);
        }
        return bytes.ToArray();
    }

    public void SetCameraId(byte cameraId)
    {
        _header = (byte)(0x80 + cameraId);
    }

    public void SetPanSpeed(byte speed)
    {
        _panSpeed = speed;
    }

    public void SetTiltSpeed(byte speed)
    {
        _tiltSpeed = speed;
    }

    public void SetZoomSpeed(byte speed)
    {
        _zoomInSpeed = (byte)(speed + 0x20);
        _zoomOutSpeed = (byte)(speed + 0x30);
    }

    public override void PowerOff()
    {
        SendCommand([_header, 0x01, 0x04, 0x00, 0x03, CommandFooter]);
        DesiredPowerState = PowerState.Off;
        Log.Verbose("Power Off");
    }

    public override void PowerOn()
    {
        SendCommand([_header, 0x01, 0x04, 0x00, 0x02, CommandFooter]);
        DesiredPowerState = PowerState.On;
        Log.Verbose("Power On");
    }public override void ZoomStop()
    {
        SendCommand([_header, 0x01, 0x04, 0x07, 0x00, CommandFooter]);
        Log.Verbose("Zoom Stop");
    }

    public override void ZoomIn()
    {
        SendCommand([_header, 0x01, 0x04, 0x07, _zoomInSpeed, CommandFooter]);
        Log.Verbose("Zooming In");
    }

    public override void ZoomOut()
    {
        SendCommand([_header, 0x01, 0x04, 0x07, _zoomOutSpeed, CommandFooter]);
        Log.Verbose("Zooming Out");
    }

    public override void PanTiltStop()
    {
        SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x03, CommandFooter]);
        Log.Verbose("PTZ Stop");
    }

    public override void PanTiltUp()
    {
        SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x01, CommandFooter]);
        Log.Verbose("PTZ Up");
    }

    public override void PanTiltDown()
    {
        SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x02, CommandFooter]);
        Log.Verbose("PTZ Down");
    }

    public override void PanTiltLeft()
    {
        SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x01, 0x03, CommandFooter]);
        Log.Verbose("PTZ Left");
    }

    public override void PanTiltRight()
    {
        SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x02, 0x03, CommandFooter]);
        Log.Verbose("PTZ Right");
    }

    public override void DoRecallPreset(int presetNumber)
    {
        SendCommand([_header, 0x01, 0x04, 0x3f, 0x02, (byte)presetNumber, CommandFooter]);
        Log.Verbose("Recall Preset {PresetNumber}", presetNumber);
    }

    public override void SavePreset(int presetNumber)
    {
        SendCommand([_header, 0x01, 0x04, 0x3f, 0x01, (byte)presetNumber, CommandFooter]);
        Log.Verbose("Save Preset {PresetNumber}", presetNumber);
    }

    private void HandleResponse(string response)
    {
        //TODO: Handle responses
    }
}