using AVCoders.Core;

namespace AVCoders.Camera;

public class SonyVisca : CameraBase, IDisposable
{
    private enum PayloadType
    {
        ViscaCommand, ViscaInquiry
    }
    private enum InquiryState
    {
        Owed, Asked, Done
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
    private const byte SocketMask = 0x0F;
    private const int PowerReplyLength = 4;
    private const int MaxSilentPolls = 3;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromMinutes(5);
    private const byte HeaderBase = 0x80;
    private const byte MinCameraId = 1;
    private const byte MaxCameraId = 7;
    private const byte ZoomInSpeedBase = 0x20;
    private const byte ZoomOutSpeedBase = 0x30;
    private const byte MaxZoomSpeed = 7;
    private const int MaxPresetNumber = 0xFE;
    private const string CameraIdIssueKey = "camera-id";
    private const string VersionIssueKey = "version-read";
    private const string PanTiltStatusIssueKey = "pan-tilt-status-read";
    private const string PanMechanismIssueKey = "pan-mechanism";
    private const string TiltMechanismIssueKey = "tilt-mechanism";
    private const string PanPositionIssueKey = "pan-position";
    private const string TiltPositionIssueKey = "tilt-position";
    private const string PanTiltInitialisationIssueKey = "pan-tilt-initialisation";
    private const string PanTiltOperationIssueKey = "pan-tilt-operation";
    private const string VendorIdLabel = "Vendor ID";
    private const string ModelIdLabel = "Model ID";
    private const string RomRevisionLabel = "ROM Revision";
    private const string PanTiltStatusLabel = "Pan/Tilt Status";
    private const int ErrorCodeIndex = 2;
    private const byte SyntaxError = 0x02;
    private const int MaxMissedReplies = 3;
    private const string NoReply = "no reply";
    private const int VersionReplyLength = 10;
    private const int VendorIdOffset = 2;
    private const int ModelIdOffset = 4;
    private const int RomRevisionOffset = 6;
    private const int PanTiltStatusReplyLength = 5;
    private const int InitialisationAndTiltByte = 2;
    private const int PanByte = 3;
    private const int HighNibbleShift = 4;
    private const int OperationShift = 2;
    private const byte TwoBitMask = 0x03;
    private const byte InitialisationFailed = 0x03;
    private const byte OperationFailed = 0x03;
    private const byte PositionNotDetected = 0x01;
    private const byte MechanismDefective = 0x02;
    private static readonly Dictionary<PayloadType, byte[]> IpHeaders = new()
    {
        { PayloadType.ViscaCommand, [0x01, 0x00] },
        { PayloadType.ViscaInquiry, [0x01, 0x10] }
    };
    private static readonly byte[][] ReplyHeaders = [[0x01, 0x11], [0x02, 0x01]];
    private readonly ThreadWorker _pollWorker;
    private bool _disposed;
    private abstract record PendingRequest(byte[] Bytes, string? Description, string? TriggerId);
    private sealed record PendingCommand(byte[] Bytes, string? Description, Action? OnCompleted, string? TriggerId)
        : PendingRequest(Bytes, Description, TriggerId);
    private sealed record PendingInquiry(byte[] Bytes, string Description, int ReplyLength, Action<byte[]> OnReply,
        Action<byte[]>? OnError, string? TriggerId) : PendingRequest(Bytes, Description, TriggerId);

    private readonly object _pendingLock = new();
    private readonly Dictionary<byte, PendingCommand> _pendingCommands = new Dictionary<byte, PendingCommand>();
    private readonly Dictionary<byte, PendingInquiry> _pendingInquiriesBySequence = new();
    private readonly SentCommandTracker<PendingRequest> _sent;
    private readonly SentCommandTracker<(byte Socket, PendingCommand Command)> _executing;
    private byte[] _partialReply = [];
    private InquiryState _versionInquiry = InquiryState.Owed;
    private InquiryState _panTiltStatusInquiry = InquiryState.Owed;
    private readonly Dictionary<string, int> _missedReplies = new();
    private readonly List<byte[]> _polledInquiries = [];
    private bool _replySincePoll;
    private int _silentPolls;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<PendingInquiry> _queuedInquiries = new();
    private PendingInquiry? _inquiryInFlight;
    private ITimer? _inquiryExpiry;

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
        : this(client, useIpHeaders, name, presetNames, cameraId, pollTime, null)
    {
    }

