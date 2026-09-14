using AVCoders.Core;
using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.SetTopBox;

public class SetTopBoxUiSignalR : DeviceBase
{
    private readonly SetTopBoxManager _setTopBoxManager;
    private readonly IHubContext<SetTopBoxHub, ISetTopBoxHub> _hubContext;

    public SetTopBoxUiSignalR(SetTopBoxManager setTopBoxManager, IHubContext<SetTopBoxHub, ISetTopBoxHub> hubContext)
        : base(setTopBoxManager.Name, CommunicationClient.None)
    {
        _setTopBoxManager = setTopBoxManager;
        _hubContext = hubContext;
        SetTopBoxHub.RegisterSetTopBoxManager(Name, setTopBoxManager);

        _setTopBoxManager.StateChanged += OnStateChanged;
    }

    private async void OnStateChanged(SetTopBoxState state)
    {
        await _hubContext.Clients.Group(Name).UpdateSetTopBox(state);
    }

    public override void PowerOn()
    {
        using (PushProperties("PowerOn"))
        {
            LogInformation("Turning on set-top box");
            _setTopBoxManager.PowerOn();
        }
    }

    public override void PowerOff()
    {
        using (PushProperties("PowerOff"))
        {
            LogInformation("Turning off set-top box");
            _setTopBoxManager.PowerOff();
        }
    }
}
