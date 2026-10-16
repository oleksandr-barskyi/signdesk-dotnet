using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SignDesk.Api;
using SignDesk.Domain;
using SignDesk.Infrastructure.Documents;
using SignDesk.Infrastructure.Notifications;

namespace SignDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public class BackgroundWorkTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<DocumentResponse> SentDocumentAsync(int days)
    {
        var owner = $"owner-{Guid.NewGuid():N}@example.com";
        var document = await (await _client.PostJsonAsync("/documents", new CreateDocumentRequest("Board minutes", owner))).ReadAsync<DocumentResponse>();
        await _client.PostJsonAsync($"/documents/{document.Id}/signers", new AddSignerRequest($"s-{Guid.NewGuid():N}@example.com", "Signer"));
        return await (await _client.PostJsonAsync($"/documents/{document.Id}/send", new SendDocumentRequest(days))).ReadAsync<DocumentResponse>();
    }

    private async Task DrainOutboxAsync()
    {
        var dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    [Fact]
    public async Task Expired_documents_are_closed_by_the_sweeper_and_owner_is_notified()
    {
        var document = await SentDocumentAsync(days: 1);
        factory.Clock.Advance(TimeSpan.FromDays(1));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<DocumentService>().ExpireDueAsync(CancellationToken.None) >= 1);
        }

        var after = await (await _client.GetAsync($"/documents/{document.Id}")).ReadAsync<DocumentResponse>();
        Assert.Equal(DocumentStatus.Expired, after.Status);

        var kinds = await factory.WithDbAsync(db => db.OutboxMessages.Where(o => o.DocumentId == document.Id).Select(o => o.Kind).ToListAsync());
        Assert.Contains(NotificationKind.DocumentExpired, kinds);

        var sign = await _client.PostAsync($"/documents/{document.Id}/signers/{document.Signers[0].Id}/sign", null);
        Assert.Equal(HttpStatusCode.Conflict, sign.StatusCode);
    }

    [Fact]
    public async Task Failed_notifications_are_retried_with_backoff_and_then_delivered_once()
    {
        await DrainOutboxAsync();
        var document = await SentDocumentAsync(days: 7);
        factory.Sender.FailuresBeforeSuccess = 1;

        await DrainOutboxAsync();

        var pending = await factory.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(o => o.DocumentId == document.Id));
        Assert.Null(pending.SentAt);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal("mail server unavailable", pending.LastError);
        Assert.DoesNotContain(factory.Sender.Sent, m => m.DocumentId == document.Id);

        factory.Clock.Advance(OutboxMessage.Backoff(1));
        await DrainOutboxAsync();
        await DrainOutboxAsync();

        var delivered = await factory.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(o => o.DocumentId == document.Id));
        Assert.NotNull(delivered.SentAt);
        Assert.Equal(2, delivered.Attempts);
        Assert.Single(factory.Sender.Sent, m => m.DocumentId == document.Id);
    }
}
