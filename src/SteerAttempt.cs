namespace CodexWhip;

internal sealed class SteerAttempt : IDisposable
{
    private const int Pending = 0;
    private const int Committed = 1;
    private const int Cancelled = 2;

    private readonly CancellationTokenSource _cancellation = new();
    private int _state = Pending;

    public CancellationToken Token => _cancellation.Token;

    public bool IsCancellationRequested =>
        Volatile.Read(ref _state) == Cancelled;

    public bool TryCommit() =>
        Interlocked.CompareExchange(ref _state, Committed, Pending) == Pending;

    public bool Cancel()
    {
        if (Interlocked.CompareExchange(ref _state, Cancelled, Pending) != Pending)
        {
            return false;
        }

        _cancellation.Cancel();
        return true;
    }

    public void Dispose()
    {
        _cancellation.Dispose();
    }

    internal static bool RunSelfTest(out string message)
    {
        using var cancelled = new SteerAttempt();
        using var committed = new SteerAttempt();
        var success = cancelled.Cancel()
            && cancelled.IsCancellationRequested
            && cancelled.Token.IsCancellationRequested
            && !cancelled.TryCommit()
            && committed.TryCommit()
            && !committed.Cancel()
            && !committed.IsCancellationRequested
            && !committed.Token.IsCancellationRequested;
        message = success
            ? "PASS: steering attempts cancel or commit atomically."
            : "FAIL: steering attempt state is inconsistent.";
        return success;
    }
}
