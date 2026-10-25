using AVCoders.Core;

namespace AVCoders.Camera;

public class SonyVisca : CameraBase, IDisposable
{
    private enum PayloadType
    {
        ViscaCommand, ViscaInquiry
    }
    private readonly bool _useIpHeaders;
    private byte _panSpeed;
    private byte _tiltSpeed;
    private byte _zoomInSpeed;
    private byte _zoomOutSpeed;
    private byte _sequenceNumber;
    private byte _cameraId;
    protected byte _header;
    protected static readonly byte CommandFooter = 0xFF;
    private const int IpHeaderLength = 8;
    private const int IpLengthHighOffset = 2;
    private const int IpLengthLowOffset = 3;
    private const int IpSequenceOffset = 7;
    private const int MaxReplyLength = 16;
    private const byte SocketZeroError = 0x60;
    private const byte HeaderBase = 0x80;
    private const byte MinCameraId = 1;
    private const byte MaxCameraId = 7;
    private const byte ZoomInSpeedBase = 0x20;
    private const byte ZoomOutSpeedBase = 0x30;
    private const byte MaxZoomSpeed = 7;
    private const int MaxPresetNumber = 0xFE;
    private const string CameraIdIssueKey = "camera-id";
    private static readonly Dictionary<PayloadType, byte[]> IpHeaders = new()
    {
        { PayloadType.ViscaCommand, [0x01, 0x00] },
        { PayloadType.ViscaInquiry, [0x01, 0x10] }
    };
    private static readonly byte[][] ReplyHeaders = [[0x01, 0x11], [0x02, 0x01]];
    private readonly ThreadWorker _pollWorker;
    private bool _disposed;
    private record PendingCommand(string Description, Action? OnCompleted, string? TriggerId);
    private record PendingInquiry(string Description, Action<byte[]> OnReply, string? TriggerId);

    private readonly object _pendingLock = new();
    private readonly Dictionary<byte, PendingCommand> _pendingCommands = new Dictionary<byte, PendingCommand>();
    private PendingCommand? _lastCommand;
    private readonly Queue<PendingInquiry> _pendingInquiries = new();
    private readonly Dictionary<byte, PendingInquiry> _pendingInquiriesBySequence = new();
    private byte[] _partialReply = [];

    private static readonly Dictionary<byte, string> ErrorMessages = new Dictionary<byte, string>
    {
        { 0x01, "Message length error" },
        { 0x02, "Syntax error" },
        { 0x03, "Command buffer full" },
        { 0x04, "Command cancelled" },
        { 0x05, "No socket" },
        { 0x41, "Command not executable" }
    };

