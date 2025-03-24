using AVCoders.Core;
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

namespace AVCoders.MediaPlayer;

public class TriplePlay : MediaPlayer, ISetTopBox
{
    private readonly int _deviceId;
    private readonly string _host;
        
    public TriplePlay(int deviceId, string host)
    {
        _deviceId = deviceId;
        _host = host;
    }

    private async Task Get(string uri)
    {
        // Use HttpClient - HttpWebRequest seems to break after three requests.
        using (HttpClient httpClient = new HttpClient())
        {
            try
            {
                Verbose($"Sending request: {uri}");
                await httpClient.GetAsync(uri);
            }
            catch (Exception e)
            {
                Verbose(e.Message);
            }
        }
    }

    private string GenerateCommandString(string command)
    {
        return
            $"http://{_host}/triplecare/JsonRpcHandler.php?call={{\"jsonrpc\":\"2.0\",\"method\":\"{command}\",\"params\":[{_deviceId}]}}";
    }

    private string GenerateCommandStringWithArgument(string command, string param)
    {
        return
            $"http://{_host}/triplecare/JsonRpcHandler.php?call={{\"jsonrpc\":\"2.0\",\"method\":\"{command}\",\"params\":[{_deviceId},{param}]}}";
    }

    public void ChannelUp()
    {
        Get(GenerateCommandString("ChannelUp"));
    }

    public void ChannelDown()
    {
        Get(GenerateCommandString("ChannelDown"));
    }

    public void SendIRCode(RemoteButton button) => Verbose("This is not supported");

    public void SetChannel(int channel) => GoToChannelNumber((uint) channel);
    public void ToggleSubtitles()
    {
        
    }

    public void GoToServiceId(uint serviceId)
    {
        Get(GenerateCommandStringWithArgument("ChangeChannel", serviceId.ToString()));
    }

    public void GoToChannelNumber(uint channelNumber)
    {
        Get(GenerateCommandStringWithArgument("SelectChannel",channelNumber.ToString()));
    }

    public override void PowerOn()
    {
        Get(GenerateCommandString("PowerOnTv"));
        PowerState = PowerState.On;
    }

    public override void PowerOff()
    {
        Get(GenerateCommandString("PowerOffTv"));
        PowerState = PowerState.Off;
    }

    public void Reboot()
    {
        Get(GenerateCommandString("Reboot"));
    }

    public void SetVolume(int volume)
    {
        Get(GenerateCommandStringWithArgument("SetVolume",volume.ToString()));
        Volume = volume;
    }

    public void SetAudioMute(MuteState state)
    {
        Get(GenerateCommandStringWithArgument("SetMute",state == MuteState.On? "true" : "false"));
        AudioMute = state;
    }
}