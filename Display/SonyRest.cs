using AVCoders.Core;

namespace AVCoders.Display;

public record SonyApp(string Title, string Uri, string Icon);

public class SonyRest : Display
{
    private readonly RestComms _restComms;
    private readonly Uri _systemUri = new ("/sony/system", UriKind.Relative);
    private readonly Uri _avContentUri = new ("/sony/avContent", UriKind.Relative);
    private readonly Uri _audioUri = new ("/sony/audio", UriKind.Relative);

    private static readonly Dictionary<Input, int> InputDictionary = new()
    {
        { Input.Hdmi1, 1 },
        { Input.Hdmi2, 2 },
        { Input.Hdmi3, 3 },
        { Input.Hdmi4, 4 }
    };
    
    public SonyRest(string name, Input? defaultInput, RestComms communicationClient, string preSharedKey, int pollTime = 23) :
        base(InputDictionary.Keys.ToList(), name, defaultInput, communicationClient, CommandStringFormat.Ascii, pollTime)
    {
        _restComms = communicationClient;
        _restComms.AddDefaultHeader("X-Auth-PSK", preSharedKey);
    }

    protected override void HandleConnectionState(ConnectionState connectionState)
    {
    }

    protected override Task DoPoll(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    protected override void DoPowerOn()
    {
        _restComms.Post(_systemUri, "{\"method\": \"setPowerStatus\",\"id\": 55,\"params\": [{\"status\": true}],\"version\": \"1.0\"}", "application/json");
    }

    protected override void DoPowerOff()
    {
        _restComms.Post(_systemUri, "{\"method\": \"setPowerStatus\",\"id\": 55,\"params\": [{\"status\": false}],\"version\": \"1.0\"}", "application/json");
    }

    protected override void DoSetInput(Input input)
    {
        _restComms.Post(_avContentUri,
            $"{{\"method\": \"setPlayContent\",\"id\": 101,\"params\": [{{\"uri\": \"extInput:hdmi?port={InputDictionary[input]}\"}}],\"version\": \"1.0\"\n}}",
            "application/json");
    }

    protected override void DoSetVolume(int percentage)
    {
        _restComms.Post(_audioUri,
            $"{{\"method\": \"setAudioVolume\",\"id\": 98,\"params\": [{{\"volume\": \"{percentage}\",\"ui\": \"off\",\"target\": \"speaker\"}}],\"version\": \"1.2\"\n}}",
            "application/json");
    }

    protected override void DoSetAudioMute(MuteState state)
    {
        string muteValue = state switch
        {
            MuteState.On => "true",
            MuteState.Off => "false",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
        _restComms.Post(_audioUri,
            $"{{\"method\": \"setAudioMute\",\"id\": 601,\"params\": [{{\"status\": {muteValue}}}],\"version\": \"1.0\"\n}}",
            "application/json");
    }
}