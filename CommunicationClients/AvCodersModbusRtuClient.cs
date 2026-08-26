using System.Diagnostics;
using AVCoders.Core;

namespace AVCoders.CommunicationClients;

public class AvCodersModbusRtuClient : ModbusRtuClient
{
    private const int MaxGatherSize = 512;

    private readonly CommunicationClient _transport;
    private readonly SemaphoreSlim _busLock = new(1, 1);
    private readonly object _receiveLock = new();
    private readonly List<byte> _gather = [];
    private PendingRequest? _pending;

    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MinimumResponseTime { get; set; } = TimeSpan.Zero;
    public TimeSpan InterFrameDelay { get; set; } = TimeSpan.FromMilliseconds(4);
    public int Retries { get; set; } = 2;
    public int AddressOffset { get; set; }
    public int MaxRegistersPerRead { get; set; } = 125;
    public bool DiscardLocalEcho { get; set; }

    private sealed class PendingRequest(
        byte deviceId, byte function, int expectedLength, int expectedByteCount,
        byte[] requestFrame, bool echoPending, string context)
    {
        public byte DeviceId { get; } = deviceId;
        public byte Function { get; } = function;
        public int ExpectedLength { get; } = expectedLength;
        public int ExpectedByteCount { get; } = expectedByteCount;
        public byte[] RequestFrame { get; } = requestFrame;
        public bool EchoPending { get; set; } = echoPending;
        public string Context { get; } = context;
        public long SentTimestamp { get; set; }
        public TaskCompletionSource<byte[]> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public AvCodersModbusRtuClient(CommunicationClient transport, string name)
        : base(name, transport.Host, transport.Port)
    {
        _transport = transport;
        _transport.ResponseByteHandlers += HandleResponse;
        _transport.ConnectionStateHandlers += state => ConnectionState = state;
        ConnectionState = transport.ConnectionState;
    }

    public override void Send(string message) => _transport.Send(message);

    public override void Send(byte[] bytes) => _transport.Send(bytes);

    public override async Task<bool[]> ReadCoils(byte deviceId, ushort startCoil, ushort count, CancellationToken token = default)
    {
        if (count is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Coil count must be between 1 and 2000");
        ushort wireAddress = ApplyOffset(startCoil);
        int byteCount = (count + 7) / 8;
        byte[] response = await ExecuteRequest(deviceId, 0x01,
            [(byte)(wireAddress >> 8), (byte)wireAddress, (byte)(count >> 8), (byte)count],
            5 + byteCount, byteCount,
            DescribeTarget("ReadCoils", startCoil, wireAddress, count), token);
        bool[] values = new bool[count];
        for (int i = 0; i < count; i++)
            values[i] = (response[3 + i / 8] >> (i % 8) & 1) == 1;
        return values;
    }

    public override async Task<ushort[]> ReadHoldingRegisters(byte deviceId, ushort startRegister, ushort count, CancellationToken token = default)
    {
        int cap = Math.Min(MaxRegistersPerRead, 125);
        if (count < 1 || count > cap)
            throw new ArgumentOutOfRangeException(nameof(count), count, $"Register count must be between 1 and {cap}");
        ushort wireAddress = ApplyOffset(startRegister);
        byte[] response = await ExecuteRequest(deviceId, 0x03,
            [(byte)(wireAddress >> 8), (byte)wireAddress, (byte)(count >> 8), (byte)count],
            5 + count * 2, count * 2,
            DescribeTarget("ReadHoldingRegisters", startRegister, wireAddress, count), token);
        ushort[] values = new ushort[count];
        for (int i = 0; i < count; i++)
            values[i] = (ushort)(response[3 + i * 2] << 8 | response[4 + i * 2]);
        return values;
    }

    public override Task WriteCoil(byte deviceId, ushort coil, bool value, CancellationToken token = default)
    {
        ushort wireAddress = ApplyOffset(coil);
        return ExecuteRequest(deviceId, 0x05,
            [(byte)(wireAddress >> 8), (byte)wireAddress, value ? (byte)0xFF : (byte)0x00, 0x00],
            8, -1, DescribeTarget("WriteCoil", coil, wireAddress, 1), token);
    }

    public override Task WriteCoils(byte deviceId, ushort startCoil, bool[] values, CancellationToken token = default)
    {
        if (values.Length is < 1 or > 1968)
            throw new ArgumentOutOfRangeException(nameof(values), values.Length,
                "Coil count must be between 1 and 1968");
        ushort wireAddress = ApplyOffset(startCoil);
        int byteCount = (values.Length + 7) / 8;
        byte[] payload = new byte[5 + byteCount];
        payload[0] = (byte)(wireAddress >> 8);
        payload[1] = (byte)wireAddress;
        payload[2] = (byte)(values.Length >> 8);
        payload[3] = (byte)values.Length;
        payload[4] = (byte)byteCount;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
                payload[5 + i / 8] |= (byte)(1 << (i % 8));
        }
        return ExecuteRequest(deviceId, 0x0F, payload, 8, -1,
            DescribeTarget("WriteCoils", startCoil, wireAddress, values.Length), token);
    }

    public override Task WriteRegister(byte deviceId, ushort register, ushort value, CancellationToken token = default)
    {
        ushort wireAddress = ApplyOffset(register);
        return ExecuteRequest(deviceId, 0x06,
            [(byte)(wireAddress >> 8), (byte)wireAddress, (byte)(value >> 8), (byte)value],
            8, -1, DescribeTarget("WriteRegister", register, wireAddress, 1), token);
    }

    public override Task WriteRegisters(byte deviceId, ushort startRegister, ushort[] values, CancellationToken token = default)
    {
        if (values.Length is < 1 or > 123)
            throw new ArgumentOutOfRangeException(nameof(values), values.Length,
                "Register count must be between 1 and 123");
        ushort wireAddress = ApplyOffset(startRegister);
        byte[] payload = new byte[5 + values.Length * 2];
        payload[0] = (byte)(wireAddress >> 8);
        payload[1] = (byte)wireAddress;
        payload[2] = (byte)(values.Length >> 8);
        payload[3] = (byte)values.Length;
        payload[4] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
        {
            payload[5 + i * 2] = (byte)(values[i] >> 8);
            payload[6 + i * 2] = (byte)values[i];
        }
        return ExecuteRequest(deviceId, 0x10, payload, 8, -1,
            DescribeTarget("WriteRegisters", startRegister, wireAddress, values.Length), token);
    }

    private ushort ApplyOffset(ushort documentedAddress)
    {
        int wireAddress = documentedAddress + AddressOffset;
        if (wireAddress is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(documentedAddress), wireAddress,
                $"Address {documentedAddress} with offset {AddressOffset} is outside the valid address range");
        return (ushort)wireAddress;
    }

    private string DescribeTarget(string operation, ushort documentedAddress, ushort wireAddress, int count) =>
        AddressOffset == 0
            ? $"{operation} at address {documentedAddress}, count {count}"
            : $"{operation} at address {documentedAddress} (wire address {wireAddress}), count {count}";

    private async Task<byte[]> ExecuteRequest(byte deviceId, byte function, byte[] payload,
        int expectedLength, int expectedByteCount, string context, CancellationToken cancellationToken)
    {
        using (PushProperties("ExecuteRequest"))
        {
            byte[] frame = BuildFrame(deviceId, function, payload);
            await _busLock.WaitAsync(cancellationToken);
            try
            {
                for (int attempt = 0;; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pending = new PendingRequest(deviceId, function, expectedLength, expectedByteCount,
                        frame, DiscardLocalEcho, context);
                    lock (_receiveLock)
                    {
                        _gather.Clear();
                        pending.SentTimestamp = Stopwatch.GetTimestamp();
                        _pending = pending;
                    }
                    _transport.Send(frame);
                    InvokeRequestHandlers(frame);
                    try
                    {
                        return await pending.Tcs.Task.WaitAsync(ResponseTimeout, cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        lock (_receiveLock)
                            _pending = null;
                        if (attempt >= Retries)
                        {
                            LogError("No response from device {DeviceId} to {Context} after {Attempts} attempts",
                                deviceId, context, attempt + 1);
                            throw new TimeoutException(
                                $"No response from Modbus device {deviceId} to {context} after {attempt + 1} attempts");
                        }
                        LogWarning("No response from device {DeviceId} to {Context}, retrying", deviceId, context);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_receiveLock)
                            _pending = null;
                        pending.Tcs.TrySetCanceled(cancellationToken);
                        throw;
                    }
                }
            }
            finally
            {
                if (InterFrameDelay > TimeSpan.Zero)
                    await Task.Delay(InterFrameDelay, CancellationToken.None);
                _busLock.Release();
            }
        }
    }

    private void HandleResponse(byte[] bytes)
    {
        using (PushProperties("HandleResponse"))
        {
            PendingRequest? pending;
            byte[]? frame = null;
            lock (_receiveLock)
            {
                pending = _pending;
                if (pending == null)
                {
                    LogVerbose("Ignoring {Count} bytes received with no request pending", bytes.Length);
                    return;
                }
                _gather.AddRange(bytes);
                if (_gather.Count > MaxGatherSize)
                {
                    LogWarning("Gather buffer exceeded {MaxGatherSize} bytes, clearing", MaxGatherSize);
                    _gather.Clear();
                    return;
                }
                if (pending.EchoPending && !TryConsumeEcho(pending))
                    return;
                while (true)
                {
                    frame = TryExtractFrame(pending);
                    if (frame == null)
                        break;
                    if (MinimumResponseTime > TimeSpan.Zero &&
                        Stopwatch.GetElapsedTime(pending.SentTimestamp) < MinimumResponseTime)
                    {
                        LogWarning("Discarding frame that arrived faster than {MinimumResponseTime} for {Context}",
                            MinimumResponseTime, pending.Context);
                        frame = null;
                        continue;
                    }
                    _pending = null;
                    break;
                }
            }

            if (frame == null)
                return;
            InvokeResponseHandlers(BitConverter.ToString(frame), frame);
            if ((frame[1] & 0x80) != 0)
            {
                byte exceptionCode = frame[2];
                pending.Tcs.TrySetException(new ModbusException(pending.Function, exceptionCode,
                    $"Device {pending.DeviceId} returned '{DescribeExceptionCode(exceptionCode)}' (function 0x{pending.Function:X2}) for {pending.Context}"));
                return;
            }
            pending.Tcs.TrySetResult(frame);
        }
    }

    private bool TryConsumeEcho(PendingRequest pending)
    {
        int compareLength = Math.Min(_gather.Count, pending.RequestFrame.Length);
        for (int i = 0; i < compareLength; i++)
        {
            if (_gather[i] != pending.RequestFrame[i])
            {
                pending.EchoPending = false;
                return true;
            }
        }
        if (_gather.Count < pending.RequestFrame.Length)
            return false;
        LogVerbose("Discarding {Count} local echo bytes", pending.RequestFrame.Length);
        _gather.RemoveRange(0, pending.RequestFrame.Length);
        pending.EchoPending = false;
        return true;
    }

    private byte[]? TryExtractFrame(PendingRequest pending)
    {
        while (_gather.Count >= 5)
        {
            if (_gather[0] != pending.DeviceId)
            {
                _gather.RemoveAt(0);
                continue;
            }
            int frameLength;
            if (_gather[1] == (byte)(pending.Function | 0x80))
                frameLength = 5;
            else if (_gather[1] == pending.Function)
            {
                if (pending.ExpectedByteCount >= 0 && _gather[2] != pending.ExpectedByteCount)
                {
                    int staleLength = 3 + _gather[2] + 2;
                    if (_gather.Count < staleLength)
                        return null;
                    byte[] stale = _gather.Take(staleLength).ToArray();
                    ushort staleCrc = Crc16(stale, staleLength - 2);
                    if (stale[^2] == (byte)staleCrc && stale[^1] == (byte)(staleCrc >> 8))
                    {
                        LogWarning("Discarding stale frame with byte count {Actual}, expected {Expected} for {Context}",
                            _gather[2], pending.ExpectedByteCount, pending.Context);
                        _gather.RemoveRange(0, staleLength);
                    }
                    else
                        _gather.RemoveAt(0);
                    continue;
                }
                frameLength = pending.ExpectedLength;
            }
            else
            {
                _gather.RemoveAt(0);
                continue;
            }
            if (_gather.Count < frameLength)
                return null;
            byte[] candidate = _gather.Take(frameLength).ToArray();
            ushort crc = Crc16(candidate, frameLength - 2);
            if (candidate[^2] == (byte)crc && candidate[^1] == (byte)(crc >> 8))
            {
                _gather.RemoveRange(0, frameLength);
                return candidate;
            }
            _gather.RemoveAt(0);
        }
        return null;
    }

    private static byte[] BuildFrame(byte deviceId, byte function, byte[] payload)
    {
        byte[] frame = new byte[payload.Length + 4];
        frame[0] = deviceId;
        frame[1] = function;
        payload.CopyTo(frame, 2);
        ushort crc = Crc16(frame, frame.Length - 2);
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    private static ushort Crc16(IReadOnlyList<byte> data, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)(crc >> 1 ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    private static string DescribeExceptionCode(byte code) => code switch
    {
        0x01 => "Illegal function",
        0x02 => "Illegal data address",
        0x03 => "Illegal data value",
        0x04 => "Slave device failure",
        0x05 => "Acknowledge",
        0x06 => "Slave device busy",
        _ => $"Exception code 0x{code:X2}"
    };
}
