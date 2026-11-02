using System.Text;
using AVCoders.Core;

namespace AVCoders.Dsp;

/// <summary>A CEC audio system (soundbar or AVR at logical address 5) as one <see cref="VolumeControl"/>, driven by volume and mute key presses; not a <see cref="Dsp"/>.</summary>
public class CecAudioSystem : VolumeControl, IDevice, IDisposable
{
    public const byte LogicalAddressAudioSystem = 0x05;
    public const byte LogicalAddressPlayback1 = 0x04;

    public const string OsdNameDetailLabel = "OSD name";
    public const string PhysicalAddressDetailLabel = "Physical address";
    public const string VendorDetailLabel = "Vendor";
    public const string CecVersionDetailLabel = "CEC version";
    public const string SystemAudioModeDetailLabel = "System audio mode";
    public const string LogicalAddressIssueKey = "logical-address";
    private const byte MaxLogicalAddress = 0x0E;

    private const char FeatureAbort = '\x00';
    private const char UserControlPressed = '\x44';
    private const char UserControlReleased = '\x45';
    private const char GiveOsdName = '\x46';
    private const char SetOsdName = '\x47';
    private const char GiveAudioStatus = '\x71';
    private const char SetSystemAudioMode = '\x72';
    private const char ReportAudioStatus = '\x7A';
    private const char GiveSystemAudioModeStatus = '\x7D';
    private const char SystemAudioModeStatus = '\x7E';
    private const char GivePhysicalAddress = '\x83';
    private const char ReportPhysicalAddress = '\x84';
    private const char DeviceVendorId = '\x87';
    private const char GiveDeviceVendorId = '\x8C';
    private const char GiveDevicePowerStatus = '\x8F';
    private const char ReportPowerStatus = '\x90';
    private const char CecVersion = '\x9E';
    private const char GetCecVersion = '\x9F';

    private const char VolumeUpKey = '\x41';
    private const char VolumeDownKey = '\x42';
    private const char MuteKey = '\x43';

    private const char DeviceTypeAudioSystem = '\x05';
    private const int ReportPhysicalAddressLength = 5;
    private const int AudioStatusMuteBit = 0x80;
    private const int AudioStatusVolumeMask = 0x7F;
    private const int AudioStatusVolumeUnknown = 0x7F;

    public const int MaxUnansweredPolls = 3;
    public const int DefaultVolumeStep = 2;
    public static readonly TimeSpan StatusReadDelayAfterKey = TimeSpan.FromMilliseconds(300);
    private const int MaxWalkPasses = 2;
    private static readonly TimeSpan KeyHold = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MinimumFrameSpacing = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan QueryReplySpacing = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DiscoveryReplySpacing = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(1);

    public CommunicationStateHandler? CommunicationStateHandlers;
    public PowerStateHandler? PowerStateHandlers;
    public PowerStateHandler? DesiredPowerStateHandlers;
    public event Action<PowerState>? OnPowerStateChanged;
    public event Action<PowerState>? OnDesiredPowerStateChanged;
    public event Action<CommunicationState>? OnCommunicationStateChanged;
    public readonly CommunicationClient CommunicationClient;
    protected const string CommunicationIssueKey = "communication";
    private PowerState _powerState = PowerState.Unknown;
    private PowerState _desiredPowerState = PowerState.Unknown;
    private CommunicationState _communicationState = CommunicationState.Unknown;

    private readonly SerialClient _cecStream;
    private readonly char _commandHeader;
    private readonly char _responseHeader;
    private readonly char _deviceBroadcastHeader;
    private readonly string? _logicalAddressError;
    private readonly TimeProvider _timeProvider;
    private readonly ThreadWorker? _pollWorker;
    private ITimer? _statusReadTimer;
    private readonly object _statusReadTimerLock = new();
    private readonly SemaphoreSlim _spacingLock = new(1, 1);
    private readonly object _sendLock = new();
    private readonly object _statusLock = new();
    private readonly object _walkLock = new();
    private readonly object _pollsLock = new();
    private readonly object _muteLock = new();
    private readonly object _queuedSendsLock = new();
    private Task _queuedSends = Task.CompletedTask;
    private long _lastSendTimestamp;
    private TimeSpan _spacingAfterLastSend = TimeSpan.Zero;

