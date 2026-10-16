namespace SignDesk.Domain;

public sealed class AuditEntry
{
    private AuditEntry()
    {
        Action = string.Empty;
        Actor = string.Empty;
    }

    public AuditEntry(Guid documentId, DateTimeOffset at, string action, string actor, string? details)
    {
        DocumentId = documentId;
        At = at;
        Action = action;
        Actor = actor;
        Details = details;
    }

    public long Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public DateTimeOffset At { get; private set; }
    public string Action { get; private set; }
    public string Actor { get; private set; }
    public string? Details { get; private set; }
}
