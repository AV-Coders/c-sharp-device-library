using System.Text;
using AVCoders.Core;
using Serilog;

namespace AVCoders.Matrix;

public delegate void EndpointArrayChangedHandler(List<ExtronMatrixEndpoint> endpoints);

public class ExtronDtpCpxx : VideoMatrix
{
    public static readonly ushort DefaultPort = 22023;
    private readonly ThreadWorker _pollWorker;
    public readonly List<ExtronMatrixOutput> ComposedOutputs = [];
    public readonly List<ExtronMatrixInput> Inputs = [];
    public static readonly SerialSpec DefaultSerialSpec =
        new (SerialBaud.Rate9600, SerialParity.None, SerialDataBits.DataBits8, SerialStopBits.Bits1, SerialProtocol.Rs232);
    public List<ExtronMatrixEndpoint> Outputs => ComposedOutputs
        .SelectMany(output => new[] { output.Primary, output.Secondary })
        .Where(endpoint => endpoint.InUse)
        .ToList();

    private readonly string EscapeHeader = "\x1b";
    
    public List<SyncStatus> InputsAndOutputs
    {
        get
        {
            var combined = new List<SyncStatus>((Inputs?.Count ?? 0) + (Outputs?.Count ?? 0));
            if (Inputs is { Count: > 0 })
                combined.AddRange(Inputs);
            if (Outputs is { Count: > 0 })
                combined.AddRange(Outputs);
            return combined;
        }
    }

    public ExtronDtpCpxx(CommunicationClient communicationClient, int numberOfOutputs, string name) 
        : base(numberOfOutputs, communicationClient, name)
    {
        CommunicationClient.ResponseHandlers += HandleResponse;
        PowerState = PowerState.Unknown;
        UpdateCommunicationState(CommunicationState.NotAttempted);
        CommunicationClient.ConnectionStateHandlers += HandleConnectionState;
        HandleConnectionState(CommunicationClient.ConnectionState);
        _pollWorker = new ThreadWorker(Poll, TimeSpan.FromSeconds(20), true);
        _pollWorker.Restart();
    }

    private void HandleResponse(string response)
    {
        using (PushProperties())
        {
            if (response.StartsWith("E13"))
                return;

            if (response.StartsWith("Ityp"))
            {
                var parts = response.TrimEnd('\r').Split('*');
                if (parts.Length != 2) return;

                var inputNumber = int.Parse(parts[0].Substring(4)) - 1;
                var status = parts[1];

                if (inputNumber >= 0 && inputNumber < Inputs.Count)
                {
                    var connectionStatus = status == "0" ? ConnectionState.Disconnected : ConnectionState.Connected;

                    Inputs[inputNumber].SetInputStatus(connectionStatus);
                }
            }
            else if (response.StartsWith("HdcpI"))
            {
                var parts = response.TrimStart('H', 'd', 'c', 'p', 'I').TrimEnd('\r').Split('*');
                int inputNumber = int.Parse(parts[0].TakeWhile(char.IsDigit).ToArray());
                if (inputNumber <= 0 || inputNumber > ComposedOutputs.Count)
                    return;

                ConnectionState connectionStatus = ConnectionState.Unknown;
                HdcpStatus hdcpStatus = HdcpStatus.Unknown;
                switch (parts[1])
                {
                    case "0":
                        connectionStatus = ConnectionState.Disconnected;
                        hdcpStatus = HdcpStatus.Unknown;
                        break;
                    case "1":
                        connectionStatus = ConnectionState.Connected;
                        hdcpStatus = HdcpStatus.NotSupported;
                        break;
                    case "2":
                        connectionStatus = ConnectionState.Connected;
                        hdcpStatus = HdcpStatus.Active;
                        break;
                }

                Log.Information("Setting output {outputNumber} B as {status}", inputNumber,
                    connectionStatus.ToString());
                Inputs[inputNumber - 1].SetInputStatus(connectionStatus);
                Inputs[inputNumber - 1].SetInputHdcpStatus(hdcpStatus);
            }
            else if (response.StartsWith("HdcpO"))
            {
                var parts = response.TrimStart('H', 'd', 'c', 'p', 'O').TrimEnd('\r').Split('*');
                int outputNumber = int.Parse(parts[0].TakeWhile(char.IsDigit).ToArray());
                if (outputNumber <= 0 || outputNumber > ComposedOutputs.Count)
                    return;

                ConnectionState connectionStatus = ConnectionState.Unknown;
                HdcpStatus hdcpStatus = HdcpStatus.Unknown;
                switch (parts[1])
                {
                    case "0":
                        connectionStatus = ConnectionState.Disconnected;
                        hdcpStatus = HdcpStatus.Unknown;
                        break;
                    case "1":
                        connectionStatus = ConnectionState.Connected;
                        hdcpStatus = HdcpStatus.NotSupported;
                        break;
                    case "2":
                        connectionStatus = ConnectionState.Connected;
                        hdcpStatus = HdcpStatus.Available;
                        break;
                    case "3":
                        connectionStatus = ConnectionState.Connected;
                        hdcpStatus = HdcpStatus.Active;
                        break;
                }

                if (parts[0].Contains('B'))
                {
                    Log.Information("Setting output {outputNumber} B as {status}", outputNumber,
                        connectionStatus.ToString());
                    ComposedOutputs[outputNumber - 1].Secondary.SetOutputStatus(connectionStatus);
                    ComposedOutputs[outputNumber - 1].Secondary.SetOutputHdcpStatus(hdcpStatus);
                }
                else
                {
                    Log.Information("Setting output {outputNumber} A as {status}", outputNumber,
                        connectionStatus.ToString());
                    ComposedOutputs[outputNumber - 1].Primary.SetOutputStatus(connectionStatus);
                    ComposedOutputs[outputNumber - 1].Primary.SetOutputHdcpStatus(hdcpStatus);
                }
            }
            else if (response.StartsWith("Hplg"))
            {
                var outputNumber = response.Substring(5).TrimEnd();
                WrapAndSendCommand($"O{outputNumber}HDCP");
            }
            else if (response.StartsWith("Nmi"))
            {
                var parts = response.TrimEnd('\r').Split(',');
                if (parts.Length != 2) return;

                var inputNumber = int.Parse(parts[0].Substring(3)) - 1;
                var name = parts[1];

                if (inputNumber >= 0 && inputNumber < Inputs.Count)
                {
                    Inputs[inputNumber].SetName(name);
                }
            }
            else if (response.StartsWith("Nmo"))
            {
                var parts = response.TrimEnd('\r').Split(',');
                if (parts.Length != 2) return;

                var outputNumber = int.Parse(parts[0].Substring(3)) - 1;
                var name = parts[1];

                if (outputNumber >= 0 && outputNumber < ComposedOutputs.Count)
                {
                    ComposedOutputs[outputNumber].SetName(name);
                }
            }
            else if (response.StartsWith("Frq00"))
            {
                var inputString = response.Split(' ')[1].TrimEnd('\r');
                var inputCount = inputString.Length;
                Inputs.Clear();

                while (Inputs.Count < inputCount)
                {
                    var index = Inputs.Count + 1;
                    var input = new ExtronMatrixInput($"Input {index}", index);
                    Inputs.Add(input);
                }
            }
            else if (response.StartsWith("Inf00*DTPCP"))
            {
                var digits = response.Remove(0, 11).TrimEnd('\r');
                if(digits.EndsWith(" 4K"))
                    digits = digits.Remove(digits.Length - 3);
                int inputCount = 0;
                int outputCount = 0;
                switch (digits.Length)
                {
                    case 2:
                        inputCount = int.Parse(digits[..1]);
                        outputCount = int.Parse(digits[^1].ToString());
                        break;
                    case 3:
                        inputCount = int.Parse(digits[..2]);
                        outputCount = int.Parse(digits[^1].ToString());
                        break;
                    default:
                        throw new ArgumentOutOfRangeException("The model number digits are unsupported");
                }

                if (inputCount == 0 || outputCount == 0)
                    throw new ArgumentOutOfRangeException(
                        "Unable to determine the number of inputs or outputs. Please check the model number and try again.");

                if (Inputs.Count > inputCount)
                    Inputs.Clear();

                if (ComposedOutputs.Count > outputCount)
                    ComposedOutputs.Clear();

                while (Inputs.Count < inputCount)
                {
                    var index = Inputs.Count + 1;
                    var input = new ExtronMatrixInput($"Input {index}", index);
                    Inputs.Add(input);
                    WrapAndSendCommand($"{index}NI");
                    WrapAndSendCommand($"I{index}HDCP");
                }

                while (ComposedOutputs.Count < outputCount)
                {
                    var index = ComposedOutputs.Count + 1;
                    var output = new ExtronMatrixOutput($"Output {index}", index);
                    ComposedOutputs.Add(output);
                    WrapAndSendCommand($"{index}NO");
                    WrapAndSendCommand($"O{index}HDCP");
                    WrapAndSendCommand($"O{index}AHDCP");
                    WrapAndSendCommand($"O{index}BHDCP");
                }
            }
        }
    }