    private bool _disposed;
    private MuteState _mute = MuteState.Unknown;
    private bool _discovered;
    private int _unansweredPolls;
    private TaskCompletionSource _nextAudioStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _targetLevel = -1;
    private Task? _walkTask;
    private int _volumeStep = DefaultVolumeStep;

    /// <summary>Levels moved per Volume Up / Down press; device-defined, two on the Sonos Beam.</summary>
    public int VolumeStep
    {
        get => _volumeStep;
        set
        {
            if (value < 1)
            {
                using (PushProperties("VolumeStep"))
                    LogWarning("The volume step is at least one level per press, not {Value}, so it stays {VolumeStep}", value, _volumeStep);
                AddEvent(EventType.Error, $"The volume step is at least one level per press, not {value}");
                return;
            }
            _volumeStep = value;
        }
    }

    /// <summary>False until a Report Audio Status carries a real level, and after one that reports it unknown (0x7F).</summary>
    public bool IsLevelKnown { get; private set; }

    /// <summary>pollTime 0 or less disables polling and the discovery that runs with the first poll.</summary>
    public CecAudioSystem(SerialClient cecStream, string name, byte audioSystemLogicalAddress = LogicalAddressAudioSystem,
        byte ownLogicalAddress = LogicalAddressPlayback1, int pollTime = 10)
        : this(cecStream, name, audioSystemLogicalAddress, ownLogicalAddress, pollTime, null)
    {
    }

