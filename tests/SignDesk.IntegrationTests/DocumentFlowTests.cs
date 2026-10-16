using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignDesk.Api;
using SignDesk.Domain;

namespace SignDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DocumentFlowTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string UniqueEmail(string who) => $"{who}-{Guid.NewGuid():N}@example.com";

    private async Task<DocumentResponse> CreateAsync(string owner, params string[] signers)
    {
        var created = await _client.PostJsonAsync("/documents", new CreateDocumentRequest("Share purchase agreement", owner));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = await created.ReadAsync<DocumentResponse>();
        foreach (var email in signers)
        {
            var added = await _client.PostJsonAsync($"/documents/{document.Id}/signers", new AddSignerRequest(email, "Signer"));
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        return await (await _client.GetAsync($"/documents/{document.Id}")).ReadAsync<DocumentResponse>();
    }

    [Fact]
    public async Task Full_signing_flow_completes_in_order_and_leaves_an_audit_trail()
    {
        var owner = UniqueEmail("owner");
        var document = await CreateAsync(owner, UniqueEmail("cfo"), UniqueEmail("ceo"));
        var (first, second) = (document.Signers[0], document.Signers[1]);

        var sent = await (await _client.PostJsonAsync($"/documents/{document.Id}/send", new SendDocumentRequest(7))).ReadAsync<DocumentResponse>();
        Assert.Equal(DocumentStatus.Sent, sent.Status);
        Assert.Equal(first.Id, sent.CurrentSignerId);

        var outOfOrder = await _client.PostAsync($"/documents/{document.Id}/signers/{second.Id}/sign", null);
        Assert.Equal(HttpStatusCode.Conflict, outOfOrder.StatusCode);
        Assert.Equal("not_your_turn", (await outOfOrder.ReadAsync<ProblemDetails>()).Extensions["code"]!.ToString());

        await _client.PostAsync($"/documents/{document.Id}/signers/{first.Id}/sign", null);
        var done = await (await _client.PostAsync($"/documents/{document.Id}/signers/{second.Id}/sign", null)).ReadAsync<DocumentResponse>();
        Assert.Equal(DocumentStatus.Completed, done.Status);
        Assert.All(done.Signers, s => Assert.Equal(SignerStatus.Signed, s.Status));

        var audit = await (await _client.GetAsync($"/documents/{document.Id}/audit")).ReadAsync<List<AuditEntryResponse>>();
        Assert.Equal(
            ["document.created", "signer.added", "signer.added", "document.sent", "signature.requested", "signer.signed", "signature.requested", "signer.signed", "document.completed"],
            audit.Select(a => a.Action));

        var outbox = await factory.WithDbAsync(db => db.OutboxMessages.Where(o => o.DocumentId == document.Id).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync());
        Assert.Equal(
            [NotificationKind.SignatureRequested, NotificationKind.SignatureRequested, NotificationKind.DocumentCompleted],
            outbox.Select(o => o.Kind));
        Assert.Equal([first.Email, second.Email, owner], outbox.Select(o => o.Recipient));
    }

    [Fact]
    public async Task Declining_closes_the_document_for_everyone()
    {
        var document = await CreateAsync(UniqueEmail("owner"), UniqueEmail("a"), UniqueEmail("b"));
        await _client.PostJsonAsync($"/documents/{document.Id}/send", new SendDocumentRequest(7));

        var declined = await _client.PostJsonAsync($"/documents/{document.Id}/signers/{document.Signers[0].Id}/decline", new DeclineRequest("Wrong price"));
        Assert.Equal(DocumentStatus.Declined, (await declined.ReadAsync<DocumentResponse>()).Status);

        var late = await _client.PostAsync($"/documents/{document.Id}/signers/{document.Signers[1].Id}/sign", null);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Removing_a_signer_renumbers_the_rest()
    {
        var document = await CreateAsync(UniqueEmail("owner"), UniqueEmail("a"), UniqueEmail("b"), UniqueEmail("c"));

        var removed = await _client.DeleteAsync($"/documents/{document.Id}/signers/{document.Signers[0].Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var after = await (await _client.GetAsync($"/documents/{document.Id}")).ReadAsync<DocumentResponse>();
        Assert.Equal([1, 2], after.Signers.Select(s => s.Order));
        Assert.Equal([document.Signers[1].Email, document.Signers[2].Email], after.Signers.Select(s => s.Email));
    }

    [Theory]
    [InlineData("", "owner@example.com")]
    [InlineData("NDA", "not-an-email")]
    public async Task Invalid_input_returns_400_with_validation_errors(string title, string owner)
    {
        var response = await _client.PostJsonAsync("/documents", new CreateDocumentRequest(title, owner));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadAsync<ValidationProblemDetails>();
        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task Unknown_document_returns_404_problem_details()
    {
        var response = await _client.GetAsync($"/documents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Listing_filters_by_owner_and_status_and_paginates()
    {
        var owner = UniqueEmail("lister");
        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(owner);
        }

        var sentOne = await CreateAsync(owner, UniqueEmail("s"));
        await _client.PostJsonAsync($"/documents/{sentOne.Id}/send", new SendDocumentRequest(7));

        var page = await (await _client.GetAsync($"/documents?owner={Uri.EscapeDataString(owner.ToUpperInvariant())}&pageSize=2&page=2")).ReadAsync<PagedResponse<DocumentSummary>>();
        Assert.Equal(4, page.Total);
        Assert.Equal(2, page.Items.Count);

        var drafts = await (await _client.GetAsync($"/documents?owner={Uri.EscapeDataString(owner)}&status=Draft")).ReadAsync<PagedResponse<DocumentSummary>>();
        Assert.Equal(3, drafts.Total);

        var sent = await (await _client.GetAsync($"/documents?owner={Uri.EscapeDataString(owner)}&status=Sent")).ReadAsync<PagedResponse<DocumentSummary>>();
        var item = Assert.Single(sent.Items);
        Assert.Equal((1, 0), (item.SignerCount, item.SignedCount));
    }

    [Fact]
    public async Task Concurrent_updates_to_the_same_document_are_detected()
    {
        var document = await CreateAsync(UniqueEmail("owner"), UniqueEmail("a"));

        await using var first = factory.NewDbContext();
        await using var second = factory.NewDbContext();
        var a = await first.Documents.Include(d => d.Signers).SingleAsync(d => d.Id == document.Id);
        var b = await second.Documents.Include(d => d.Signers).SingleAsync(d => d.Id == document.Id);

        a.Send(factory.Clock.GetUtcNow(), TimeSpan.FromDays(7));
        await first.SaveChangesAsync();

        b.Send(factory.Clock.GetUtcNow(), TimeSpan.FromDays(3));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Health_endpoint_checks_the_database()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
