using AVCoders.Core;

namespace AVCoders.Matrix;

/// <summary>
/// Shared plumbing for the Extron DTP CrossPoint family: SIS framing, single-tie routing,
/// HdcpI / HdcpO reply splitting, verbose-mode setup on connect and the keepalive poll. Model-specific
/// discovery, naming, hotplug and signal-presence handling live in the concrete drivers.
/// </summary>
public abstract class ExtronDtpCpBase : ExtronSisMatrix
{
    public static readonly ushort DefaultPort = 22023;
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    protected const string EscapeHeader = "\x1b";
    private static readonly char[] LineSeparators = ['\r', '\n'];
    private static readonly TimeSpan ConnectStepGap = TimeSpan.FromMilliseconds(200);
    private static readonly ExtronSisCommand VerboseMode = new($"{EscapeHeader}3CV\r", "Vrb");
    private static readonly ExtronSisCommand ModelQuery = new("I", "Inf00*");
    private readonly ThreadWorker _pollWorker;
    private readonly object _spacedSendsLock = new();
    private Task _spacedSends = Task.CompletedTask;

    protected ExtronDtpCpBase(CommunicationClient communicationClient, int numberOfOutputs, string name)
        : this(communicationClient, numberOfOutputs, name, null)
    {
    }

    private protected ExtronDtpCpBase(CommunicationClient communicationClient, int numberOfOutputs, string name, TimeProvider? timeProvider)
        : base(numberOfOutputs, communicationClient, name, timeProvider)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        PowerState = PowerState.Unknown;
        CommunicationState = CommunicationState.NotAttempted;
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(CommunicationClient.ConnectionState);
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true, TimeProvider);
        _pollWorker.Restart();
    }

    // The SSH client delivers one line per callback but the TCP client delivers raw chunks,
    // which can hold several responses. Split so a prefix match never misses the second one.
    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            foreach (var line in response.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
                using (TakeReply(line.Trim()))
                using (PushProperties("ProcessResponse"))
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
        ForgetCommands();
        if (connectionState != ConnectionState.Connected)
            return;
        lock (_spacedSendsLock)
            _spacedSends = _spacedSends.ContinueWith(async _ =>
            {
                try
                {
                    await Task.Delay(ConnectStepGap, TimeProvider);
                    Send(VerboseMode);
                    await Task.Delay(ConnectStepGap, TimeProvider);
                    Send(ModelQuery);
                }
                catch (Exception e)
                {
                    LogException(e, "The connection sequence failed");
                }
            }, TaskScheduler.Default).Unwrap();
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

    public override void RouteAV(int input, int output)
    {
        Send(new ExtronSisCommand(output == 0 ? $"{input}*!" : $"{input}*{output}!", new ExtronSisTie(input, output, ExtronSisTie.VideoAndAudio)));
        AddEvent(EventType.Input, $"Switched output {output} to input {input}");
    }

    public override void RouteVideo(int input, int output)
    {
        Send(new ExtronSisCommand(output == 0 ? $"{input}*%" : $"{input}*{output}%", new ExtronSisTie(input, output, ExtronSisTie.Video)));
        AddEvent(EventType.Input, $"Switched video output {output} to input {input}");
    }

    public override void RouteAudio(int input, int output)
    {
        Send(new ExtronSisCommand(output == 0 ? $"{input}*$" : $"{input}*{output}$", new ExtronSisTie(input, output, ExtronSisTie.Audio)));
        AddEvent(EventType.Input, $"Switched audio output {output} to input {input}");
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override bool RequiresOutputSpecification => true;
    public override bool SupportsVideoBreakaway => true;
}
