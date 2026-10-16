using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignDesk.Infrastructure.Documents;
using SignDesk.Infrastructure.Notifications;
using SignDesk.Infrastructure.Persistence;
using SignDesk.Infrastructure.Webhooks;

namespace SignDesk.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSignDeskInfrastructure(
        this IServiceCollection services, string connectionString, string webhookSecret, bool runBackgroundWorkers = true)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<SignDeskDbContext>(o => ConfigureDb(o, connectionString));
        services.AddScoped<DocumentService>();
        services.AddSingleton<OutboxSignal>();
        services.AddScoped<INotificationSender, LoggingNotificationSender>();
        services.AddSingleton(sp => new WebhookSignatureVerifier(webhookSecret, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<OutboxDispatcher>();

        if (runBackgroundWorkers)
        {
            services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
            services.AddHostedService<ExpirationSweeper>();
        }

        return services;
    }

    public static void ConfigureDb(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(SignDeskDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention();
}
