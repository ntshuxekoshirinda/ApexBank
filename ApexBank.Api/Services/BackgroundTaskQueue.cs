using System.Threading.Channels;

public interface IBackgroundTaskQueue {
    ValueTask QueueBackgroundWorkItemAsync(AuditLogMessage workItem);
    ValueTask<AuditLogMessage> DequeueAsync(CancellationToken cancellationToken);
}

public class BackgroundTaskQueue : IBackgroundTaskQueue {
    private readonly Channel<AuditLogMessage> _queue;

    public BackgroundTaskQueue(int capacity = 1000) {
        // Bounded channel prevents memory exhaustion under heavy traffic spikes
        var options = new BoundedChannelOptions(capacity) {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<AuditLogMessage>(options);
    }

    public async ValueTask QueueBackgroundWorkItemAsync(AuditLogMessage workItem) {
        ArgumentNullException.ThrowIfNull(workItem);
        await _queue.Writer.WriteAsync(workItem);
    }

    public async ValueTask<AuditLogMessage> DequeueAsync(CancellationToken cancellationToken) {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }
}