using AVCoders.Core;

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
    protected byte _header;
    protected static readonly byte CommandFooter = 0xFF;
    private const int IpHeaderLength = 8;
    private readonly Dictionary<PayloadType, byte[]> _ipHeaders = new Dictionary<PayloadType, byte[]>();
    private record PendingCommand(string Description, Action? OnCompleted);

    private readonly bool _deviceSendsResponses;
    private readonly Dictionary<byte, PendingCommand> _pendingCommands = new Dictionary<byte, PendingCommand>();
    private PendingCommand? _lastCommand;

    private static readonly Dictionary<byte, string> ErrorMessages = new Dictionary<byte, string>
    {
        { 0x01, "Message length error" },
        { 0x02, "Syntax error" },
        { 0x03, "Command buffer full" },
        { 0x04, "Command cancelled" },
        { 0x05, "No socket" },
        { 0x41, "Command not executable" }
    };

    public SonyVisca(CommunicationClient client, bool useIpHeaders, string name, Dictionary<int, string> presetNames, byte cameraId = 0x01, int pollTime = 30, bool deviceSendsResponses = true)
        : base(name, client, presetNames)
    {
        _useIpHeaders = useIpHeaders;
        _deviceSendsResponses = deviceSendsResponses;
        SetCameraId(cameraId);
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        _panSpeed = 0x04;
        _tiltSpeed = 0x04;
        _zoomInSpeed = 0x23;
        _zoomOutSpeed = 0x33;
        CommunicationState = CommunicationState.NotAttempted;
        _sequenceNumber = 0x00;
        _ipHeaders.Add(PayloadType.ViscaCommand, [0x01, 0x00]);
        _ipHeaders.Add(PayloadType.ViscaInquiry, [0x01, 0x10]);
        _ipHeaders.Add(PayloadType.ViscaReply, [0x01, 0x11]);
        _ipHeaders.Add(PayloadType.DeviceSetting, [0x01, 0x10]);
        _ipHeaders.Add(PayloadType.ControlCommand, [0x02, 0x00]);
        _ipHeaders.Add(PayloadType.ControlReply, [0x02, 0x01]);
        // Local rather than a field (S1450): the worker's running task keeps it alive.
        var pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime));
        pollWorker.Restart();
    }

    private Task Poll(CancellationToken token)
    {
        using (PushProperties("Poll"))
        {
            if (CommunicationClient.ConnectionState != ConnectionState.Connected)
                return Task.CompletedTask;

            SendInquiry([_header, 0x09, 0x04, 0x00, CommandFooter]);
        }
        return Task.CompletedTask;
    }

    private void SendInquiry(byte[] bytes)
    {
        try
        {
            if (_useIpHeaders)
            {
                CommunicationClient.Send(PayloadWithIpHeader(PayloadType.ViscaInquiry, bytes));
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

    protected void SendCommand(byte[] bytes, string? description = null, Action? onCompleted = null)
    {
        try
        {
            CommunicationState = CommunicationState.Okay;
            var pending = description == null ? null : new PendingCommand(description, onCompleted);
            if (_useIpHeaders)
            {
                if (pending == null)
                    _pendingCommands.Remove(_sequenceNumber);
                else
                    _pendingCommands[_sequenceNumber] = pending;
                CommunicationClient.Send(PayloadWithIpHeader(PayloadType.ViscaCommand, bytes));
                return;
            }
            _lastCommand = pending;
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
    }

    public override void PowerOn()
    {
        SendCommand([_header, 0x01, 0x04, 0x00, 0x02, CommandFooter]);
        DesiredPowerState = PowerState.On;
    }
    
    protected override void DoZoomStop()
    {
        using (PushProperties("DoZoomStop"))
        {
            SendCommand([_header, 0x01, 0x04, 0x07, 0x00, CommandFooter]);
            LogVerbose("Zoom Stop");
        }
    }

    public override void ZoomIn()
    {
        using (PushProperties("ZoomIn"))
        {
            SendCommand([_header, 0x01, 0x04, 0x07, _zoomInSpeed, CommandFooter]);
            LogVerbose("Zooming In");
        }
    }

    public override void ZoomOut()
    {
        using (PushProperties("ZoomOut"))
        {
            SendCommand([_header, 0x01, 0x04, 0x07, _zoomOutSpeed, CommandFooter]);
            LogVerbose("Zooming Out");
        }
    }

    protected override void DoPanTiltStop()
    {
        using (PushProperties("DoPanTiltStop"))
        {
            SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x03, CommandFooter]);
            LogVerbose("PTZ Stop");
        }
    }

    public override void PanTiltUp()
    {
        using (PushProperties("PanTiltUp"))
        {
            SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x01, CommandFooter]);
            LogVerbose("PTZ Up");
        }
    }

    public override void PanTiltDown()
    {
        using (PushProperties("PanTiltDown"))
        {
            SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x03, 0x02, CommandFooter]);
            LogVerbose("PTZ Down");
        }
    }

    public override void PanTiltLeft()
    {
        using (PushProperties("PanTiltLeft"))
        {
            SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x01, 0x03, CommandFooter]);
            LogVerbose("PTZ Left");
        }
    }

    public override void PanTiltRight()
    {
        using (PushProperties("PanTiltRight"))
        {
            SendCommand([_header, 0x01, 0x06, 0x01, _panSpeed, _tiltSpeed, 0x02, 0x03, CommandFooter]);
            LogVerbose("PTZ Right");
        }
    }

    public override void RecallPreset(int presetNumber)
    {
        DoRecallPreset(presetNumber);
        if (!_deviceSendsResponses)
            LastRecalledPreset = presetNumber;
    }

    public override void DoRecallPreset(int presetNumber)
    {
        var presetName = PresetNames.TryGetValue(presetNumber, out var name) ? name : presetNumber.ToString();
        void Confirm()
        {
            LastRecalledPreset = presetNumber;
            AddEvent(EventType.Preset, $"Preset {presetName} recalled");
        }
        SendCommand([_header, 0x01, 0x04, 0x3f, 0x02, (byte)presetNumber, CommandFooter], $"recall preset {presetName}",
            _deviceSendsResponses ? Confirm : null);
        if (!_deviceSendsResponses)
            AddEvent(EventType.Preset, $"Preset {presetName} recalled");
    }

    public override void SavePreset(int presetNumber)
    {
        var presetName = PresetNames.TryGetValue(presetNumber, out var name) ? name : presetNumber.ToString();
        SendCommand([_header, 0x01, 0x04, 0x3f, 0x01, (byte)presetNumber, CommandFooter], $"save preset {presetName}");
        AddEvent(EventType.Preset, $"Preset {presetName} saved");
    }

    private void HandleResponse(byte[] response)
    {
        using (PushProperties("HandleResponse"))
        {
            var index = 0;
            while (index < response.Length)
            {
                if (_useIpHeaders)
                {
                    if (response.Length - index < IpHeaderLength)
                    {
                        ReportMalformedResponse(response);
                        return;
                    }
                    var payloadLength = response[index + 2] << 8 | response[index + 3];
                    if (response.Length - index - IpHeaderLength < payloadLength)
                    {
                        ReportMalformedResponse(response);
                        return;
                    }
                    ProcessViscaPayload(response[(index + IpHeaderLength)..(index + IpHeaderLength + payloadLength)],
                        response[index + 7]);
                    index += IpHeaderLength + payloadLength;
                }
                else
                {
                    var footerIndex = Array.IndexOf(response, CommandFooter, index);
                    if (footerIndex < 0)
                    {
                        ReportMalformedResponse(response);
                        return;
                    }
                    ProcessViscaPayload(response[index..(footerIndex + 1)]);
                    index = footerIndex + 1;
                }
            }
        }
    }

    private void ProcessViscaPayload(byte[] payload, byte? sequenceNumber = null)
    {
        if (payload.Length < 3 || payload[^1] != CommandFooter)
        {
            ReportMalformedResponse(payload);
            return;
        }

        switch (payload[1] & 0xF0)
        {
            case 0x40:
                CommunicationState = CommunicationState.Okay;
                InvokePendingCallback(sequenceNumber);
                LogVerbose("Command acknowledged");
                break;
            case 0x50:
                CommunicationState = CommunicationState.Okay;
                if (payload.Length == 3)
                {
                    ConsumePendingCommand(sequenceNumber)?.OnCompleted?.Invoke();
                    LogVerbose("Command complete");
                    return;
                }
                // The power inquiry is the only inquiry this driver sends
                PowerState = payload[2] switch
                {
                    0x02 => PowerState.On,
                    0x03 => PowerState.Off,
                    _ => PowerState
                };
                ProcessPowerState();
                break;
            case 0x60:
                var errorMessage = ErrorMessages.TryGetValue(payload[2], out var message)
                    ? message
                    : $"Unknown error 0x{payload[2]:X2}";
                var failedCommand = ConsumePendingCommand(sequenceNumber);
                if (failedCommand != null)
                    errorMessage = $"Unable to {failedCommand.Description}: {errorMessage}";
                LogError("The camera reported an error: {error} ({response})", errorMessage, BitConverter.ToString(payload));
                AddEvent(EventType.Error, errorMessage);
                CommunicationState = CommunicationState.Error;
                break;
            default:
                LogWarning("Unhandled response {response}", BitConverter.ToString(payload));
                break;
        }
    }

    private void InvokePendingCallback(byte? sequenceNumber)
    {
        if (sequenceNumber is { } sequence)
        {
            if (!_pendingCommands.TryGetValue(sequence, out var pending) || pending.OnCompleted == null)
                return;
            _pendingCommands[sequence] = pending with { OnCompleted = null };
            pending.OnCompleted.Invoke();
            return;
        }
        if (_lastCommand?.OnCompleted is not { } onCompleted)
            return;
        _lastCommand = _lastCommand with { OnCompleted = null };
        onCompleted.Invoke();
    }

    private PendingCommand? ConsumePendingCommand(byte? sequenceNumber)
    {
        if (sequenceNumber is { } sequence)
            return _pendingCommands.Remove(sequence, out var pending) ? pending : null;
        var last = _lastCommand;
        _lastCommand = null;
        return last;
    }

    private void ReportMalformedResponse(byte[] response)
    {
        CommunicationState = CommunicationState.Error;
        LogWarning("The response was malformed: {response}", BitConverter.ToString(response));
        AddEvent(EventType.Error, "The response was malformed");
    }

    public override void SetAutoFocus(PowerState state)
    {
        SendCommand([_header, 0x01, 0x04, 0x38, (byte)(state == PowerState.On? 0x02 : 0x03), CommandFooter]);
        AddEvent(EventType.Other, $"Auto Focus: {state}");
    }
}