    private Task Poll(CancellationToken arg)
    {
        if(CommunicationClient.ConnectionState == ConnectionState.Connected)
            WrapAndSendCommand("0TC");
        return Task.CompletedTask;
    }

    private void HandleConnectionState(ConnectionState connectionState)
    {
        if (connectionState != ConnectionState.Connected)
            return;
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        WrapAndSendCommand("3CV");
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        SendCommand("I"); // To get the input and output count, resets the lists and all data
    }

    private void WrapAndSendCommand(string command) => SendCommand($"{EscapeHeader}{command}\r");

    private void SendCommand(string command)
    {
        try
        {
            CommunicationClient.Send(command);
            UpdateCommunicationState(CommunicationState.Okay);
        }
        catch (Exception e)
        {
            LogException(e);
            UpdateCommunicationState(CommunicationState.Error);
        }
    }

    public override void RouteAV(int input, int output) => SendCommand(output == 0 ? $"{input}*!" : $"{input}*{output}!");
    
    public void RouteAV(int input, List<int> outputs)
    {
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}!"));
        sb.Append('\r');
        SendCommand(sb.ToString());
    }

    public override void PowerOn() { }

    public override void PowerOff() { }

    public override void RouteVideo(int input, int output) => SendCommand(output == 0 ? $"{input}*%" : $"{input}*{output}%");
    
    public void RouteVideo(int input, List<int> outputs)
    {
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}%"));
        sb.Append('\r');
        SendCommand(sb.ToString());
    }

    public override void RouteAudio(int input, int output) => SendCommand(output == 0 ? $"{input}*$" : $"{input}*{output}$");
    
    public void RouteAudio(int input, List<int> outputs)
    {
        if (input <= 0)
            return;
        if (outputs.Count == 0)
            return;
        var sb = new StringBuilder(EscapeHeader);
        sb.Append("+Q");
        outputs.ForEach(o => sb.Append($"{input}*{o}$"));
        sb.Append('\r');
        SendCommand(sb.ToString());
    }

    public void SetSyncTimeout(int seconds, int output)
    {
        if (seconds < 502)
        {
            SendCommand($"\u001bT{seconds}*{output}SSAV\u0027");
        }
    }
}