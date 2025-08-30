namespace AVCoders.Core;

public enum CommandStringFormat
{
    Unknown,
    Ascii,
    Hex,
}

public enum CommunicationState
{
    Unknown,
    NotAttempted,
    Okay,
    Error
}
public enum ConnectionState
{
    Unknown, // Moq will set the uninitialised instances of enums to the first option.
    Connected,
    Disconnected,
    Connecting,
    Disconnecting,
    Error,
    Idle
}

public enum PowerState
{
    Unknown,
    On,
    Off,
    Warming,
    Cooling,
    Rebooting
}

public enum MuteState
{
    Unknown,
    On,
    Off
}

public enum TransportState
{
    Unknown,
    Recording,
    RecordingPaused,
    Stopped,
    Stopping,
    PreparingToRecord,
    Playing,
    Paused,
    Ready,
    
}

public enum MediaState
{
    Unknown,
    Inserted,
    Ejected
}

public enum PollTask
{
    None,
    Power,
    Input,
    AudioMute,
    VideoMute,
}

public enum SerialBaud
{
    Rate110,
    Rate300,
    Rate1200,
    Rate2400,
    Rate4800,
    Rate9600,
    Rate19200,
    Rate38400,
    Rate57600,
    Rate115200
}

public enum SerialParity
{
    None,
    Even,
    Odd
}

public enum SerialProtocol
{
    Rs232,
    Rs422,
    Rs485
}

public enum SerialHardwareHandshake
{
    None,
    Rts,
    Cts,
    RtsAndCts,
}

public enum SerialSoftwareHandshake
{
    None,
    Xon,
    XonT,
    XonR,
}

public enum SerialDataBits
{
    DataBits7,
    DataBits8,
}

public enum SerialStopBits
{
    Bits1,
    Bits2,
}