namespace SignDesk.Domain;

public sealed record DomainEvent(
    string Action,
    string Actor,
    string? Details,
    NotificationKind? Notify = null,
    string? Recipient = null);
