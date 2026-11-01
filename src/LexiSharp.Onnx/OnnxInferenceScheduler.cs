using System.Threading.Channels;

namespace LexiSharp.Onnx;

/// <summary>
/// Runs inference work on a small set of dedicated workers instead of the caller's thread. The
/// asynchronous encoder methods return immediately; the session call happens on a worker, and a
/// bounded queue provides backpressure when callers outrun the model.
/// </summary>
internal sealed class OnnxInferenceScheduler : IDisposable
{
    private readonly Channel<WorkItem> _channel;
    private readonly Task[] _workers;

    public OnnxInferenceScheduler(int workerCount, int queueCapacity)
    {
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = workerCount == 1,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _workers = new Task[workerCount];

        for (int i = 0; i < workerCount; i++)
            _workers[i] = Task.Factory.StartNew(WorkerAsync, TaskCreationOptions.LongRunning).Unwrap();
    }

    public async Task<T> EnqueueAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
            return await completion.Task.ConfigureAwait(false);
        }

        var item = new WorkItem(
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(work());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });

        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(OnnxTextEncoder));
        }

        return await completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        Task.WaitAll(_workers);
    }

    private async Task WorkerAsync()
    {
        await foreach (WorkItem item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            item.Run();
    }

    private readonly record struct WorkItem(Action Run);
}
