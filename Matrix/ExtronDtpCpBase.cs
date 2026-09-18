using AVCoders.Core;

namespace AVCoders.Matrix;

/// <summary>
/// Shared plumbing for the Extron DTP CrossPoint family: SIS framing, single-tie routing,
/// HDCP status decoding, verbose-mode setup on connect and the keepalive poll. Model-specific
/// discovery, naming, hotplug and signal-presence handling live in the concrete drivers.
/// </summary>
public abstract class ExtronDtpCpBase : VideoMatrix
{
    public static readonly ushort DefaultPort = 22023;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    protected const string EscapeHeader = "\x1b";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private readonly ThreadWorker _pollWorker;

    protected ExtronDtpCpBase(CommunicationClient communicationClient, int numberOfOutputs, string name)
        : base(numberOfOutputs, communicationClient, name)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(CommunicationClient.ConnectionState);
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
    }

    // The SSH client delivers one line per callback but the TCP client delivers raw chunks,
    // which can hold several responses. Split so a prefix match never misses the second one.
    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                ProcessResponse(line);
        }
    }

    /// <summary>Handles one response line. The trailing CR/LF has already been removed.</summary>
    protected abstract void ProcessResponse(string response);

    /// <summary>Sent every 20 seconds while connected, as a keepalive and status refresh.</summary>
    protected abstract void SendPoll();

    private Task Poll(CancellationToken arg)
    {
        if (CommunicationClient.ConnectionState == ConnectionState.Connected)
            SendPoll();
        return Task.CompletedTask;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        WrapAndSendCommand("3CV");
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        SendCommand("I"); // The model response drives endpoint discovery and resets all data
    }

    protected void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    protected void SendCommand(string command)
    {
        try
        {
            CommunicationClient.Send(command);
            CommunicationState = CommunicationState.Okay;
        }
        catch (Exception e)
        {
            LogException(e);
            CommunicationState = CommunicationState.Error;
        }
    }

    /// <summary>
    /// Splits an HdcpI / HdcpO response into its endpoint token (e.g. "1" or "5A") and status value.
    /// </summary>
    protected static bool TryParseHdcpResponse(string response, out string endpoint, out string value)
    {
        endpoint = string.Empty;
        value = string.Empty;
        if (response.Length <= 5)
            return false;
        var parts = response[5..].TrimEnd('\r').Split('*');
        if (parts.Length != 2)
            return false;
        endpoint = parts[0];
        value = parts[1];
        return true;
    }

    /// <summary>Decodes an input HDCP value; the tables are shared with the other Extron SIS drivers.</summary>
    protected static (ConnectionState Connection, HdcpStatus Hdcp) DecodeInputHdcp(string value) => ExtronSisHdcp.DecodeInput(value);

    /// <summary>Decodes an output HDCP value; the tables are shared with the other Extron SIS drivers.</summary>
    protected static (ConnectionState Connection, HdcpStatus Hdcp) DecodeOutputHdcp(string value) => ExtronSisHdcp.DecodeOutput(value);

    public override void RouteAV(int input, int output)
    {
        SendCommand(output == 0 ? $"{input}*!" : $"{input}*{output}!");
        AddEvent(EventType.Input, $"Switched output {output} to input {input}");
    }

    public override void RouteVideo(int input, int output)
    {
        SendCommand(output == 0 ? $"{input}*%" : $"{input}*{output}%");
        AddEvent(EventType.Input, $"Switched video output {output} to input {input}");
    }

    public override void RouteAudio(int input, int output)
    {
        SendCommand(output == 0 ? $"{input}*$" : $"{input}*{output}$");
        AddEvent(EventType.Input, $"Switched audio output {output} to input {input}");
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override bool RequiresOutputSpecification => true;
    public override bool SupportsVideoBreakaway => true;
}
