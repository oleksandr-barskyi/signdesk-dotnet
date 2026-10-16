using Microsoft.EntityFrameworkCore;
using SignDesk.Domain;
using SignDesk.Infrastructure.Documents;
using SignDesk.Infrastructure.Persistence;

namespace SignDesk.Api;

public static class DocumentEndpoints
{
    public static RouteGroupBuilder MapDocuments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/documents").WithTags("Documents");

        group.MapPost("/", async (CreateDocumentRequest request, DocumentService service, CancellationToken ct) =>
        {
            var document = await service.CreateAsync(request.Title, request.OwnerEmail, ct);
            return TypedResults.Created($"/documents/{document.Id}", DocumentResponse.From(document));
        });

        group.MapGet("/", async (SignDeskDbContext db, DocumentStatus? status, string? owner, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var query = db.Documents.AsNoTracking();
            if (status is not null)
            {
                query = query.Where(d => d.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(owner))
            {
                var normalized = owner.Trim().ToLowerInvariant();
                query = query.Where(d => d.OwnerEmail == normalized);
            }

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(d => d.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(d => new DocumentSummary(
                    d.Id, d.Title, d.OwnerEmail, d.Status, d.CreatedAt, d.ExpiresAt,
                    d.Signers.Count, d.Signers.Count(s => s.Status == SignerStatus.Signed)))
                .ToListAsync(ct);
            return TypedResults.Ok(new PagedResponse<DocumentSummary>(items, page, pageSize, total));
        });

        group.MapGet("/{id:guid}", async (Guid id, SignDeskDbContext db, CancellationToken ct) =>
        {
            var document = await db.Documents.AsNoTracking().Include(d => d.Signers).SingleOrDefaultAsync(d => d.Id == id, ct)
                ?? throw new NotFoundException($"Document {id} not found.");
            return TypedResults.Ok(DocumentResponse.From(document));
        });

        group.MapGet("/{id:guid}/audit", async (Guid id, SignDeskDbContext db, CancellationToken ct) =>
        {
            if (!await db.Documents.AnyAsync(d => d.Id == id, ct))
            {
                throw new NotFoundException($"Document {id} not found.");
            }

            var entries = await db.AuditEntries.AsNoTracking()
                .Where(a => a.DocumentId == id)
                .OrderBy(a => a.Id)
                .Select(a => new AuditEntryResponse(a.At, a.Action, a.Actor, a.Details))
                .ToListAsync(ct);
            return TypedResults.Ok(entries);
        });

        group.MapPost("/{id:guid}/signers", async (Guid id, AddSignerRequest request, DocumentService service, CancellationToken ct) =>
        {
            var signer = await service.AddSignerAsync(id, request.Email, request.Name, ct);
            return TypedResults.Created(
                $"/documents/{id}/signers/{signer.Id}",
                new SignerResponse(signer.Id, signer.Email, signer.Name, signer.Order, signer.Status, signer.SignedAt, signer.DeclineReason));
        });

        group.MapDelete("/{id:guid}/signers/{signerId:guid}", async (Guid id, Guid signerId, DocumentService service, CancellationToken ct) =>
        {
            await service.RemoveSignerAsync(id, signerId, ct);
            return TypedResults.NoContent();
        });

        group.MapPost("/{id:guid}/send", async (Guid id, SendDocumentRequest request, DocumentService service, CancellationToken ct) =>
            TypedResults.Ok(DocumentResponse.From(await service.SendAsync(id, TimeSpan.FromDays(request.ExpiresInDays), ct))));

        group.MapPost("/{id:guid}/signers/{signerId:guid}/sign", async (Guid id, Guid signerId, DocumentService service, CancellationToken ct) =>
            TypedResults.Ok(DocumentResponse.From(await service.SignAsync(id, signerId, ct))));

        group.MapPost("/{id:guid}/signers/{signerId:guid}/decline", async (Guid id, Guid signerId, DeclineRequest request, DocumentService service, CancellationToken ct) =>
            TypedResults.Ok(DocumentResponse.From(await service.DeclineAsync(id, signerId, request.Reason, ct))));

        return group;
    }
}
