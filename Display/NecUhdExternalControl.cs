using AVCoders.Core;

namespace AVCoders.Display;

public class NecUhdExternalControl : Display
{
    public static readonly ushort DefaultPort = 7142;
    public readonly CommunicationClient CommunicationClient;
    private readonly byte _displayId;
    private List<byte> _gather = new ();

    private const byte StartOfHeader = 0x01;
    private const byte ReservedByte = 0x30;
    private const byte Controller = 0x30;
    private const byte MessageTypeCommand = 0x41;
    private const byte MessageTypeCommandReply = 0x42;
    private const byte MessageTypeGetParameter = 0x43;
    private const byte MessageTypeGetParameterReply = 0x44;
    private const byte MessageTypeSetParameter = 0x45;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Delimiter = 0x0d;

    private static readonly Dictionary<Input, byte[]> InputDictionary = new()
    {
        { Input.Hdmi1, new byte[] { 0x31, 0x31 } },
        { Input.Hdmi2, new byte[] { 0x31, 0x32 } },
        { Input.Hdmi3, new byte[] { 0x38, 0x32 } },
        { Input.Hdmi4, new byte[] { 0x38, 0x33 } },
        { Input.DisplayPort, new byte[] { 0x30, 0x46 } },
    };

    public NecUhdExternalControl(CommunicationClient tcpClient, string name, Input? defaultInput, byte displayId = 0x2A) : base(InputDictionary.Keys.ToList(), name, defaultInput)
    {
        CommunicationClient = tcpClient;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        tcpClient.ResponseByteHandlers += HandleResponse;
        UpdateCommunicationState(CommunicationState.NotAttempted);
        _displayId = displayId;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected) 
            return;
        
        Thread.Sleep(1500);
        PollWorker.Restart();
    }

    protected override async Task Poll(CancellationToken token)
    {
        Log("Polling Power");
        //Power
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeCommand),
            new byte[] { Stx, 0x30, 0x31, 0x44, 0x36, Etx });
        await Task.Delay(1500, token);
        if (PowerState == PowerState.On)
        {
            //Input
            Log("Polling Input");
            PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeGetParameter),
                new byte[] { Stx, 0x30, 0x30, 0x36, 0x30, Etx });
            await Task.Delay(1500, token);
        }

        if (PowerState == PowerState.On)
        {
            // Volume
            Log("Polling Volume");
            PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeGetParameter),
                new byte[] { Stx, 0x30, 0x30, 0x36, 0x32, Etx });
        }
    }
    
    private int ConvertAsciiHexToNumber(byte[] asciiBytes)
    {
        if (asciiBytes.Length != 2)
            throw new ArgumentException("Array must contain exactly two bytes.");
        
        string hexString = System.Text.Encoding.ASCII.GetString(asciiBytes);
        int hexNumber = int.Parse(hexString, System.Globalization.NumberStyles.HexNumber);

        return hexNumber;
    }

    private void HandleResponse(byte[] response)
    {
        _gather.AddRange(response);

        while (_gather.Contains(0x0D))
        {
            int endIndex = _gather.IndexOf(0x0D) + 1;
            byte[] aResponsePayload = _gather.Take(endIndex).ToArray();
            _gather = _gather.Skip(endIndex).ToList();

            ProcessResponse(aResponsePayload);
        }
    }

    private void ProcessResponse(byte[] response)
    {
        Log($"Response: {BitConverter.ToString(response)}");

        if (response[4] == MessageTypeCommandReply)
        {
            if (response[12] != 0x44 || response[13] != 0x36)
                return; // Only care about power responses

            switch (response[23])
            {
                case 0x34:
                    PowerState = PowerState.Off;
                    break;
                case 0x31:
                    PowerState = PowerState.On;
                    break;
            }

            ProcessPowerResponse();
        }
        else if (response[4] == MessageTypeGetParameterReply)
        {
            if (response[8] != 0x30 || response[9] != 0x30 || response[10] != 0x30 || response[11] != 0x30 || response[12] != 0x36)
                return;

            switch (response[13])
            {
                case 0x30: // Input response
                    Input = ConvertAsciiHexToNumber(new[] { response[22], response[23] }) switch
                    {
                        0x11 => Input.Hdmi1,
                        0x12 => Input.Hdmi2,
                        0x82 => Input.Hdmi3,
                        0x83 => Input.Hdmi4,
                        0x0F => Input.DisplayPort,
                        _ => Input.Unknown
                    };
                    ProcessInputResponse();
                    break;
                case 0x32: // Volume Response
                    Volume = ConvertAsciiHexToNumber(new[] { response[22], response[23] });
                    VolumeLevelHandlers?.Invoke(Volume);
                    break;
            }
        }
    }

    private void PrepareAndSendCommand(List<byte> header, byte[] payload)
    {
        AddPayloadLengthToCommand(header, payload.Length);
        AddPayloadToCommand(header, payload);
        AddChecksumToCommand(header);
        WrapAndSendCommand(header);
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
        byte[] payload =
            { Stx, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, InputDictionary[input][0], InputDictionary[input][1], Etx };
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
        byte[] payload =
            { Stx, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, (byte)(state == MuteState.On ? 0x31 : 0x32), Etx };
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeSetParameter);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }
}