    internal CecAudioSystem(SerialClient cecStream, string name, byte audioSystemLogicalAddress, byte ownLogicalAddress, int pollTime,
        TimeProvider? timeProvider)
        : base(name, VolumeType.Speaker)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastSendTimestamp = _timeProvider.GetTimestamp() - _timeProvider.TimestampFrequency;
        _cecStream = cecStream;
        CommunicationClient = cecStream;
        if (audioSystemLogicalAddress > MaxLogicalAddress || ownLogicalAddress > MaxLogicalAddress)
        {
            _logicalAddressError = $"CEC logical addresses are 0x00 to 0x0E, the audio system is 0x{audioSystemLogicalAddress:X2} and this device is 0x{ownLogicalAddress:X2}";
            RaiseOngoingIssue(LogicalAddressIssueKey, _logicalAddressError, IssueSeverity.Critical);
        }
        _commandHeader = (char)((ownLogicalAddress << 4) | audioSystemLogicalAddress);
        _responseHeader = (char)((audioSystemLogicalAddress << 4) | ownLogicalAddress);
        _deviceBroadcastHeader = (char)((audioSystemLogicalAddress << 4) | 0x0F);
        _cecStream.ResponseHandlers += HandleResponse;
        _cecStream.ConnectionStateHandlers += AddConnectionEvent;
        _cecStream.ConnectionStateHandlers += HandleConnectionState;
        _cecStream.ConnectionStateHandlers += HandleRequestedDisconnect;
        CommunicationState = CommunicationState.NotAttempted;
        if (pollTime <= 0 || _logicalAddressError != null)
            return;
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(pollTime), false, _timeProvider);
        _ = Task.Run(RestartPollingAfterStartup);
    }

    private async Task RestartPollingAfterStartup()
    {
        await Task.Delay(StartupDelay, _timeProvider);
        RestartPolling();
    }

    public PowerState PowerState
    {
        get => _powerState;
        protected set
        {
            if (_powerState == value)
                return;
            _powerState = value;
            AddEvent(EventType.Power, value.ToString());
            PowerStateHandlers?.Invoke(value);
            OnPowerStateChanged?.Invoke(value);
        }
    }

    public PowerState DesiredPowerState
    {
        get => _desiredPowerState;
        protected set
        {
            if (_desiredPowerState == value)
                return;
            _desiredPowerState = value;
            AddEvent(EventType.Power, $"Desired power state is now {value.ToString()}");
            DesiredPowerStateHandlers?.Invoke(value);
            OnDesiredPowerStateChanged?.Invoke(value);
        }
    }

    public CommunicationState CommunicationState
    {
        get => _communicationState;
        protected set
        {
            if (_communicationState == value)
                return;
            _communicationState = value;
            AddEvent(EventType.DriverState, value.ToString());
            if (value == CommunicationState.Error)
                RaiseOngoingIssue(CommunicationIssueKey, "Device communication error", IssueSeverity.Critical);
            else if (value is CommunicationState.Okay or CommunicationState.NotAttempted)
                ResolveIssue(CommunicationIssueKey);
            CommunicationStateHandlers?.Invoke(value);
            OnCommunicationStateChanged?.Invoke(value);
        }
    }

    private void AddConnectionEvent(ConnectionState connectionState) => AddEvent(EventType.Connection, connectionState.ToString());

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        _discovered = false;
        RestartPolling();
    }

    private void RestartPolling()
    {
        lock (_pollsLock)
        {
            if (!_disposed)
                _pollWorker?.Restart();
        }
    }

    private void HandleRequestedDisconnect(ConnectionState connectionState)
    {
        if (CommunicationClient.DesiredConnectionState != ConnectionState.Disconnected)
            return;
        CommunicationState = CommunicationState.NotAttempted;
    }

    private Task Poll(CancellationToken token)
    {
        var connectionState = CommunicationClient.ConnectionState;
        if (connectionState is ConnectionState.Disconnected or ConnectionState.Disconnecting or ConnectionState.Error)
        {
            using (PushProperties("Poll"))
                LogDebug("Not polling, the CEC stream is {ConnectionState}", connectionState);
            return Task.CompletedTask;
        }
        return Task.Run(SendPoll);
    }

    /// <summary>One poll cycle: discovery if not yet done, then Give Audio Status.</summary>
    public void PollAudioStatus() => QueueSend(SendPoll, "Polling the audio status failed");

    private async Task SendPoll()
    {
        using (PushProperties("PollAudioStatus"))
        {
            if (!_discovered)
                await SendDiscovery();
            int unansweredPolls;
            lock (_pollsLock)
                unansweredPolls = _unansweredPolls++;
            if (unansweredPolls >= MaxUnansweredPolls && CommunicationState != CommunicationState.Error)
            {
                LogWarning("No answer to the last {Count} audio status polls", unansweredPolls);
                CommunicationState = CommunicationState.Error;
                PowerState = PowerState.Unknown;
                lock (_muteLock)
                    _mute = MuteState.Unknown;
                PublishMute();
                IsLevelKnown = false;
                _discovered = false;
            }
            await RequestAudioStatus();
        }
    }

    /// <summary>Queries the physical address, name, vendor, CEC version, system audio mode and power into <see cref="LogBase.Details"/>.</summary>
    public void Discover() => QueueSend(SendDiscovery, "Sending the discovery requests failed");

    private async Task SendDiscovery()
    {
        using (PushProperties("Discover"))
        {
            LogDebug("Sending CEC discovery requests");
            await SendFrame([_commandHeader, GivePhysicalAddress], DiscoveryReplySpacing);
            await SendFrame([_commandHeader, GiveOsdName], DiscoveryReplySpacing);
            await SendFrame([_commandHeader, GiveDeviceVendorId], DiscoveryReplySpacing);
            await SendFrame([_commandHeader, GetCecVersion], DiscoveryReplySpacing);
            await SendFrame([_commandHeader, GiveSystemAudioModeStatus], DiscoveryReplySpacing);
            await SendFrame([_commandHeader, GiveDevicePowerStatus], DiscoveryReplySpacing);
        }
    }

    private Task RequestAudioStatus() => SendFrame([_commandHeader, GiveAudioStatus], QueryReplySpacing);

    private async Task ReadStatusNow()
    {
        try
        {
            await RequestAudioStatus();
        }
        catch (Exception e)
        {
            LogException(e, "Reading the audio status after a key failed");
        }
    }

    private void HandleResponse(string incoming)
    {
        using (PushProperties("HandleResponse"))
        {
            var frame = StripRepeatedHeader(incoming);
            if (frame.Length < 1)
                return;
            var header = frame[0];
            if (header != _responseHeader && header != _deviceBroadcastHeader)
                return;
            if (frame.Length < 2)
                return;

            CommunicationState = CommunicationState.Okay;
            lock (_pollsLock)
                _unansweredPolls = 0;

            switch (frame[1])
            {
                case ReportAudioStatus when frame.Length >= 3:
                    HandleAudioStatus(frame[2]);
                    break;
                case ReportPowerStatus when frame.Length >= 3:
                    PowerState = frame[2] switch
                    {
                        '\x00' => PowerState.On,
                        '\x01' => PowerState.Off,
                        '\x02' => PowerState.Warming,
                        '\x03' => PowerState.Cooling,
                        _ => PowerState
                    };
                    break;
                case SystemAudioModeStatus when frame.Length >= 3:
                case SetSystemAudioMode when frame.Length >= 3:
                    SetDetail(SystemAudioModeDetailLabel, frame[2] == '\x01' ? "On" : "Off");
                    break;
                case ReportPhysicalAddress when frame.Length == ReportPhysicalAddressLength && frame[4] == DeviceTypeAudioSystem:
                    SetDetail(PhysicalAddressDetailLabel, FormatPhysicalAddress(frame[2], frame[3]));
                    _discovered = true;
                    break;
                case SetOsdName when frame.Length >= 3:
                    SetDetail(OsdNameDetailLabel, new string(frame[2..]));
                    break;
                case DeviceVendorId when frame.Length >= 5:
                    SetDetail(VendorDetailLabel, FormatVendor(frame[2], frame[3], frame[4]));
                    break;
                case CecVersion when frame.Length >= 3:
                    SetDetail(CecVersionDetailLabel, FormatCecVersion(frame[2]));
                    break;
                case FeatureAbort when frame.Length >= 4:
                    HandleFeatureAbort(frame[2], frame[3]);
                    break;
                default:
                    LogDebug("Unhandled CEC frame: {Frame}", Describe(frame));
                    break;
            }
        }
    }

    private void HandleAudioStatus(char status)
    {
        lock (_muteLock)
            _mute = (status & AudioStatusMuteBit) != 0 ? MuteState.On : MuteState.Off;
        PublishMute();
        var level = status & AudioStatusVolumeMask;
        if (level == AudioStatusVolumeUnknown)
        {
            LogDebug("The audio system reports its volume as unknown");
            IsLevelKnown = false;
        }
        else
        {
            Volume = Math.Min(level, 100);
            IsLevelKnown = true;
        }
        TaskCompletionSource reported;
        lock (_statusLock)
        {
            reported = _nextAudioStatus;
            _nextAudioStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        reported.SetResult();
    }

    private void HandleFeatureAbort(char opcode, char reason)
    {
        var reasonText = reason switch
        {
            '\x00' => "unrecognised opcode",
            '\x01' => "not in the correct mode to respond",
            '\x02' => "cannot provide source",
            '\x03' => "invalid operand",
            '\x04' => "refused",
            '\x05' => "unable to determine",
            _ => $"reason 0x{(int)reason:X2}"
        };
        switch (opcode)
        {
            case UserControlPressed:
                LogWarning("The audio system rejected the last key: {Reason}", reasonText);
                AddEvent(EventType.Error, $"Key rejected: {reasonText}");
                break;
            default:
                LogDebug("Feature abort for opcode 0x{Opcode:X2}: {Reason}", (int)opcode, reasonText);
                break;
        }
    }

    private async Task<bool> WaitForAudioStatus()
    {
        Task reported;
        lock (_statusLock)
            reported = _nextAudioStatus.Task;
        await RequestAudioStatus();
        using var replyTimeout = new CancellationTokenSource();
        var first = await Task.WhenAny(reported, Task.Delay(ReplyTimeout, _timeProvider, replyTimeout.Token));
        replyTimeout.Cancel();
        return first == reported;
    }

    private char[] StripRepeatedHeader(string incoming)
    {
        var frame = incoming.ToCharArray();
        while (frame.Length >= 2 && frame[0] == frame[1] &&
               (frame[0] == _responseHeader || frame[0] == _deviceBroadcastHeader))
            frame = frame[1..];
        return frame;
    }

    private static string FormatPhysicalAddress(char high, char low) =>
        $"{high >> 4}.{high & 0x0F}.{low >> 4}.{low & 0x0F}";

    private static string FormatVendor(char b0, char b1, char b2)
    {
        var id = $"{(int)b0:X2}:{(int)b1:X2}:{(int)b2:X2}";
        return id == "EA:BE:A7" ? $"Sonos ({id})" : id;
    }

    private static string FormatCecVersion(char version) => version switch
    {
        '\x00' => "1.1",
        '\x01' => "1.2",
        '\x02' => "1.2a",
        '\x03' => "1.3",
        '\x04' => "1.3a",
        '\x05' => "1.4",
        '\x06' => "2.0",
        _ => $"0x{(int)version:X2}"
    };

    private static string Describe(char[] frame)
    {
        var sb = new StringBuilder();
        foreach (var c in frame)
        {
            if (sb.Length > 0)
                sb.Append(':');
            sb.Append(((int)c).ToString("X2"));
        }
        return sb.ToString();
    }

    private async Task SendFrame(char[] frame, TimeSpan? spacingAfter = null)
    {
        await _spacingLock.WaitAsync();
        try
        {
            await SendSpacedFrame(frame, spacingAfter);
        }
        finally
        {
            _spacingLock.Release();
        }
    }

    private async Task SendKey(char code)
    {
        await _spacingLock.WaitAsync();
        try
        {
            await SendSpacedFrame([_commandHeader, UserControlPressed, code], KeyHold);
            await SendSpacedFrame([_commandHeader, UserControlReleased], null);
        }
        finally
        {
            _spacingLock.Release();
        }
    }

    private async Task SendSpacedFrame(char[] frame, TimeSpan? spacingAfter)
    {
        if (_logicalAddressError != null)
        {
            using (PushProperties("SendFrame"))
                LogWarning("Not sending {Frame}: {LogicalAddressError}", Describe(frame), _logicalAddressError);
            return;
        }
        var wait = _spacingAfterLastSend - _timeProvider.GetElapsedTime(_lastSendTimestamp);
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _timeProvider);
        lock (_sendLock)
        {
            if (_disposed)
                return;
            _cecStream.Send(frame);
            _lastSendTimestamp = _timeProvider.GetTimestamp();
            _spacingAfterLastSend = spacingAfter ?? MinimumFrameSpacing;
        }
    }

    private Task QueueSend(Func<Task> send, string failure)
    {
        lock (_queuedSendsLock)
        {
            _queuedSends = _queuedSends.ContinueWith(_ => SendLoggingFailure(send, failure), TaskScheduler.Default).Unwrap();
            return _queuedSends;
        }
    }

    private async Task SendLoggingFailure(Func<Task> send, string failure)
    {
        try
        {
            await send();
        }
        catch (Exception e)
        {
            LogException(e, failure);
        }
    }

    private Task QueueKeyPresses(char key, int presses) =>
        QueueSend(() => SendKeyPresses(key, presses), $"Sending {presses} presses of key 0x{(int)key:X2} failed");

    private async Task SendKeyPresses(char key, int presses)
    {
        for (var i = 0; i < presses; i++)
            await SendKey(key);
        ReadStatusAfterKey();
    }

    private void ReadStatusAfterKey()
    {
        lock (_statusReadTimerLock)
        {
            if (_disposed)
                return;
            _statusReadTimer?.Dispose();
            _statusReadTimer = _timeProvider.CreateTimer(_ => Task.Run(ReadStatusNow), null, StatusReadDelayAfterKey, Timeout.InfiniteTimeSpan);
        }
    }

    private int PressesFor(int levels) => (levels + VolumeStep - 1) / VolumeStep;

    /// <summary>Raises the level by about <paramref name="amount"/> percent, rounded up to whole presses of <see cref="VolumeStep"/>.</summary>
    public override void LevelUp(int amount)
    {
        using (PushProperties("LevelUp"))
            SendLevelKeys(VolumeUpKey, amount);
    }

    /// <summary>Lowers the level by about <paramref name="amount"/> percent, rounded up to whole presses of <see cref="VolumeStep"/>.</summary>
    public override void LevelDown(int amount)
    {
        using (PushProperties("LevelDown"))
            SendLevelKeys(VolumeDownKey, amount);
    }

    private void SendLevelKeys(char key, int amount)
    {
        if (amount < 1)
        {
            LogWarning("The amount needs to be at least one percent, it's {Amount}", amount);
            AddEvent(EventType.Error, $"The amount needs to be at least one percent, it's {amount}");
            return;
        }
        QueueKeyPresses(key, PressesFor(amount));
    }

    /// <summary>Walks the level to <paramref name="percentage"/> with key presses in the background; a call mid-walk retargets it.</summary>
    public override void SetLevel(int percentage) => _ = SetLevelAsync(percentage);

    /// <summary><see cref="SetLevel"/> as a task that completes when the walk has finished or given up.</summary>
    public Task SetLevelAsync(int percentage)
    {
        using (PushProperties("SetLevel"))
        {
            if (percentage is > 100 or < 0)
            {
                LogWarning("Volume needs to be a value between 0 and 100, it's {Volume} - clamping", percentage);
                percentage = Math.Clamp(percentage, 0, 100);
            }
            lock (_walkLock)
            {
                _targetLevel = percentage;
                if (_walkTask is { IsCompleted: false })
                    return _walkTask;
                _walkTask = Task.Run(WalkLevel);
                return _walkTask;
            }
        }
    }

    private async Task WalkLevel()
    {
        using (PushProperties("SetLevel"))
        {
            var attempted = -1;
            var passes = 0;
            var unknownReads = 0;
            while (true)
            {
                int target;
                lock (_walkLock)
                {
                    target = _targetLevel;
                    if (target < 0)
                    {
                        _walkTask = null;
                        return;
                    }
                }
                if (target != attempted)
                {
                    attempted = target;
                    passes = 0;
                    unknownReads = 0;
                }

                try
                {
                    if (!await WaitForAudioStatus())
                    {
                        LogWarning("No Report Audio Status within {Timeout} ms, giving up on level {Target}", ReplyTimeout.TotalMilliseconds, target);
                        AddEvent(EventType.Error, $"No audio status reply, giving up on level {target}");
                        FinishWalk(target);
                        continue;
                    }
                    if (!IsLevelKnown)
                    {
                        if (++unknownReads > 1)
                        {
                            LogWarning("The audio system does not know its volume, giving up on level {Target}", target);
                            AddEvent(EventType.Error, $"Volume unknown, giving up on level {target}");
                            FinishWalk(target);
                        }
                        continue;
                    }
                    unknownReads = 0;

                    var difference = target - Volume;
                    if (Math.Abs(difference) * 2 <= VolumeStep)
                    {
                        if (difference != 0)
                            LogDebug("Level {Volume} is the closest reachable to {Target} with a step of {Step}", Volume, target, VolumeStep);
                        FinishWalk(target);
                        continue;
                    }
                    if (passes >= MaxWalkPasses)
                    {
                        LogWarning("Level is {Volume} after {Passes} passes, wanted {Target}", Volume, passes, target);
                        AddEvent(EventType.Error, $"Level is {Volume} after {passes} passes, wanted {target}");
                        FinishWalk(target);
                        continue;
                    }

                    var presses = PressesFor(Math.Abs(difference));
                    LogDebug("Level is {Volume}, sending {Presses} Volume {Direction} presses for {Target}",
                        Volume, presses, difference > 0 ? "Up" : "Down", target);
                    for (var i = 0; i < presses && TargetIs(target); i++)
                        await SendKey(difference > 0 ? VolumeUpKey : VolumeDownKey);
                    passes++;
                    await Task.Delay(StatusReadDelayAfterKey, _timeProvider);
                }
                catch (Exception e)
                {
                    LogException(e, $"Setting the level to {target} failed");
                    FinishWalk(target);
                }
            }
        }
    }

    private bool TargetIs(int target)
    {
        lock (_walkLock)
            return _targetLevel == target;
    }

    private void FinishWalk(int target)
    {
        lock (_walkLock)
        {
            if (_targetLevel == target)
                _targetLevel = -1;
        }
    }

    /// <summary>Sends the Mute toggle key only if the last reported mute state differs; reads it first in the background if never reported.</summary>
    public override void SetAudioMute(MuteState state) => _ = SetAudioMuteAsync(state);

    /// <summary><see cref="SetAudioMute"/> as a task that completes once the key has been sent, skipped, or the read-first has given up.</summary>
    public Task SetAudioMuteAsync(MuteState state)
    {
        using (PushProperties("SetAudioMute"))
        {
            if (state == MuteState.Unknown)
            {
                LogWarning("Cannot set the mute state to Unknown");
                return Task.CompletedTask;
            }
            if (MuteState != MuteState.Unknown)
                return ApplyMute(state);
            return Task.Run(async () =>
            {
                using (PushProperties("SetAudioMute"))
                {
                    if (await WaitForAudioStatus())
                    {
                        await ApplyMute(state);
                        return;
                    }
                    LogWarning("No Report Audio Status within {Timeout} ms, not sending the mute key blind", ReplyTimeout.TotalMilliseconds);
                    AddEvent(EventType.Error, $"No audio status reply, cannot set mute {state}");
                }
            });
        }
    }

    private Task ApplyMute(MuteState state)
    {
        Task keySent;
        lock (_muteLock)
        {
            if (_mute == state)
                return Task.CompletedTask;
            _mute = state;
            keySent = QueueKeyPresses(MuteKey, 1);
        }
        PublishMute();
        return keySent;
    }

    /// <summary>Sends the Mute toggle key once.</summary>
    public override void ToggleAudioMute()
    {
        using (PushProperties("ToggleAudioMute"))
        {
            lock (_muteLock)
            {
                _mute = _mute switch
                {
                    MuteState.On => MuteState.Off,
                    MuteState.Off => MuteState.On,
                    _ => MuteState.Unknown
                };
                QueueKeyPresses(MuteKey, 1);
            }
            PublishMute();
        }
    }

    private void PublishMute()
    {
        var mute = ReadMute();
        while (MuteState != mute)
        {
            MuteState = mute;
            mute = ReadMute();
        }
    }

    private MuteState ReadMute()
    {
        lock (_muteLock)
            return _mute;
    }

    /// <summary>No-op: the Sonos Beam is always On over CEC.</summary>
    public void PowerOn()
    {
        using (PushProperties("PowerOn"))
            LogInformation("PowerOn is a no-op: the audio system does not wake or sleep over CEC");
        AddEvent(EventType.Power, "PowerOn ignored - the audio system does not sleep over CEC");
    }

    /// <summary>No-op: the Sonos Beam acknowledges Standby but stays On.</summary>
    public void PowerOff()
    {
        using (PushProperties("PowerOff"))
            LogInformation("PowerOff is a no-op: the audio system ignores Standby and stays On");
        AddEvent(EventType.Power, "PowerOff ignored - the audio system ignores Standby");
    }

    public void Dispose()
    {
        lock (_sendLock)
        lock (_pollsLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _cecStream.ResponseHandlers -= HandleResponse;
        _cecStream.ConnectionStateHandlers -= AddConnectionEvent;
        _cecStream.ConnectionStateHandlers -= HandleConnectionState;
        _cecStream.ConnectionStateHandlers -= HandleRequestedDisconnect;
        if (_pollWorker != null)
        {
            Task.Run(_pollWorker.Stop).GetAwaiter().GetResult();
            LogBaseRegistry.Deregister(_pollWorker);
        }
        lock (_statusReadTimerLock)
        {
            _statusReadTimer?.Dispose();
            _statusReadTimer = null;
        }
        LogBaseRegistry.Deregister(this);
        GC.SuppressFinalize(this);
    }
}
