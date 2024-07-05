namespace AVCoders.MediaPlayer;

public interface ISetTopBox
{
    public void ChannelUp();
    public void ChannelDown();
    public void SendIRCode(RemoteButton button);
    public void SetChannel(int channel);
}