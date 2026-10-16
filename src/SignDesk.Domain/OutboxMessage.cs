namespace SignDesk.Domain;

public sealed class OutboxMessage
{
    public const int MaxAttempts = 5;

    private OutboxMessage()
    {
        Recipient = string.Empty;
    }

    public OutboxMessage(Guid documentId, NotificationKind kind, string recipient, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        DocumentId = documentId;
        Kind = kind;
        Recipient = recipient;
        CreatedAt = now;
        NextAttemptAt = now;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public NotificationKind Kind { get; private set; }
    public string Recipient { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? DeadLetteredAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public void MarkSent(DateTimeOffset now)
    {
        Attempts++;
        SentAt = now;
        LastError = null;
    }

    public void MarkFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = error.Length > 500 ? error[..500] : error;
        if (Attempts >= MaxAttempts)
        {
            DeadLetteredAt = now;
            return;
        }

        NextAttemptAt = now + Backoff(Attempts);
    }

    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, attempts - 1)));
}
