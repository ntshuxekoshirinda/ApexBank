using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class QueuedHostedService : BackgroundService {
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly ILogger<QueuedHostedService> _logger;

    public QueuedHostedService(IBackgroundTaskQueue taskQueue, ILogger<QueuedHostedService> logger) {
        _taskQueue = taskQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        _logger.LogInformation("ApexBank Audit Background Worker service is now running.");

        while (!stoppingToken.IsCancellationRequested) {
            try {
                // Wait for an audit message to appear in the channel queue
                var auditMessage = await _taskQueue.DequeueAsync(stoppingToken);

                // Simulate processing: writing to an audit database table, dispatching webhooks, or generating logs
                _logger.LogInformation(
                    "BACKGROUND AUDIT PROCESSED: Transaction {Id} | Transfer: {Amount} {Currency} | Description: {Desc} | Time: {Time}",
                    auditMessage.TransactionId,
                    auditMessage.Amount,
                    auditMessage.CurrencyCode,
                    auditMessage.Description,
                    auditMessage.Timestamp
                );
            }
            catch (OperationCanceledException) {
                // Graceful shutdown handling
            }
            catch (Exception ex) {
                _logger.LogError(ex, "An error occurred while processing background audit queue item.");
            }
        }
    }
}