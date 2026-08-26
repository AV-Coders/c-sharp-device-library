using AVCoders.Core.Tests;
using Moq;

namespace AVCoders.CommunicationClients.Tests;

public class AvCodersModbusRtuClientTest
{
    private readonly AvCodersModbusRtuClient _client;
    private readonly Mock<CommunicationClient> _mockClient = TestFactory.CreateCommunicationClient();
    private readonly List<byte[]> _sent = [];

    private static readonly byte[] ReadRegistersRequest = [0x11, 0x03, 0x00, 0x6B, 0x00, 0x03, 0x76, 0x87];
    private static readonly byte[] ReadRegistersResponse = [0x11, 0x03, 0x06, 0xAE, 0x41, 0x56, 0x52, 0x43, 0x40, 0x49, 0xAD];
    private static readonly byte[] WriteRegisterRequest = [0x11, 0x06, 0x00, 0x01, 0x00, 0x03, 0x9A, 0x9B];
    private static readonly byte[] ReadTwoRegistersRequest = [0x11, 0x03, 0x00, 0x6B, 0x00, 0x02, 0xB7, 0x47];
    private static readonly byte[] ReadTwoRegistersResponse = [0x11, 0x03, 0x04, 0x00, 0x0A, 0x01, 0x02, 0x4B, 0xA1];

    public AvCodersModbusRtuClientTest()
    {
        _mockClient.Setup(client => client.Send(It.IsAny<byte[]>())).Callback<byte[]>(bytes => _sent.Add(bytes));
        _client = new AvCodersModbusRtuClient(_mockClient.Object, "Test modbus client");
        _client.ResponseTimeout = TimeSpan.FromSeconds(5);
    }

    private void Respond(byte[] bytes) => _mockClient.Object.ResponseByteHandlers?.Invoke(bytes);

    [Fact]
    public async Task ReadCoils_SendsTheRequestFrame()
    {
        byte[] expected = [0x11, 0x01, 0x00, 0x13, 0x00, 0x25, 0x0E, 0x84];

        var task = _client.ReadCoils(0x11, 0x0013, 0x0025);
        Respond([0x11, 0x01, 0x05, 0xCD, 0x6B, 0xB2, 0x0E, 0x1B, 0x45, 0xE6]);
        await task;

        Assert.Equal(expected, _sent[0]);
    }

    [Fact]
    public async Task ReadCoils_UnpacksBitsLsbFirst()
    {
        var task = _client.ReadCoils(0x11, 0x0013, 0x0025);
        Respond([0x11, 0x01, 0x05, 0xCD, 0x6B, 0xB2, 0x0E, 0x1B, 0x45, 0xE6]);
        bool[] coils = await task;

        Assert.Equal(37, coils.Length);
        bool[] expectedFirstByte = [true, false, true, true, false, false, true, true];
        Assert.Equal(expectedFirstByte, coils.Take(8));
        Assert.True(coils[36]);
        Assert.False(coils[34]);
    }

    [Fact]
    public async Task ReadHoldingRegisters_SendsTheRequestFrame()
    {
        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond(ReadRegistersResponse);
        await task;

        Assert.Equal(ReadRegistersRequest, _sent[0]);
    }

    [Fact]
    public async Task ReadHoldingRegisters_DecodesBigEndianValues()
    {
        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond(ReadRegistersResponse);
        ushort[] registers = await task;

        Assert.Equal([0xAE41, 0x5652, 0x4340], registers);
    }

    [Fact]
    public async Task WriteCoil_On_SendsTheRequestFrame()
    {
        byte[] expected = [0x11, 0x05, 0x00, 0xAC, 0xFF, 0x00, 0x4E, 0x8B];

        var task = _client.WriteCoil(0x11, 0x00AC, true);
        Respond(expected);
        await task;

        Assert.Equal(expected, _sent[0]);
    }

    [Fact]
    public async Task WriteCoil_Off_SendsTheRequestFrame()
    {
        byte[] expected = [0x11, 0x05, 0x00, 0xAC, 0x00, 0x00, 0x0F, 0x7B];

        var task = _client.WriteCoil(0x11, 0x00AC, false);
        Respond(expected);
        await task;

        Assert.Equal(expected, _sent[0]);
    }

    [Fact]
    public async Task WriteCoils_PacksBitsLsbFirst()
    {
        byte[] expected = [0x11, 0x0F, 0x00, 0x13, 0x00, 0x0A, 0x02, 0xCD, 0x01, 0xBF, 0x0B];
        bool[] values = [true, false, true, true, false, false, true, true, true, false];

        var task = _client.WriteCoils(0x11, 0x0013, values);
        Respond([0x11, 0x0F, 0x00, 0x13, 0x00, 0x0A, 0x26, 0x99]);
        await task;

        Assert.Equal(expected, _sent[0]);
    }

    [Fact]
    public async Task WriteRegister_SendsTheRequestFrame()
    {
        var task = _client.WriteRegister(0x11, 0x0001, 0x0003);
        Respond(WriteRegisterRequest);
        await task;

        Assert.Equal(WriteRegisterRequest, _sent[0]);
    }

