using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SignDesk.Infrastructure.Documents;

public sealed partial class ExpirationSweeper(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ExpirationSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<DocumentService>();
                var expired = await service.ExpireDueAsync(stoppingToken);
                if (expired > 0)
                {
                    LogExpired(expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} documents")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Expiration sweep failed")]
    private partial void LogFailed(Exception ex);
}
