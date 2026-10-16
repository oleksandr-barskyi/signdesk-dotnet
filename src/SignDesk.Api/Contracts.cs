using System.ComponentModel.DataAnnotations;
using SignDesk.Domain;

namespace SignDesk.Api;

public sealed record CreateDocumentRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    [property: Required, EmailAddress, StringLength(320)] string OwnerEmail);

public sealed record AddSignerRequest(
    [property: Required, EmailAddress, StringLength(320)] string Email,
    [property: Required, StringLength(200, MinimumLength = 1)] string Name);

public sealed record SendDocumentRequest(
    [property: Range(1, 90)] int ExpiresInDays = 14);

public sealed record DeclineRequest(
    [property: Required, StringLength(1000, MinimumLength = 1)] string Reason);

public sealed record SignerResponse(
    Guid Id, string Email, string Name, int Order, SignerStatus Status, DateTimeOffset? SignedAt, string? DeclineReason);

public sealed record DocumentResponse(
    Guid Id,
    string Title,
    string OwnerEmail,
    DocumentStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ClosedAt,
    Guid? CurrentSignerId,
    IReadOnlyList<SignerResponse> Signers)
{
    public static DocumentResponse From(Document d) => new(
        d.Id,
        d.Title,
        d.OwnerEmail,
        d.Status,
        d.CreatedAt,
        d.SentAt,
        d.ExpiresAt,
        d.ClosedAt,
        d.CurrentSigner?.Id,
        [.. d.Signers.OrderBy(s => s.Order).Select(s => new SignerResponse(s.Id, s.Email, s.Name, s.Order, s.Status, s.SignedAt, s.DeclineReason))]);
}

public sealed record DocumentSummary(
    Guid Id, string Title, string OwnerEmail, DocumentStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, int SignerCount, int SignedCount);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record AuditEntryResponse(DateTimeOffset At, string Action, string Actor, string? Details);

public sealed record WebhookEnvelope(string Id, string Type, WebhookData Data);

public sealed record WebhookData(Guid DocumentId, Guid SignerId, string? Reason);