    [Fact]
    public async Task WriteRegisters_SendsBigEndianValues()
    {
        byte[] expected = [0x11, 0x10, 0x00, 0x01, 0x00, 0x02, 0x04, 0x00, 0x0A, 0x01, 0x02, 0xC6, 0xF0];

        var task = _client.WriteRegisters(0x11, 0x0001, [0x000A, 0x0102]);
        Respond([0x11, 0x10, 0x00, 0x01, 0x00, 0x02, 0x12, 0x98]);
        await task;

        Assert.Equal(expected, _sent[0]);
    }

    [Fact]
    public async Task ExceptionResponse_ThrowsModbusException()
    {
        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond([0x11, 0x83, 0x02, 0xC1, 0x34]);

        var exception = await Assert.ThrowsAsync<ModbusException>(() => task);
        Assert.Equal(0x03, exception.FunctionCode);
        Assert.Equal(0x02, exception.ExceptionCode);
        Assert.Contains("Illegal data address", exception.Message);
        Assert.Contains("Device 17", exception.Message);
        Assert.Contains("ReadHoldingRegisters", exception.Message);
        Assert.Contains("address 107", exception.Message);
        Assert.Contains("count 3", exception.Message);
    }

    [Fact]
    public async Task NoResponse_ThrowsTimeoutExceptionAfterRetries()
    {
        _client.ResponseTimeout = TimeSpan.FromMilliseconds(50);

        await Assert.ThrowsAsync<TimeoutException>(() => _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003));

