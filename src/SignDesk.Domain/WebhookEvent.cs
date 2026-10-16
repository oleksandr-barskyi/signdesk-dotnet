namespace SignDesk.Domain;

public sealed class WebhookEvent
{
    private WebhookEvent()
    {
        EventId = string.Empty;
        Type = string.Empty;
    }

    public WebhookEvent(string eventId, string type, DateTimeOffset receivedAt)
    {
        EventId = eventId;
        Type = type;
        ReceivedAt = receivedAt;
    }

    public long Id { get; private set; }
    public string EventId { get; private set; }
    public string Type { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
}
