using Microsoft.EntityFrameworkCore;
using Npgsql;
using SignDesk.Domain;
using SignDesk.Infrastructure.Notifications;
using SignDesk.Infrastructure.Persistence;

namespace SignDesk.Infrastructure.Documents;

public sealed class NotFoundException(string message) : Exception(message);

public enum WebhookResult
{
    Applied,
    Duplicate,
}

public sealed class DocumentService(SignDeskDbContext db, OutboxSignal signal, TimeProvider clock)
{
    public const int ExpireBatchSize = 100;

    public async Task<Document> CreateAsync(string title, string ownerEmail, CancellationToken ct)
    {
        var document = new Document(title, ownerEmail, clock.GetUtcNow());
        db.Documents.Add(document);
        await CommitAsync(document, ct);
        return document;
    }

    public async Task<Signer> AddSignerAsync(Guid documentId, string email, string name, CancellationToken ct)
    {
        var document = await LoadAsync(documentId, ct);
        var signer = document.AddSigner(email, name);
        db.Signers.Add(signer);
        await CommitAsync(document, ct);
        return signer;
    }

    public async Task RemoveSignerAsync(Guid documentId, Guid signerId, CancellationToken ct)
    {
        var document = await LoadAsync(documentId, ct);
        document.RemoveSigner(signerId);
        await CommitAsync(document, ct);
    }

    public async Task<Document> SendAsync(Guid documentId, TimeSpan lifetime, CancellationToken ct)
    {
        var document = await LoadAsync(documentId, ct);
        document.Send(clock.GetUtcNow(), lifetime);
        await CommitAsync(document, ct);
        return document;
    }

    public async Task<Document> SignAsync(Guid documentId, Guid signerId, CancellationToken ct)
    {
        var document = await LoadAsync(documentId, ct);
        document.Sign(signerId, clock.GetUtcNow());
        await CommitAsync(document, ct);
        return document;
    }

    public async Task<Document> DeclineAsync(Guid documentId, Guid signerId, string reason, CancellationToken ct)
    {
        var document = await LoadAsync(documentId, ct);
        document.Decline(signerId, reason, clock.GetUtcNow());
        await CommitAsync(document, ct);
        return document;
    }

    public async Task<WebhookResult> ApplyWebhookAsync(
        string eventId, string type, Guid documentId, Guid signerId, string? reason, CancellationToken ct)
    {
        if (await db.WebhookEvents.AnyAsync(w => w.EventId == eventId, ct))
        {
            return WebhookResult.Duplicate;
        }

        var document = await LoadAsync(documentId, ct);
        switch (type)
        {
            case "signer.signed":
                document.Sign(signerId, clock.GetUtcNow());
                break;
            case "signer.declined":
                document.Decline(signerId, reason ?? "Declined via signing provider", clock.GetUtcNow());
                break;
            default:
                throw new DomainException("unknown_event_type", $"Unsupported event type '{type}'.");
        }

        db.WebhookEvents.Add(new WebhookEvent(eventId, type, clock.GetUtcNow()));
        try
        {
            await CommitAsync(document, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return WebhookResult.Duplicate;
        }

        return WebhookResult.Applied;
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.Documents
            .Include(d => d.Signers)
            .Where(d => d.Status == DocumentStatus.Sent && d.ExpiresAt <= now)
            .OrderBy(d => d.ExpiresAt)
            .Take(ExpireBatchSize)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var document in due)
        {
            if (document.TryExpire(now))
            {
                Collect(document);
                expired++;
            }
        }

        if (expired > 0)
        {
            await SaveAndSignalAsync(ct);
        }

        return expired;
    }

    private async Task<Document> LoadAsync(Guid id, CancellationToken ct) =>
        await db.Documents.Include(d => d.Signers).SingleOrDefaultAsync(d => d.Id == id, ct)
        ?? throw new NotFoundException($"Document {id} not found.");

    private async Task CommitAsync(Document document, CancellationToken ct)
    {
        Collect(document);
        await SaveAndSignalAsync(ct);
    }

    private void Collect(Document document)
    {
        var now = clock.GetUtcNow();
        foreach (var e in document.Events)
        {
            db.AuditEntries.Add(new AuditEntry(document.Id, now, e.Action, e.Actor, e.Details));
            if (e.Notify is { } kind && e.Recipient is { } recipient)
            {
                db.OutboxMessages.Add(new OutboxMessage(document.Id, kind, recipient, now));
            }
        }

        document.ClearEvents();
    }

    private async Task SaveAndSignalAsync(CancellationToken ct)
    {
        var hasOutbox = db.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added);
        await db.SaveChangesAsync(ct);
        if (hasOutbox)
        {
            signal.Notify();
        }
    }
}
