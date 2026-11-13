namespace AVCoders.Dsp;

internal sealed class StatusRequests(int maxUnanswered, Action send, Action reportUnanswered, Action reconnect)
{
    private int _unanswered;

    public void Request()
    {
        var unanswered = Volatile.Read(ref _unanswered);
        if (unanswered > 0)
            reportUnanswered();

        if (unanswered >= maxUnanswered)
        {
            Interlocked.Exchange(ref _unanswered, 0);
            reconnect();
            return;
        }

        Interlocked.Increment(ref _unanswered);
        send();
    }

    public void Answered() => Interlocked.Exchange(ref _unanswered, 0);
}
