namespace OFPlayer.WinUI.Core;

/// <summary>Short-lived, process-local read result. Concurrent callers share one
/// load; cancellation abandons a waiter, not the other callers' request.</summary>
public sealed class AsyncReadCache<T>(TimeSpan ttl, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private Task<T>? pending;
    private T? value;
    private bool hasValue;
    private long storedAt, generation;

    public Task<T> GetAsync(Func<Task<T>> load, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        TaskCompletionSource<T>? owner = null;
        Task<T> task;
        long stamp;
        lock (gate)
        {
            if (hasValue && clock.GetElapsedTime(storedAt) < ttl) return Task.FromResult(value!);
            stamp = generation;
            if (pending is null)
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = owner.Task;
            }
            task = pending;
        }
        if (owner is not null) _ = LoadAsync(load, owner, stamp);
        return task.WaitAsync(cancel);
    }

    public void Invalidate()
    {
        lock (gate) { generation++; hasValue = false; value = default; pending = null; }
    }

    private async Task LoadAsync(Func<Task<T>> load, TaskCompletionSource<T> owner, long stamp)
    {
        try
        {
            var result = await load().ConfigureAwait(false);
            lock (gate)
            {
                if (stamp == generation)
                {
                    value = result; hasValue = true; storedAt = clock.GetTimestamp();
                }
            }
            owner.TrySetResult(result);
        }
        catch (OperationCanceledException error) { owner.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { owner.TrySetException(error); }
        finally { lock (gate) { if (ReferenceEquals(pending, owner.Task)) pending = null; } }
    }
}
