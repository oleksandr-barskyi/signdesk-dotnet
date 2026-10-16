using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using SignDesk.Domain;
using SignDesk.Infrastructure.Notifications;
using SignDesk.Infrastructure.Persistence;

namespace SignDesk.IntegrationTests;

public sealed class RecordingSender : INotificationSender
{
    private readonly Lock _gate = new();
    private readonly List<OutboxMessage> _sent = [];

    public int FailuresBeforeSuccess { get; set; }

    public IReadOnlyList<OutboxMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(OutboxMessage message, CancellationToken ct)
    {
        lock (_gate)
        {
            if (FailuresBeforeSuccess > 0)
            {
                FailuresBeforeSuccess--;
                throw new InvalidOperationException("mail server unavailable");
            }

            _sent.Add(message);
        }

        return Task.CompletedTask;
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WebhookSecret = "test-webhook-secret";

    private readonly string _connectionString;

    public ApiFactory()
    {
        var baseConnection = Environment.GetEnvironmentVariable("SIGNDESK_TEST_DB")
            ?? "Host=localhost;Port=54331;Username=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"signdesk_test_{Guid.NewGuid():N}",
        }.ConnectionString;
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));

    public RecordingSender Sender { get; } = new();

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:SignDesk", _connectionString);
        builder.UseSetting("Webhooks:SigningProviderSecret", WebhookSecret);
        builder.UseSetting("BackgroundWorkers:Enabled", "false");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<INotificationSender>();
            services.AddSingleton<INotificationSender>(Sender);
        });
    }

    public Task InitializeAsync()
    {
        using var client = CreateClient();
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SignDeskDbContext>().Database.EnsureDeletedAsync();
        }

        await DisposeAsync();
    }

    public async Task<T> WithDbAsync<T>(Func<SignDeskDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SignDeskDbContext>());
    }

    public SignDeskDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<SignDeskDbContext>();
        Infrastructure.DependencyInjection.ConfigureDb(options, _connectionString);
        return new SignDeskDbContext(options.Options);
    }
}

public static class HttpExtensions
{
    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ApiFactory.Json);
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
