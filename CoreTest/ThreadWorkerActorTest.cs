namespace AVCoders.Core.Tests;

public class ThreadWorkerActorTest
{
    private static readonly Actor Panel = new(ActorKind.Panel, "10.1.2.5", "Crestron", null, "hub-1");
    private static readonly TimeSpan SleepTime = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task StartedAndRestartedUnderAnActor_TheLoopDoesNotSeeIt()
    {
        var invocations = 0;
        var sawAnActor = false;
        var worker = new ThreadWorker(_ =>
        {
            if (Actor.Current != null)
                sawAnActor = true;
            invocations++;
            return Task.CompletedTask;
        }, SleepTime);
        Actor.Current = Panel;

        await worker.Restart();
        await Wait.UntilAsync(() => invocations >= 1, "The worker never ran");
        await worker.Restart();
        var afterRestart = invocations;
        await Wait.UntilAsync(() => invocations > afterRestart, "The worker never ran after the restart");
        await worker.Stop();

        Assert.False(sawAnActor);
    }

    [Fact]
    public async Task RestartedFromInsideTheActionUnderAnActor_TheNewLoopDoesNotSeeIt()
    {
        var invocations = 0;
        var sawAnActor = false;
        ThreadWorker? worker = null;
        worker = new ThreadWorker(async _ =>
        {
            invocations++;
            if (invocations == 1)
            {
                Actor.Current = Panel;
                await worker!.Restart();
                return;
            }
            if (Actor.Current != null)
                sawAnActor = true;
        }, SleepTime);

        await worker.Restart();
        await Wait.UntilAsync(() => invocations >= 3, "The worker never ran after restarting itself");
        await worker.Stop();

        Assert.False(sawAnActor);
    }
}
