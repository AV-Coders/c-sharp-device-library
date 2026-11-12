namespace AVCoders.Core.Tests;

public static class AsAPerson
{
    public static readonly Actor Browser = new(ActorKind.Browser, "192.0.2.10", "AsAPerson", "TECH-LAPTOP", "circuit-1");

    public static void Run(Action action) => Run(Browser, action);

    public static void Run(Actor person, Action action)
    {
        var previous = Actor.Current;
        Actor.Current = person;
        try
        {
            action();
        }
        finally
        {
            Actor.Current = previous;
        }
    }

    public static async Task RunAsync(Func<Task> action)
    {
        Actor.Current = Browser;
        await action();
    }
}
