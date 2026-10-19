using Microsoft.AspNetCore.SignalR;

namespace AVCoders.SignalR.Audit.Tests;

#pragma warning disable S2325 // Methods should be static
public class ProgramHub : Hub
{
    public void Mute() { }

    public bool CheckPin(string pin) => true;

    public void SavePreset(string camera, int index, string pin) { }
}
