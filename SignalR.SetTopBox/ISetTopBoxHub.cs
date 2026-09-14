namespace AVCoders.SignalR.SetTopBox;

public interface ISetTopBoxHub
{
    Task UpdateSetTopBox(SetTopBoxState state);
}
