using AVCoders.Core;
using EventLevel = AVCoders.Core.EventLevel;

namespace AVCoders.Camera;

public class SonyVisca : CameraBase
{
    private enum PayloadType
    {
        ViscaCommand, ViscaInquiry, ViscaReply, DeviceSetting, ControlCommand, ControlReply
    }
    private readonly CommunicationClient _communicationClient;
    private readonly bool _useIpHeaders;
    private byte _panSpeed;
    private byte _tiltSpeed;
    private byte _zoomInSpeed;
    private byte _zoomOutSpeed;
    private byte _sequenceNumber;
    private static readonly byte[] SequenceHeader = { 0xFF, 0xFF, 0xFF };
    protected byte _header;
    protected static readonly byte CommandFooter = 0xFF;
    private readonly Dictionary<PayloadType, byte[]> _ipHeaders = new Dictionary<PayloadType, byte[]>();

    public SonyVisca(CommunicationClient client, bool useIpHeaders, byte cameraId = 0x01)
    {
        _communicationClient = client;
        _useIpHeaders = useIpHeaders;
        SetCameraId(cameraId);
        SetCameraId(cameraId);
        _communicationClient.ResponseHandlers += HandleResponse;
        _panSpeed = 0x04;
        _tiltSpeed = 0x04;
        _zoomInSpeed = 0x23;
        _zoomOutSpeed = 0x33;
        CommunicationState = CommunicationState.NotAttempted;
        _sequenceNumber = 0x00;
        _useIpHeaders = useIpHeaders;
        _ipHeaders.Add(PayloadType.ViscaCommand, new byte[]{ 0x01, 0x00 });
        _ipHeaders.Add(PayloadType.ViscaInquiry, new byte[]{ 0x01, 0x10 });
        _ipHeaders.Add(PayloadType.ViscaReply, new byte[]{ 0x01, 0x11 });
        _ipHeaders.Add(PayloadType.DeviceSetting, new byte[]{ 0x01, 0x10 });
        _ipHeaders.Add(PayloadType.ControlCommand, new byte[]{ 0x02, 0x00 });
        _ipHeaders.Add(PayloadType.ControlReply, new byte[]{ 0x02, 0x01 });
    }

    protected void SendCommand(byte[] bytes)
    {
        try
        {
            CommunicationState = CommunicationState.Okay;
            if (_useIpHeaders)
            {
                _communicationClient.Send(PayloadWithIpHeader(PayloadType.ViscaCommand, bytes));
                return;
            }
            _communicationClient.Send(bytes);
        }
        catch (Exception e)
        {
            LogHandlers?.Invoke($"VISCACamera - Communication error: {e.Message}", EventLevel.Error);
            CommunicationState = CommunicationState.Error;
        }
            
    }

    private byte[] PayloadWithIpHeader(PayloadType payloadType, byte[] payload)
    {
        List<byte> bytes = new List<byte>();
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
        SendCommand(new byte[] { _header, 0x01, 0x04, 0x00, 0x03, CommandFooter });
        DesiredPowerState = PowerState.Off;
        Log("Power Off");
    }

    public override void PowerOn()
    {
        SendCommand(new byte[] { _header, 0x01, 0x04, 0x00, 0x02, CommandFooter });
        DesiredPowerState = PowerState.On;
        Log("Power On");
    }public override void ZoomStop()
    {
        SendCommand(new byte[] { _header, 0x01, 0x04, 0x07, 0x00, CommandFooter });
        Log("Zoom Stop");
    }

    public override void ZoomIn()
    {
        SendCommand(new byte[] { _header, 0x01, 0x04, 0x07, _zoomInSpeed, CommandFooter });
        Log("Zooming In");
    }

    public override void ZoomOut()
    {
        SendCommand(new byte[] { _header, 0x01, 0x04, 0x07, _zoomOutSpeed, CommandFooter });
        Log("Zooming Out");
    }

    public override void PanTiltStop()
    {
        SendCommand(new byte[] { _header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x03, CommandFooter });
        Log("PTZ Stop");
    }

    public override void PanTiltUp()
    {
        SendCommand(new byte[] { _header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x01, CommandFooter });
        Log("PTZ Up");
    }

    public override void PanTiltDown()
    {
        SendCommand(new byte[] { _header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x02, CommandFooter });
        Log("PTZ Down");
    }

    public override void PanTiltLeft()
    {
        SendCommand(new byte[] { _header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x01, 0x03, CommandFooter });
        Log("PTZ Left");
    }

    public override void PanTiltRight()
    {
        SendCommand(new byte[] { _header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x02, 0x03, CommandFooter });
        Log("PTZ Right");
    }

    public override void DoRecallPreset(int presetNumber)
    {
        SendCommand(new byte[] { _header, 0x01, 0x04, 0x3f, 0x02, (byte)presetNumber, CommandFooter });
        Log($"Recall Preset {presetNumber}");
    }

    public override void SavePreset(int presetNumber)
    {
        SendCommand(new byte[] { _header, 0x01, 0x04, 0x3f, 0x01, (byte)presetNumber, CommandFooter });
        Log($"Save Preset {presetNumber}");
    }

    private void HandleResponse(String response)
    {
        //TODO: Handle responses
    }
}