    public SonyVisca(CommunicationClient client, bool useIpHeaders, string name, Dictionary<int, string> presetNames, byte cameraId = 0x01, int pollTime = 30)
        : base(name, client, presetNames)
    {
        _useIpHeaders = useIpHeaders;
        SetCameraId(cameraId);
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        _panSpeed = 0x04;
        _tiltSpeed = 0x04;
        _zoomInSpeed = 0x23;
        _zoomOutSpeed = 0x33;
        CommunicationState = CommunicationState.NotAttempted;
        _sequenceNumber = 0x00;
        // waitFirst: the first inquiry must not beat an object initialiser that turns responses off.
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime), waitFirst: true);
        _ = _pollWorker.Restart();
    }

    protected override void OnDeviceSendsResponsesChanged()
    {
        lock (_pendingLock)
        {
            // A stale entry must not be matched against a later command that reuses its sequence number.
            _pendingCommands.Clear();
            _lastCommand = null;
            _pendingInquiries.Clear();
            _pendingInquiriesBySequence.Clear();
            _partialReply = [];
        }
        if (DeviceSendsResponses)
            return;
        // Nothing will report the real state again, so adopt the desired state and clear any
        // power issue raised by an earlier inquiry reply.
        if (DesiredPowerState != PowerState.Unknown)
            PowerState = DesiredPowerState;
        ProcessPowerState();
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        lock (_pendingLock)
            _partialReply = [];
    }

    private Task Poll(CancellationToken token)
    {
        if (!DeviceSendsResponses || CommunicationClient.ConnectionState != ConnectionState.Connected)
            return Task.CompletedTask;

        using (PushProperties("Poll"))
        {
            lock (_pendingLock)
            {
                _pendingInquiries.Clear();
                _pendingInquiriesBySequence.Clear();
            }
            SendPollInquiries();
        }
        return Task.CompletedTask;
    }

    protected virtual void SendPollInquiries()
    {
        SendInquiry([_header, 0x09, 0x04, 0x00, CommandFooter], "query the power state", HandlePowerReply);
    }

    private void HandlePowerReply(byte[] payload)
    {
        PowerState = payload[2] switch
        {
            0x02 => PowerState.On,
            0x03 => PowerState.Off,
            _ => PowerState
        };
        ProcessPowerState();
    }

    protected void SendInquiry(byte[] bytes, string description, Action<byte[]> onReply)
    {
        if (!CameraIdIsValid)
        {
            LogWarning("Not sending {inquiry}, camera id {cameraId} is out of range", BitConverter.ToString(bytes), _cameraId);
            return;
        }
        var pending = new PendingInquiry(description, onReply, Trigger.Id);
        try
        {
            if (_useIpHeaders)
            {
                byte sequenceNumber;
                lock (_pendingLock)
                {
                    sequenceNumber = _sequenceNumber++;
                    _pendingCommands.Remove(sequenceNumber);
                    _pendingInquiriesBySequence[sequenceNumber] = pending;
                }
                CommunicationClient.Send(PayloadWithIpHeader(PayloadType.ViscaInquiry, sequenceNumber, bytes));
                return;
            }
            lock (_pendingLock)
                _pendingInquiries.Enqueue(pending);
            CommunicationClient.Send(bytes);
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    /// <returns>Whether the command was handed to the transport.</returns>
    protected bool SendCommand(byte[] bytes, string? description = null, Action? onCompleted = null)
    {
        if (!CameraIdIsValid)
        {
            LogWarning("Not sending {command}, camera id {cameraId} is out of range", BitConverter.ToString(bytes), _cameraId);
            return false;
        }
        try
        {
            var pending = description == null ? null : new PendingCommand(description, onCompleted, Trigger.Id);
            if (_useIpHeaders)
            {
                byte sequenceNumber;
                lock (_pendingLock)
                {
                    sequenceNumber = _sequenceNumber++;
                    _pendingInquiriesBySequence.Remove(sequenceNumber);
                    if (pending == null)
                        _pendingCommands.Remove(sequenceNumber);
                    else
                        _pendingCommands[sequenceNumber] = pending;
                }
                CommunicationClient.Send(PayloadWithIpHeader(PayloadType.ViscaCommand, sequenceNumber, bytes));
            }
            else
            {
                lock (_pendingLock)
                    _lastCommand = pending;
                CommunicationClient.Send(bytes);
            }
            if (!DeviceSendsResponses)
                CommunicationState = CommunicationState.Okay;
            return true;
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
            return false;
        }
    }

    private static byte[] PayloadWithIpHeader(PayloadType payloadType, byte sequenceNumber, byte[] payload) =>
    [
        ..IpHeaders[payloadType], (byte)(payload.Length >> 8), (byte)payload.Length, 0xFF, 0xFF, 0xFF, sequenceNumber,
        ..payload
    ];

    private bool CameraIdIsValid => _cameraId is >= MinCameraId and <= MaxCameraId;

    public void SetCameraId(byte cameraId)
    {
        _cameraId = cameraId;
        if (!CameraIdIsValid)
        {
            RaiseOngoingIssue(CameraIdIssueKey,
                $"Camera id {cameraId} is out of range, it must be between {MinCameraId} and {MaxCameraId}");
            return;
        }
        ResolveIssue(CameraIdIssueKey);
        _header = (byte)(HeaderBase + cameraId);
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
        if (speed > MaxZoomSpeed)
        {
            using (PushProperties("SetZoomSpeed"))
            {
                LogWarning("Zoom speed {speed} is out of range, it must be between 0 and {maxZoomSpeed}", speed, MaxZoomSpeed);
                AddEvent(EventType.Error, $"Zoom speed {speed} is out of range, it must be between 0 and {MaxZoomSpeed}");
            }
            return;
        }
        _zoomInSpeed = (byte)(ZoomInSpeedBase + speed);
        _zoomOutSpeed = (byte)(ZoomOutSpeedBase + speed);
    }

    public override void PowerOff() => SetPower(PowerState.Off, 0x03);

    public override void PowerOn() => SetPower(PowerState.On, 0x02);

    private void SetPower(PowerState state, byte code)
    {
        var sent = SendCommand([_header, 0x01, 0x04, 0x00, code, CommandFooter]);
        DesiredPowerState = state;
        if (DeviceSendsResponses || !sent)
            return;
        PowerState = state;
        ProcessPowerState(); // Both states agree, so this only resolves any open power issue.
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

    public override void RecallPreset(int presetNumber) => DoRecallPreset(presetNumber);

    public override void DoRecallPreset(int presetNumber)
    {
        using (PushProperties("DoRecallPreset"))
        {
            if (!IsValidPreset(presetNumber))
                return;
            var presetName = PresetNames.TryGetValue(presetNumber, out var name) ? name : presetNumber.ToString();
            void Confirm()
            {
                LastRecalledPreset = presetNumber;
                AddEvent(EventType.Preset, $"Preset {presetName} recalled");
            }
            var sendsResponses = DeviceSendsResponses;
            var sent = SendCommand([_header, 0x01, 0x04, 0x3f, 0x02, (byte)presetNumber, CommandFooter],
                $"recall preset {presetName}", sendsResponses ? Confirm : null);
            if (!sendsResponses && sent)
                Confirm();
        }
    }

    public override void SavePreset(int presetNumber)
    {
        using (PushProperties("SavePreset"))
        {
            if (!IsValidPreset(presetNumber))
                return;
            var presetName = PresetNames.TryGetValue(presetNumber, out var name) ? name : presetNumber.ToString();
            SendCommand([_header, 0x01, 0x04, 0x3f, 0x01, (byte)presetNumber, CommandFooter], $"save preset {presetName}");
            AddEvent(EventType.Preset, $"Preset {presetName} saved");
        }
    }

    private bool IsValidPreset(int presetNumber)
    {
        if (presetNumber is >= 0 and <= MaxPresetNumber)
            return true;
        LogWarning("Preset {presetNumber} is out of range, it must be between 0 and {maxPresetNumber}", presetNumber, MaxPresetNumber);
        AddEvent(EventType.Error, $"Preset {presetNumber} is out of range, it must be between 0 and {MaxPresetNumber}");
        return false;
    }

    private void HandleResponse(byte[] response)
    {
        // State is assumed on send; a reply here is either late or from a camera the integrator chose to treat as silent.
        if (!DeviceSendsResponses)
            return;
        using (PushProperties("HandleResponse"))
        {
            if (_useIpHeaders)
                HandleIpResponse(response);
            else
                HandleRawResponse(response);
        }
    }

    private void HandleIpResponse(byte[] response)
    {
        List<(byte[] Payload, byte SequenceNumber)> frames = [];
        byte[] dropped = [];
        byte[] partial;
        bool malformed;
        lock (_pendingLock)
        {
            if (_partialReply.Length > 0 && StartsWithReplyHeader(response))
            {
                dropped = _partialReply;
                _partialReply = [];
            }
            byte[] buffered = [.._partialReply, ..response];
            var index = 0;
            while (buffered.Length - index >= IpHeaderLength)
            {
                var payloadLength = IpPayloadLength(buffered, index);
                if (payloadLength > MaxReplyLength || buffered.Length - index - IpHeaderLength < payloadLength)
                    break;
                var payloadStart = index + IpHeaderLength;
                frames.Add((buffered[payloadStart..(payloadStart + payloadLength)], buffered[index + IpSequenceOffset]));
                index = payloadStart + payloadLength;
            }
            partial = buffered[index..];
            malformed = partial.Length >= IpHeaderLength && IpPayloadLength(partial, 0) > MaxReplyLength;
            _partialReply = malformed ? [] : partial;
        }
        if (dropped.Length > 0)
            ReportMalformedResponse(dropped);
        foreach (var (payload, sequenceNumber) in frames)
            ProcessViscaPayload(payload, sequenceNumber);
        if (malformed)
            ReportMalformedResponse(partial);
    }

    private static bool StartsWithReplyHeader(byte[] response) =>
        ReplyHeaders.Any(header => response.AsSpan().StartsWith(header));

    private static int IpPayloadLength(byte[] message, int index) =>
        message[index + IpLengthHighOffset] << 8 | message[index + IpLengthLowOffset];

    private void HandleRawResponse(byte[] response)
    {
        byte[] complete;
        byte[] partial;
        lock (_pendingLock)
        {
            byte[] buffered = [.._partialReply, ..response];
            var completeLength = Array.LastIndexOf(buffered, CommandFooter) + 1;
            complete = buffered[..completeLength];
            partial = buffered[completeLength..];
            _partialReply = partial.Length > MaxReplyLength ? [] : partial;
        }
        var index = 0;
        while (index < complete.Length)
        {
            var footerIndex = Array.IndexOf(complete, CommandFooter, index);
            ProcessViscaPayload(complete[index..(footerIndex + 1)]);
            index = footerIndex + 1;
        }
        if (partial.Length > MaxReplyLength)
            ReportMalformedResponse(partial);
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
                    CompletePendingCommand(sequenceNumber);
                    LogVerbose("Command complete");
                    return;
                }
                AnswerPendingInquiry(payload, sequenceNumber);
                break;
            case 0x60:
                var errorMessage = ErrorMessages.TryGetValue(payload[2], out var message)
                    ? message
                    : $"Unknown error 0x{payload[2]:X2}";
                var failed = ConsumeFailedRequest(payload[1], sequenceNumber);
                if (failed != null)
                    errorMessage = $"Unable to {failed.Value.Description}: {errorMessage}";
                using (Trigger.Resume(failed?.TriggerId))
                using (PushProperties("ProcessViscaPayload"))
                {
                    LogError("The camera reported an error: {error} ({response})", errorMessage, BitConverter.ToString(payload));
                    AddEvent(EventType.Error, errorMessage);
                    CommunicationState = CommunicationState.Error;
                }
                break;
            default:
                LogWarning("Unhandled response {response}", BitConverter.ToString(payload));
                break;
        }
    }

    // The callbacks run under the lock so that turning responses off, which takes the same lock,
    // either happens before the lookup (nothing found) or after the callback has finished.
    private void InvokePendingCallback(byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (!DeviceSendsResponses)
                return;
            if (sequenceNumber is { } sequence)
            {
                if (!_pendingCommands.TryGetValue(sequence, out var pending) || pending.OnCompleted == null)
                    return;
                _pendingCommands[sequence] = pending with { OnCompleted = null };
                using (Trigger.Resume(pending.TriggerId))
                using (PushProperties("InvokePendingCallback"))
                    pending.OnCompleted.Invoke();
                return;
            }
            if (_lastCommand?.OnCompleted is not { } onCompleted)
                return;
            _lastCommand = _lastCommand with { OnCompleted = null };
            using (Trigger.Resume(_lastCommand.TriggerId))
            using (PushProperties("InvokePendingCallback"))
                onCompleted.Invoke();
        }
    }

    private void CompletePendingCommand(byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (!DeviceSendsResponses)
                return;
            if (ConsumePendingCommand(sequenceNumber) is not { } pending)
                return;
            using (Trigger.Resume(pending.TriggerId))
            using (PushProperties("CompletePendingCommand"))
                pending.OnCompleted?.Invoke();
        }
    }

    private PendingCommand? ConsumePendingCommand(byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (sequenceNumber is { } sequence)
                return _pendingCommands.Remove(sequence, out var pending) ? pending : null;
            var last = _lastCommand;
            _lastCommand = null;
            return last;
        }
    }

    private void AnswerPendingInquiry(byte[] payload, byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (!DeviceSendsResponses)
                return;
            if (ConsumePendingInquiry(sequenceNumber) is not { } inquiry)
            {
                LogWarning("Unexpected inquiry reply {response}", BitConverter.ToString(payload));
                return;
            }
            using (Trigger.Resume(inquiry.TriggerId))
            using (PushProperties("AnswerPendingInquiry"))
                inquiry.OnReply(payload);
        }
    }

    private PendingInquiry? ConsumePendingInquiry(byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (sequenceNumber is { } sequence)
                return _pendingInquiriesBySequence.Remove(sequence, out var pending) ? pending : null;
            return _pendingInquiries.TryDequeue(out var oldest) ? oldest : null;
        }
    }

    private (string Description, string? TriggerId)? ConsumeFailedRequest(byte errorReply, byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (sequenceNumber != null)
            {
                if (ConsumePendingCommand(sequenceNumber) is { } command)
                    return (command.Description, command.TriggerId);
                if (ConsumePendingInquiry(sequenceNumber) is { } inquiry)
                    return (inquiry.Description, inquiry.TriggerId);
                return null;
            }
            if (errorReply == SocketZeroError && ConsumePendingInquiry(null) is { } oldestInquiry)
                return (oldestInquiry.Description, oldestInquiry.TriggerId);
            if (ConsumePendingCommand(null) is { } lastCommand)
                return (lastCommand.Description, lastCommand.TriggerId);
            return null;
        }
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CommunicationClient.ResponseByteHandlers -= HandleResponse;
        CommunicationClient.ConnectionStateHandlers -= HandleConnectionState;
        CommunicationClient.ConnectionStateHandlers -= HandleRequestedDisconnect;
        Task.Run(_pollWorker.Stop).GetAwaiter().GetResult();
        LogBaseRegistry.Deregister(_pollWorker);
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }
}