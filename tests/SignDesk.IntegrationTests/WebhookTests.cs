using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignDesk.Api;
using SignDesk.Domain;
using SignDesk.Infrastructure.Webhooks;

namespace SignDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public class WebhookTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<DocumentResponse> SentDocumentAsync()
    {
        var document = await (await _client.PostJsonAsync("/documents", new CreateDocumentRequest("Loan agreement", $"o-{Guid.NewGuid():N}@example.com"))).ReadAsync<DocumentResponse>();
        await _client.PostJsonAsync($"/documents/{document.Id}/signers", new AddSignerRequest($"s-{Guid.NewGuid():N}@example.com", "Signer"));
        return await (await _client.PostJsonAsync($"/documents/{document.Id}/send", new SendDocumentRequest(7))).ReadAsync<DocumentResponse>();
    }

    private async Task<HttpResponseMessage> DeliverAsync(object envelope, string? secret = null, TimeSpan? age = null)
    {
        var payload = JsonSerializer.Serialize(envelope, ApiFactory.Json);
        var signer = new WebhookSignatureVerifier(secret ?? ApiFactory.WebhookSecret, factory.Clock);
        var timestamp = (factory.Clock.GetUtcNow() - (age ?? TimeSpan.Zero)).ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/signing-provider")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WebhookEndpoints.SignatureHeader, signer.Sign(timestamp, payload));
        return await _client.SendAsync(request);
    }

    private static WebhookEnvelope Signed(DocumentResponse d, string? eventId = null) =>
        new(eventId ?? $"evt_{Guid.NewGuid():N}", "signer.signed", new WebhookData(d.Id, d.Signers[0].Id, null));

    [Fact]
    public async Task Signed_event_is_applied_once_even_when_delivered_twice()
    {
        var document = await SentDocumentAsync();
        var envelope = Signed(document);

        var first = await DeliverAsync(envelope);
        var second = await DeliverAsync(envelope);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"applied\"", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"duplicate\"", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var after = await (await _client.GetAsync($"/documents/{document.Id}")).ReadAsync<DocumentResponse>();
        Assert.Equal(DocumentStatus.Completed, after.Status);
        var signedEvents = await factory.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.DocumentId == document.Id && a.Action == "signer.signed"));
        Assert.Equal(1, signedEvents);
    }

    [Fact]
    public async Task Parallel_duplicate_deliveries_apply_once()
    {
        var document = await SentDocumentAsync();
        var envelope = Signed(document);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => DeliverAsync(envelope)));

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        var stored = await factory.WithDbAsync(db => db.WebhookEvents.CountAsync(w => w.EventId == envelope.Id));
        Assert.Equal(1, stored);
        var signedEvents = await factory.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.DocumentId == document.Id && a.Action == "signer.signed"));
        Assert.Equal(1, signedEvents);
    }

    [Fact]
    public async Task Wrong_secret_is_rejected()
    {
        var document = await SentDocumentAsync();

        var response = await DeliverAsync(Signed(document), secret: "attacker");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Replayed_old_event_is_rejected()
    {
        var document = await SentDocumentAsync();

        var response = await DeliverAsync(Signed(document), age: TimeSpan.FromMinutes(10));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Event_for_a_signer_not_on_the_document_is_a_conflict()
    {
        var document = await SentDocumentAsync();
        var envelope = new WebhookEnvelope($"evt_{Guid.NewGuid():N}", "signer.signed", new WebhookData(document.Id, Guid.NewGuid(), null));

        var response = await DeliverAsync(envelope);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