        Assert.Equal(3, _sent.Count);
        Assert.All(_sent, frame => Assert.Equal(ReadRegistersRequest, frame));
    }

    [Fact]
    public async Task SplitResponse_StillCompletes()
    {
        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond(ReadRegistersResponse.Take(4).ToArray());
        Respond(ReadRegistersResponse.Skip(4).ToArray());

        Assert.Equal([0xAE41, 0x5652, 0x4340], await task);
    }

    [Fact]
    public async Task LeadingGarbage_IsSkipped()
    {
        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond([0xFF, 0x00, 0x42, .. ReadRegistersResponse]);

        Assert.Equal([0xAE41, 0x5652, 0x4340], await task);
    }

    [Fact]
    public async Task ResponseWithCorruptCrc_IsIgnored()
    {
        _client.ResponseTimeout = TimeSpan.FromMilliseconds(50);
        _client.Retries = 0;

        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        byte[] corrupted = ReadRegistersResponse.ToArray();
        corrupted[^1] ^= 0xFF;
        Respond(corrupted);

        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [Fact]
    public void ResponseWithNoRequestPending_IsIgnored()
    {
        Respond(ReadRegistersResponse);

        Assert.Empty(_sent);
    }

    [Fact]
    public async Task SecondRequest_WaitsForTheFirstToComplete()
    {
        var readTask = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        var writeTask = _client.WriteRegister(0x11, 0x0001, 0x0003);

        Assert.Single(_sent);
        Assert.Equal(ReadRegistersRequest, _sent[0]);

        Respond(ReadRegistersResponse);
        await readTask;

        await TestNetwork.WaitUntilAsync(() => _sent.Count == 2, 5, "second request was never sent");
        Assert.Equal(WriteRegisterRequest, _sent[1]);

        Respond(WriteRegisterRequest);
        await writeTask;
    }

    [Fact]
    public async Task ReadCoils_RejectsInvalidCounts()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadCoils(0x11, 0x0000, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadCoils(0x11, 0x0000, 2001));
    }

    [Fact]
    public async Task ReadHoldingRegisters_RejectsInvalidCounts()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadHoldingRegisters(0x11, 0x0000, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadHoldingRegisters(0x11, 0x0000, 126));
    }

    [Fact]
    public async Task WriteCoils_RejectsEmptyValues()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.WriteCoils(0x11, 0x0000, []));
    }

    [Fact]
    public async Task WriteRegisters_RejectsEmptyValues()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.WriteRegisters(0x11, 0x0000, []));
    }

    [Fact]
    public void Send_PassesBytesThroughToTheTransport()
    {
        byte[] payload = [0x01, 0x02, 0x03];

        _client.Send(payload);

        _mockClient.Verify(client => client.Send(payload), Times.Once);
    }

    [Fact]
    public void Send_PassesStringsThroughToTheTransport()
    {
        _client.Send("passthrough");

        _mockClient.Verify(client => client.Send("passthrough"), Times.Once);
    }

    [Fact]
    public void Defaults_MatchTheDocumentedValues()
    {
        var client = new AvCodersModbusRtuClient(TestFactory.CreateCommunicationClient().Object, "Defaults client");

        Assert.Equal(TimeSpan.FromSeconds(1), client.ResponseTimeout);
        Assert.Equal(TimeSpan.Zero, client.MinimumResponseTime);
        Assert.Equal(TimeSpan.FromMilliseconds(4), client.InterFrameDelay);
        Assert.Equal(2, client.Retries);
        Assert.Equal(0, client.AddressOffset);
        Assert.Equal(125, client.MaxRegistersPerRead);
        Assert.False(client.DiscardLocalEcho);
    }

    [Fact]
    public async Task AddressOffset_Zero_LeavesTheWireAddressUnchanged()
    {
        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond(ReadRegistersResponse);
        await task;

        Assert.Equal(ReadRegistersRequest, _sent[0]);
    }

    [Fact]
    public async Task AddressOffset_ShiftsTheWireAddressOnReads()
    {
        _client.AddressOffset = -1;
        byte[] expected = [0x11, 0x03, 0x00, 0x6A, 0x00, 0x03, 0x27, 0x47];

        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond(ReadRegistersResponse);
        await task;

        Assert.Equal(expected, _sent[0]);
    }

    [Fact]
    public async Task AddressOffset_ShiftsTheWireAddressOnWrites()
    {
        _client.AddressOffset = -1;
        byte[] expected = [0x11, 0x06, 0x00, 0x00, 0x00, 0x03, 0xCB, 0x5B];

        var task = _client.WriteRegister(0x11, 0x0001, 0x0003);
        Respond(expected);
        await task;

        Assert.Equal(expected, _sent[0]);
    }

    [Fact]
    public async Task AddressOffset_RejectsAddressesOutsideTheValidRange()
    {
        _client.AddressOffset = -1;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadHoldingRegisters(0x11, 0x0000, 0x0001));
    }

    [Fact]
    public async Task StaleResponseWithWrongByteCount_IsRejectedAndTheRealResponseAccepted()
    {
        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0002);
        Respond(ReadRegistersResponse);
        Respond(ReadTwoRegistersResponse);

        Assert.Equal([0x000A, 0x0102], await task);
        Assert.Equal(ReadTwoRegistersRequest, _sent[0]);
    }

    [Fact]
    public async Task StaleResponseWithWrongByteCount_TimesOutInsteadOfHanging()
    {
        _client.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        _client.Retries = 0;

        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0002);
        Respond(ReadRegistersResponse);

        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [Fact]
    public async Task MinimumResponseTime_DiscardsImplausiblyFastFrames()
    {
        _client.MinimumResponseTime = TimeSpan.FromSeconds(10);
        _client.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        _client.Retries = 0;

        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond(ReadRegistersResponse);

        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [Fact]
    public async Task MinimumResponseTime_AcceptsFramesThatArriveAfterIt()
    {
        _client.MinimumResponseTime = TimeSpan.FromMilliseconds(1);

        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        await Task.Delay(50);
        Respond(ReadRegistersResponse);

        Assert.Equal([0xAE41, 0x5652, 0x4340], await task);
    }

    [Fact]
    public async Task RaisedResponseTimeout_AllowsResponsesSlowerThanTheDefault()
    {
        _client.ResponseTimeout = TimeSpan.FromSeconds(5);

        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        await Task.Delay(1500);
        Respond(ReadRegistersResponse);

        Assert.Equal([0xAE41, 0x5652, 0x4340], await task);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task CancelledRequest_ThrowsPromptlyAndReleasesTheBus()
    {
        using var cts = new CancellationTokenSource();
        var readTask = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);

        var writeTask = _client.WriteRegister(0x11, 0x0001, 0x0003);
        await TestNetwork.WaitUntilAsync(() => _sent.Count == 2, 5, "the bus was not released after cancellation");
        Respond(WriteRegisterRequest);
        await writeTask;
    }

    [Fact]
    public async Task DiscardLocalEcho_IgnoresTheEchoedFrame()
    {
        _client.DiscardLocalEcho = true;
        _client.ResponseTimeout = TimeSpan.FromMilliseconds(100);
        _client.Retries = 0;

        var task = _client.WriteRegister(0x11, 0x0001, 0x0003);
        Respond(WriteRegisterRequest);

        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [Fact]
    public async Task DiscardLocalEcho_AcceptsTheResponseAfterTheEcho()
    {
        _client.DiscardLocalEcho = true;

        var task = _client.WriteRegister(0x11, 0x0001, 0x0003);
        Respond(WriteRegisterRequest);
        Respond(WriteRegisterRequest);

        await task;
    }

    [Fact]
    public async Task DiscardLocalEcho_Disabled_AcceptsAnEchoIdenticalResponseImmediately()
    {
        var task = _client.WriteRegister(0x11, 0x0001, 0x0003);
        Respond(WriteRegisterRequest);

        await task;
    }

    [Fact]
    public async Task MaxRegistersPerRead_CapsReadRequests()
    {
        _client.MaxRegistersPerRead = 25;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.ReadHoldingRegisters(0x11, 0x0000, 26));

        var task = _client.ReadHoldingRegisters(0x11, 0x006B, 0x0003);
        Respond(ReadRegistersResponse);
        await task;
    }

    [Fact]
    public void ConnectionStateChanges_AreMirroredFromTheTransport()
    {
        ConnectionState? reported = null;
        _client.ConnectionStateHandlers += state => reported = state;

        _mockClient.Object.ConnectionStateHandlers?.Invoke(ConnectionState.Connected);

        Assert.Equal(ConnectionState.Connected, _client.ConnectionState);
        Assert.Equal(ConnectionState.Connected, reported);
    }
}