    internal SonyVisca(CommunicationClient client, bool useIpHeaders, string name, Dictionary<int, string> presetNames, byte cameraId, int pollTime, TimeProvider? timeProvider)
        : base(name, client, presetNames)
    {
        _useIpHeaders = useIpHeaders;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sent = new SentCommandTracker<PendingRequest>(ReplyTimeout, timeProvider);
        _executing = new SentCommandTracker<(byte Socket, PendingCommand Command)>(CompletionTimeout, timeProvider);
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
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime), waitFirst: true, timeProvider);
        _ = _pollWorker.Restart();
    }

    protected override void OnDeviceSendsResponsesChanged()
    {
        lock (_pendingLock)
        {
            // A stale entry must not be matched against a later command that reuses its sequence number.
            _pendingCommands.Clear();
            _pendingInquiriesBySequence.Clear();
            _sent.Clear();
            _executing.Clear();
            _partialReply = [];
            _versionInquiry = InquiryState.Owed;
            _panTiltStatusInquiry = InquiryState.Owed;
            _missedReplies.Clear();
            ForgetPolls();
            ForgetQueuedInquiries();
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
        {
            _partialReply = [];
            _sent.Clear();
            _executing.Clear();
            ForgetPolls();
            ForgetQueuedInquiries();
            if (connectionState != ConnectionState.Connected)
                return;
            _versionInquiry = InquiryState.Owed;
            _panTiltStatusInquiry = InquiryState.Owed;
            _missedReplies.Clear();
        }
    }

    private void ForgetPolls()
    {
        _polledInquiries.Clear();
        _replySincePoll = false;
        _silentPolls = 0;
    }

    private void ForgetQueuedInquiries()
    {
        _queuedInquiries.Clear();
        _inquiryInFlight = null;
        _inquiryExpiry?.Dispose();
    }

    private Task Poll(CancellationToken token)
    {
        QueryStatus();
        return Task.CompletedTask;
    }

    public void QueryStatus()
    {
        if (!DeviceSendsResponses || CommunicationClient.ConnectionState != ConnectionState.Connected)
            return;

        using (PushProperties("QueryStatus"))
        {
            lock (_pendingLock)
            {
                _pendingInquiriesBySequence.Clear();
                _queuedInquiries.Clear();
            }
            ForgetMissedReplies();
            CountSilentPoll();
            SendPollInquiries();
            AskForThePanTiltStatus();
            AskForTheVersion();
        }
    }

    private void ForgetMissedReplies()
    {
        foreach (var missed in _sent.TakeMissed())
            using (Trigger.Resume(missed.Command.TriggerId))
                LogDebug("No reply to {Request}", BitConverter.ToString(missed.Command.Bytes));
        foreach (var missed in _executing.TakeMissed())
            using (Trigger.Resume(missed.Command.Command.TriggerId))
                LogDebug("No completion for {Command}", BitConverter.ToString(missed.Command.Command.Bytes));
    }

    private void CountSilentPoll()
    {
        string unanswered;
        int silentPolls;
        lock (_pendingLock)
        {
            var silent = !_replySincePoll && _polledInquiries.Count > 0;
            unanswered = string.Join(", ", _polledInquiries.Select(inquiry => BitConverter.ToString(inquiry)));
            _polledInquiries.Clear();
            _replySincePoll = false;
            if (!silent)
                return;
            silentPolls = ++_silentPolls;
        }
        if (silentPolls < MaxSilentPolls)
        {
            LogInformation("The camera gave no reply to {Inquiries} ({SilentPolls} of {MaxSilentPolls})",
                unanswered, silentPolls, MaxSilentPolls);
            return;
        }
        if (silentPolls > MaxSilentPolls)
            return;
        LogWarning("The camera gave no reply to {MaxSilentPolls} polls in a row, the last was {Inquiries}", MaxSilentPolls, unanswered);
        AddEvent(EventType.Error, $"No reply to {unanswered}");
        CommunicationState = CommunicationState.Error;
    }

    private void ReplyReceived()
    {
        lock (_pendingLock)
        {
            _replySincePoll = true;
            _silentPolls = 0;
        }
    }

    protected virtual void SendPollInquiries()
    {
        SendInquiry([_header, 0x09, 0x04, 0x00, CommandFooter], PowerReplyLength, "query the power state", HandlePowerReply);
    }

    private void AskForTheVersion()
    {
        if (_versionInquiry == InquiryState.Done)
            return;
        if (_versionInquiry == InquiryState.Asked)
            MissedReply("version", VersionIssueKey, NoReply);
        _versionInquiry = InquiryState.Asked;
        SendInquiry([_header, 0x09, 0x00, 0x02, CommandFooter], VersionReplyLength, "query the version", HandleVersionReply, HandleVersionError);
    }

    private void HandleVersionReply(byte[] payload)
    {
        if (payload.Length != VersionReplyLength)
        {
            _versionInquiry = InquiryState.Owed;
            MissedReply("version", VersionIssueKey, $"{BitConverter.ToString(payload)}, which is not a version reply");
            return;
        }
        _versionInquiry = InquiryState.Done;
        SetDetail(VendorIdLabel, TwoBytesHex(payload, VendorIdOffset));
        SetDetail(ModelIdLabel, TwoBytesHex(payload, ModelIdOffset));
        SetDetail(RomRevisionLabel, TwoBytesHex(payload, RomRevisionOffset));
        ReadAnswered(VersionIssueKey);
    }

    private void HandleVersionError(byte[] payload)
    {
        if (IsRejection(payload))
        {
            _versionInquiry = InquiryState.Done;
            ShowNotSupported("version", VersionIssueKey, payload, VendorIdLabel, ModelIdLabel, RomRevisionLabel);
            return;
        }
        _versionInquiry = InquiryState.Owed;
        MissedReply("version", VersionIssueKey, $"{BitConverter.ToString(payload)} ({ErrorText(payload)})");
    }

    private static bool IsRejection(byte[] payload) => payload[ErrorCodeIndex] == SyntaxError;

    private void MissedReply(string inquiry, string issueKey, string reply)
    {
        int missed;
        lock (_pendingLock)
        {
            missed = _missedReplies.GetValueOrDefault(issueKey) + 1;
            _missedReplies[issueKey] = missed;
        }
        if (missed < MaxMissedReplies)
        {
            LogInformation("The camera gave {Reply} to the {Inquiry} inquiry, asking again on the next poll ({Missed} of {MaxMissedReplies})",
                reply, inquiry, missed, MaxMissedReplies);
            return;
        }
        if (CommunicationState == CommunicationState.Error)
        {
            LogDebug("The camera gave {Reply} to the {Inquiry} inquiry, not raising the read issue while communication is in error",
                reply, inquiry);
            return;
        }
        RaiseOngoingIssue(issueKey,
            $"The camera did not answer the {inquiry} inquiry {MaxMissedReplies} times in a row, the last was {reply}", IssueSeverity.Minor);
    }

    private void ReadAnswered(string issueKey)
    {
        lock (_pendingLock)
            _missedReplies.Remove(issueKey);
        ResolveIssue(issueKey);
    }

    private void ShowNotSupported(string inquiry, string issueKey, byte[] payload, params string[] labels)
    {
        string raw = BitConverter.ToString(payload);
        LogInformation("The camera does not support the {Inquiry} inquiry: {Response} ({Error})", inquiry, raw, ErrorText(payload));
        foreach (var label in labels)
            SetDetail(label, $"Not supported: {raw}", DetailTone.Normal);
        ReadAnswered(issueKey);
    }

    private static string TwoBytesHex(byte[] payload, int offset) => $"{payload[offset]:X2}{payload[offset + 1]:X2}";

    private void AskForThePanTiltStatus()
    {
        if (PowerState != PowerState.On || _panTiltStatusInquiry == InquiryState.Done)
            return;
        if (_panTiltStatusInquiry == InquiryState.Asked)
            MissedReply("pan/tilt status", PanTiltStatusIssueKey, NoReply);
        _panTiltStatusInquiry = InquiryState.Asked;
        SendInquiry([_header, 0x09, 0x06, 0x10, CommandFooter], PanTiltStatusReplyLength, "query the pan/tilt status",
            HandlePanTiltStatusReply, HandlePanTiltStatusError);
    }

    private void HandlePanTiltStatusReply(byte[] payload)
    {
        _panTiltStatusInquiry = InquiryState.Owed;
        if (payload.Length != PanTiltStatusReplyLength)
        {
            MissedReply("pan/tilt status", PanTiltStatusIssueKey, $"{BitConverter.ToString(payload)}, which is not a status reply");
            return;
        }
        ReadAnswered(PanTiltStatusIssueKey);
        var initialisationAndTilt = payload[InitialisationAndTiltByte];
        var pan = payload[PanByte] >> HighNibbleShift;
        (string Key, bool Active, string Message)[] faults =
        [
            (PanTiltInitialisationIssueKey, (initialisationAndTilt >> HighNibbleShift & TwoBitMask) == InitialisationFailed,
                "The pan/tilt initialisation failed"),
            (PanTiltOperationIssueKey, (initialisationAndTilt >> OperationShift & TwoBitMask) == OperationFailed,
                "A pan/tilt operation failed"),
            (TiltMechanismIssueKey, (initialisationAndTilt & MechanismDefective) != 0, "The tilt mechanism is defective"),
            (TiltPositionIssueKey, (initialisationAndTilt & PositionNotDetected) != 0, "The tilt position cannot be detected"),
            (PanMechanismIssueKey, (pan & MechanismDefective) != 0, "The pan mechanism is defective"),
            (PanPositionIssueKey, (pan & PositionNotDetected) != 0, "The pan position cannot be detected")
        ];
        foreach (var (key, active, message) in faults)
            SetFault(key, active, message);
        if (faults.Any(fault => fault.Active))
            RemoveDetail(PanTiltStatusLabel);
        else
            SetDetail(PanTiltStatusLabel, $"Normal: {BitConverter.ToString(payload)}");
    }

    private void HandlePanTiltStatusError(byte[] payload)
    {
        if (IsRejection(payload))
        {
            _panTiltStatusInquiry = InquiryState.Done;
            ShowNotSupported("pan/tilt status", PanTiltStatusIssueKey, payload, PanTiltStatusLabel);
            return;
        }
        _panTiltStatusInquiry = InquiryState.Owed;
        MissedReply("pan/tilt status", PanTiltStatusIssueKey, $"{BitConverter.ToString(payload)} ({ErrorText(payload)})");
    }

    private void SetFault(string key, bool active, string message)
    {
        if (active)
            RaiseOngoingIssue(key, message, IssueSeverity.Major);
        else
            ResolveIssue(key);
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

    protected void SendInquiry(byte[] bytes, int replyLength, string description, Action<byte[]> onReply, Action<byte[]>? onError = null)
    {
        if (!CameraIdIsValid)
        {
            LogWarning("Not sending {inquiry}, camera id {cameraId} is out of range", BitConverter.ToString(bytes), _cameraId);
            return;
        }
        var pending = new PendingInquiry(bytes, description, replyLength, onReply, onError, Trigger.Id);
        if (!_useIpHeaders)
        {
            lock (_pendingLock)
                _queuedInquiries.Enqueue(pending);
            SendNextInquiry();
            return;
        }
        try
        {
            byte sequenceNumber;
            lock (_pendingLock)
            {
                sequenceNumber = _sequenceNumber++;
                _pendingCommands.Remove(sequenceNumber);
                _pendingInquiriesBySequence[sequenceNumber] = pending;
            }
            var message = PayloadWithIpHeader(PayloadType.ViscaInquiry, sequenceNumber, bytes);
            CommunicationClient.Send(message);
            lock (_pendingLock)
                _polledInquiries.Add(message);
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    // An IV-CAM-I12 silently drops inquiries when more than two await a reply, so without IP headers one is sent at a time.
    private void SendNextInquiry()
    {
        PendingInquiry? next;
        lock (_pendingLock)
        {
            if (InquiryAwaitingReply() || !_queuedInquiries.TryDequeue(out next))
                return;
            _inquiryInFlight = next;
            _inquiryExpiry?.Dispose();
            _inquiryExpiry = _timeProvider.CreateTimer(_ => ExpireInquiry(next), null, ReplyTimeout, Timeout.InfiniteTimeSpan);
            using (Trigger.Resume(next.TriggerId))
                WarnIfDropped(_sent.Add(next)?.Command);
        }
        using (Trigger.Resume(next.TriggerId))
        {
            try
            {
                CommunicationClient.Send(next.Bytes);
                lock (_pendingLock)
                    _polledInquiries.Add(next.Bytes);
            }
            catch (Exception e)
            {
                LogException(e);
                CommunicationState = CommunicationState.Error;
            }
        }
    }

    private bool InquiryAwaitingReply() =>
        _inquiryInFlight is { } inFlight && _sent.IsAwaitingReply(request => ReferenceEquals(request, inFlight));

    private void ExpireInquiry(PendingInquiry inquiry)
    {
        lock (_pendingLock)
        {
            if (!ReferenceEquals(_inquiryInFlight, inquiry))
                return;
            _inquiryInFlight = null;
        }
        SendNextInquiry();
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
            var pending = new PendingCommand(bytes, description, onCompleted, Trigger.Id);
            if (_useIpHeaders)
            {
                byte sequenceNumber;
                lock (_pendingLock)
                {
                    sequenceNumber = _sequenceNumber++;
                    _pendingInquiriesBySequence.Remove(sequenceNumber);
                    if (description == null)
                        _pendingCommands.Remove(sequenceNumber);
                    else
                        _pendingCommands[sequenceNumber] = pending;
                }
                CommunicationClient.Send(PayloadWithIpHeader(PayloadType.ViscaCommand, sequenceNumber, bytes));
            }
            else
            {
                if (DeviceSendsResponses)
                    WarnIfDropped(_sent.Add(pending)?.Command);
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

    private void WarnIfDropped(PendingRequest? dropped)
    {
        if (dropped != null)
            LogWarning("Dropped {Request}: more than {MaxCommands} requests awaiting a reply",
                BitConverter.ToString(dropped.Bytes), SentCommandTracker<PendingRequest>.MaxCommands);
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
            SendNextInquiry();
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

        ReplyReceived();
        var socket = (byte)(payload[1] & SocketMask);
        switch (payload[1] & 0xF0)
        {
            case 0x40:
                CommunicationState = CommunicationState.Okay;
                InvokePendingCallback(socket, sequenceNumber);
                LogVerbose("Command acknowledged");
                break;
            case 0x50:
                CommunicationState = CommunicationState.Okay;
                if (payload.Length == 3)
                {
                    CompletePendingCommand(socket, sequenceNumber);
                    LogVerbose("Command complete");
                    return;
                }
                AnswerPendingInquiry(payload, sequenceNumber);
                break;
            case 0x60:
                var errorMessage = ErrorText(payload);
                var failed = ConsumeFailedRequest(socket, sequenceNumber);
                if (failed?.Description is { } description)
                    errorMessage = $"Unable to {description}: {errorMessage}";
                using (Trigger.Resume(failed?.TriggerId))
                using (PushProperties("ProcessViscaPayload"))
                {
                    if (failed?.OnError is { } onError)
                    {
                        onError(payload);
                        break;
                    }
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
    private void InvokePendingCallback(byte socket, byte? sequenceNumber)
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
            if (TakeUnacknowledgedCommand() is not { } acknowledged)
                return;
            // The camera reuses a socket only once the command in it has finished.
            TakeExecutingCommand(socket);
            WarnIfDropped(_executing.Add((socket, acknowledged with { OnCompleted = null }))?.Command.Command);
            using (Trigger.Resume(acknowledged.TriggerId))
            using (PushProperties("InvokePendingCallback"))
                acknowledged.OnCompleted?.Invoke();
        }
    }

    private void CompletePendingCommand(byte socket, byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (!DeviceSendsResponses)
                return;
            if (ConsumePendingCommand(socket, sequenceNumber) is not { } pending)
                return;
            using (Trigger.Resume(pending.TriggerId))
            using (PushProperties("CompletePendingCommand"))
                pending.OnCompleted?.Invoke();
        }
    }

    private PendingCommand? ConsumePendingCommand(byte socket, byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (sequenceNumber is { } sequence)
                return _pendingCommands.Remove(sequence, out var pending) ? pending : null;
            return TakeExecutingCommand(socket) ?? TakeUnacknowledgedCommand();
        }
    }

    private PendingCommand? TakeExecutingCommand(byte socket) =>
        _executing.TakeReply(executing => executing.Socket == socket)?.Command.Command;

    private PendingCommand? TakeUnacknowledgedCommand() =>
        (PendingCommand?)_sent.TakeReply(request => request is PendingCommand)?.Command;

    private void AnswerPendingInquiry(byte[] payload, byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (!DeviceSendsResponses)
                return;
            if (ConsumePendingInquiry(payload.Length, sequenceNumber) is not { } inquiry)
            {
                LogWarning("Unexpected inquiry reply {response}", BitConverter.ToString(payload));
                return;
            }
            using (Trigger.Resume(inquiry.TriggerId))
            using (PushProperties("AnswerPendingInquiry"))
                inquiry.OnReply(payload);
        }
    }

    private PendingInquiry? ConsumePendingInquiry(int replyLength, byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (sequenceNumber is { } sequence)
                return _pendingInquiriesBySequence.Remove(sequence, out var pending) ? pending : null;
            return (PendingInquiry?)_sent.TakeReply(request => request is PendingInquiry inquiry && inquiry.ReplyLength == replyLength)?.Command;
        }
    }

    private (string? Description, string? TriggerId, Action<byte[]>? OnError)? ConsumeFailedRequest(byte socket, byte? sequenceNumber)
    {
        lock (_pendingLock)
        {
            if (sequenceNumber is { } sequence)
            {
                if (_pendingCommands.Remove(sequence, out var command))
                    return (command.Description, command.TriggerId, null);
                if (_pendingInquiriesBySequence.Remove(sequence, out var inquiry))
                    return (inquiry.Description, inquiry.TriggerId, inquiry.OnError);
                return null;
            }
            var failed = socket == 0
                ? _sent.TakeError()?.Command
                : TakeExecutingCommand(socket) ?? _sent.TakeError(request => request is PendingCommand)?.Command;
            return failed switch
            {
                PendingInquiry failedInquiry => (failedInquiry.Description, failedInquiry.TriggerId, failedInquiry.OnError),
                PendingCommand failedCommand => (failedCommand.Description, failedCommand.TriggerId, null),
                _ => null
            };
        }
    }

    private static string ErrorText(byte[] payload) =>
        ErrorMessages.TryGetValue(payload[2], out var message) ? message : $"Unknown error 0x{payload[2]:X2}";

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
        lock (_pendingLock)
            ForgetQueuedInquiries();
        Task.Run(_pollWorker.Stop).GetAwaiter().GetResult();
        LogBaseRegistry.Deregister(_pollWorker);
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }
}