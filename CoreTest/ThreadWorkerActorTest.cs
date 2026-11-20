using System.Collections.Concurrent;

namespace AVCoders.Core.Tests;

public class ThreadWorkerActorTest
{
    private static readonly Actor Panel = new(ActorKind.Panel, "10.1.2.5", "Crestron", null, "hub-1");
    private static readonly TimeSpan SleepTime = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task StartedAndRestartedUnderAnActor_TheLoopRunsAsTheWorker()
    {
        var invocations = 0;
        var seen = new ConcurrentQueue<Actor?>();
        var worker = new ThreadWorker(_ =>
        {
            seen.Enqueue(Actor.Current);
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

        Assert.All(seen, actor => Assert.Equal(Actor.System(worker.Name), actor));
    }

    [Fact]
    public async Task RestartedFromInsideTheActionUnderAnActor_TheNewLoopRunsAsTheWorker()
    {
        var invocations = 0;
        var sawAnotherActor = false;
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
            if (Actor.Current != Actor.System(worker!.Name))
                sawAnotherActor = true;
        }, SleepTime);

        await worker.Restart();
        await Wait.UntilAsync(() => invocations >= 3, "The worker never ran after restarting itself");
        await worker.Stop();

        Assert.False(sawAnotherActor);
    }
}
