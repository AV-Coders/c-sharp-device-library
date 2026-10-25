using AVCoders.Core;

namespace AVCoders.Display;

public class NecUhdExternalControl : Display
{
    public static readonly ushort DefaultPort = 7142;
    private readonly byte _displayId;
    private List<byte> _gather = [];

    private const byte StartOfHeader = 0x01;
    private const byte ReservedByte = 0x30;
    private const byte Controller = 0x30;
    private const byte MessageTypeCommand = 0x41;
    private const byte MessageTypeCommandReply = 0x42;
    private const byte MessageTypeGetParameter = 0x43;
    private const byte MessageTypeGetParameterReply = 0x44;
    private const byte MessageTypeSetParameter = 0x45;
    private const byte MessageTypeSetParameterReply = 0x46;
    private const byte FirstMonitorId = 0x41;
    private const byte LastMonitorId = 0xA4;
    private const int SourceIndex = 3;
    private const int ReplyLength = 27;
    private const byte PowerStatusOn = 0x31;
    private const byte PowerStatusStandby = 0x32;
    private const byte PowerStatusOff = 0x34;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Delimiter = 0x0d;

    private static readonly Dictionary<Input, byte[]> InputDictionary = new()
    {
        { Input.Hdmi1, [0x31, 0x31] },
        { Input.Hdmi2, [0x31, 0x32] },
        { Input.Hdmi3, [0x38, 0x32] },
        { Input.Hdmi4, [0x38, 0x33] },
        { Input.DisplayPort, [0x30, 0x46] },
    };

    public NecUhdExternalControl(CommunicationClient comms, string name, Input? defaultInput, byte displayId = 0x2A) 
        : base(InputDictionary.Keys.ToList(), name, defaultInput, comms, CommandStringFormat.Hex)
    {
        CommunicationClient.ResponseByteHandlers += HandleResponse;
        _displayId = displayId;
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;

        Task.Delay(1500).ContinueWith(_ => PollWorker.Restart());
    }

    protected override async Task DoPoll(CancellationToken token)
    {
        if (CommunicationClient.ConnectionState != ConnectionState.Connected)
        {
            using (PushProperties("DoPoll"))
                LogDebug("Not polling");
            return;
        }

        //Power
        PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeCommand), [Stx, 0x30, 0x31, 0x44, 0x36, Etx]);
        await Task.Delay(1500, token);
        if (PowerState == PowerState.On)
        {
            //Input
            PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeGetParameter), [Stx, 0x30, 0x30, 0x36, 0x30, Etx]);
            await Task.Delay(1500, token);
        }

        if (PowerState == PowerState.On)
        {
            // Volume
            PrepareAndSendCommand(GetCommandHeaderWithoutSoh(MessageTypeGetParameter), [Stx, 0x30, 0x30, 0x36, 0x32, Etx]);
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
        using (PushProperties())
        {
            _gather.AddRange(response);

            while (_gather.Contains(0x0D))
            {
                int endIndex = _gather.IndexOf(0x0D) + 1;
                byte[] aResponsePayload = _gather.Take(endIndex).ToArray();
                _gather = _gather.Skip(endIndex).ToList();

                ProcessResponse(aResponsePayload);
            }

            if (_gather.Count > 1024)
            {
                LogWarning("Gather buffer exceeded 1024 bytes, clearing");
                _gather.Clear();
            }
        }
    }

    private void ProcessResponse(byte[] response)
    {
        if (response.Length < ReplyLength || !IsFromThisMonitor(response))
        {
            LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
            return;
        }

        if (response[4] == MessageTypeCommandReply)
        {
            if (response[12] != 0x44 || response[13] != 0x36)
            {
                LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
                return;
            }

            if (response[10] != 0x30 || response[11] != 0x30)
            {
                ReportErrorResult(response);
                return;
            }

            CommunicationState = CommunicationState.Okay;
            switch (response[23])
            {
                case PowerStatusOff:
                case PowerStatusStandby:
                    PowerState = PowerState.Off;
                    break;
                case PowerStatusOn:
                    PowerState = PowerState.On;
                    break;
                default:
                    LogWarning("Unknown power status in {Response}", Convert.ToHexString(response));
                    break;
            }

            ProcessPowerResponse();
        }
        else if (response[4] is MessageTypeGetParameterReply or MessageTypeSetParameterReply)
        {
            if (response[8] != 0x30 || response[9] != 0x30)
            {
                ReportErrorResult(response);
                return;
            }

            if (response[10] != 0x30 || response[11] != 0x30 || response[12] != 0x36)
            {
                LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
                return;
            }

            CommunicationState = CommunicationState.Okay;
            switch (response[13])
            {
                case 0x30: // Input response
                    Input = ConvertAsciiHexToNumber([response[22], response[23]]) switch
                    {
                        0x11 => Input.Hdmi1,
                        0x12 => Input.Hdmi2,
                        0x82 => Input.Hdmi3,
                        0x83 => Input.Hdmi4,
                        0x0F => Input.DisplayPort,
                        _ => Input.Unknown
                    };
                    if (Input == Input.Unknown)
                        LogWarning("Unknown input code in {Response}", Convert.ToHexString(response));
                    ProcessInputResponse();
                    break;
                case 0x32: // Volume Response
                    Volume = ConvertAsciiHexToNumber([response[22], response[23]]);
                    break;
            }
        }
        else
        {
            LogDebug("Ignored reply {Response}", Convert.ToHexString(response));
        }
    }

    private bool IsFromThisMonitor(byte[] response) =>
        _displayId is < FirstMonitorId or > LastMonitorId || response[SourceIndex] == _displayId;

    private void ReportErrorResult(byte[] response)
    {
        LogWarning("The display returned an error result: {Response}", Convert.ToHexString(response));
        AddEvent(EventType.Error, "The display returned an error result");
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
        return [ReservedByte, _displayId, Controller, commandType];
    }

    private void WrapAndSendCommand(List<byte> theCommand)
    {
        // add soh
        theCommand.Insert(0, StartOfHeader);
        theCommand.Add(Delimiter);
        try
        {
            CommunicationClient.Send(theCommand.ToArray());
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
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
        byte[] payload = [Stx, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x31, Etx];
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeCommand);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoPowerOff()
    {
        byte[] payload = [Stx, 0x43, 0x32, 0x30, 0x33, 0x44, 0x36, 0x30, 0x30, 0x30, 0x34, Etx];
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeCommand);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoSetInput(Input input)
    {
        byte[] payload =
            [Stx, 0x30, 0x30, 0x36, 0x30, 0x30, 0x30, InputDictionary[input][0], InputDictionary[input][1], Etx];
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeSetParameter);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoSetVolume(int percentage)
    {
        byte[] volumeBytes = Bytes.AsciiRepresentationOfHexEquivalentOf(percentage, 2);
        byte[] payload = [Stx, 0x30, 0x30, 0x36, 0x32, 0x30, 0x30, volumeBytes[0], volumeBytes[1], Etx];
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeSetParameter);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }

    protected override void DoSetAudioMute(MuteState state)
    {
        byte[] payload =
            [Stx, 0x30, 0x30, 0x38, 0x44, 0x30, 0x30, 0x30, (byte)(state == MuteState.On ? 0x31 : 0x32), Etx];
        List<byte> fullCommand = GetCommandHeaderWithoutSoh(MessageTypeSetParameter);
        AddPayloadLengthToCommand(fullCommand, payload.Length);
        AddPayloadToCommand(fullCommand, payload);
        AddChecksumToCommand(fullCommand);
        WrapAndSendCommand(fullCommand);
    }
}