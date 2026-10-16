using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SignDesk.Infrastructure.Persistence;

namespace SignDesk.Infrastructure.Notifications;

public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopes,
    OutboxSignal signal,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    public const int BatchSize = 20;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await DispatchBatchAsync(stoppingToken) == BatchSize)
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBatchFailed(ex);
            }

            await signal.WaitAsync(PollInterval, stoppingToken);
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SignDeskDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE sent_at IS NULL AND dead_lettered_at IS NULL AND next_attempt_at <= {now}
                ORDER BY next_attempt_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            try
            {
                await sender.SendAsync(message, ct);
                message.MarkSent(clock.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.MarkFailed(ex.Message, clock.GetUtcNow());
                LogSendFailed(message.Id, message.Attempts, ex.Message);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed")]
    private partial void LogBatchFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {Id} failed on attempt {Attempts}: {Error}")]
    private partial void LogSendFailed(Guid id, int attempts, string error);
}
