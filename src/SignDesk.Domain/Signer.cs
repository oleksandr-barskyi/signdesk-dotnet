namespace SignDesk.Domain;

public sealed class Signer
{
    private Signer()
    {
        Email = string.Empty;
        Name = string.Empty;
    }

    internal Signer(Guid documentId, string email, string name, int order)
    {
        Id = Guid.CreateVersion7();
        DocumentId = documentId;
        Email = email;
        Name = name;
        Order = order;
        Status = SignerStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public string Email { get; private set; }
    public string Name { get; private set; }
    public int Order { get; internal set; }
    public SignerStatus Status { get; private set; }
    public DateTimeOffset? SignedAt { get; private set; }
    public string? DeclineReason { get; private set; }

    internal void MarkSigned(DateTimeOffset now)
    {
        Status = SignerStatus.Signed;
        SignedAt = now;
    }

    internal void MarkDeclined(string reason)
    {
        Status = SignerStatus.Declined;
        DeclineReason = reason;
    }
}
