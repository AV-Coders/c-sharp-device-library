namespace AVCoders.Matrix;

public class NavMatrix(List<NavEncoder> inputs, List<NavDecoder> outputs, Navigator navigator, string name) 
    : VideoMatrix(outputs.Count, navigator.CommunicationClient, name)
{
    public override int NumberOfOutputs { get => outputs.Count; }
    public override int NumberOfInputs { get => inputs.Count; }
    public override bool RequiresOutputSpecification { get => true; }
    public override bool SupportsVideoBreakaway { get => false; }
    public override bool SupportsAudioBreakaway { get => false; }

    public override List<SyncStatus> GetInputs() => [..inputs];
    public override List<SyncStatus> GetOutputs() => [..outputs];

    public override void RouteVideo(int input, int output)
    {
        outputs[output].SetVideo(inputs[input].DeviceNumber);
    }

    public override void RouteAudio(int input, int output)
    {
        LogException(new InvalidOperationException("Audio breakaway not supported by NAV"));
    }

    public override void RouteAV(int input, int output)
    {
        outputs[output].SetInput(inputs[input]);
    }
    public override void PowerOn() { }

    public override void PowerOff() { }
}