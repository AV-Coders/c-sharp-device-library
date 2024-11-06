using AVCoders.Core;

namespace AVCoders.Display;

public class NecUhdExternalControl : Display
{
    public static readonly ushort DefaultPort = 7142;
    public readonly CommunicationClient CommunicationClient;
    private readonly byte _displayId;

    private const byte StartOfHeader = 0x01;
    private const byte ReservedByte = 0x30;
    private const byte Controller = 0x30;
    private const byte MessageTypeCommand = 0x41;
    private const byte MessageTypeSetParameter = 0x45;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Delimiter = 0x0d;
    private static readonly Dictionary<Input, byte[]> InputDictionary = new()
    {
        { Input.Hdmi1, new byte[] { 0x31, 0x31 } },
        { Input.Hdmi2, new byte[] { 0x31, 0x32 } },
        { Input.Hdmi3, new byte[] { 0x38, 0x32 } },
        { Input.Hdmi4, new byte[] { 0x38, 0x33 } }
    };

    public NecUhdExternalControl(CommunicationClient tcpClient, string name, byte displayId = 0x2A) : base(InputDictionary.Keys.ToList(), name)
    {
        CommunicationClient = tcpClient;
        _displayId = displayId;
        ConfigureCommClient();
    }

    protected override Task Poll(CancellationToken token) => PollWorker.Stop();

    private void ConfigureCommClient()
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        UpdateCommunicationState(CommunicationState.NotAttempted);
    }

    private void HandleResponse(string response)
    {
        //Todo: Handle the response
        // Don't forget to use the methods in the base class to force states
    }

    private List<byte> GetCommandHeaderWithoutSoh(byte commandType)
    {
        return new List<byte> { ReservedByte, _displayId, Controller, commandType };
    }

    private void WrapAndSendCommand(List<byte> theCommand)
    {
        // add soh
        theCommand.Insert(0, StartOfHeader);
        theCommand.Add(Delimiter);
        CommunicationClient.Send(theCommand.ToArray());
    }

    private void AddPayloadLengthToCommand(List<byte> command, int payloadLength)
    {
        foreach (var theByte in Bytes.AsciiRepresentationOfHexEquivalentOf(payloadLength, 2))
        {
            command.Add(theByte);
        }
    }

    private void AddPayloadToCommand(List<byte> command, byte[] payload)
    {
        foreach (byte theByte in payload)
        {
            command.Add(theByte);
        }
    }

    private void AddChecksumToCommand(List<byte> command)
    {
        command.Add(Bytes.XorAList(command));
    }

    protected override void DoPowerOn()
    {
        byte[] payload = { Stx, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x31, Etx };
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeCommand);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoPowerOff()
    {
        byte[] payload = { Stx, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x34, Etx };
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeCommand);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoSetInput(Input input)
    {
        byte[] payload = { Stx, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, InputDictionary[input][0], InputDictionary[input][1], Etx };
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeSetParameter);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoSetVolume(int percentage)
    {
        byte[] volumeBytes = Bytes.AsciiRepresentationOfHexEquivalentOf(percentage, 2);
        byte[] payload = { Stx, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, volumeBytes[0], volumeBytes[1], Etx };
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeSetParameter);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoSetAudioMute(MuteState state)
    {
        byte[] payload = { Stx, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, (byte)(state == MuteState.On? 0x31:0x32), Etx };
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeSetParameter);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }
}