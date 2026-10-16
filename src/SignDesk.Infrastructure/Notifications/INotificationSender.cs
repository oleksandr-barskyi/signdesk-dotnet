using Microsoft.Extensions.Logging;
using SignDesk.Domain;

namespace SignDesk.Infrastructure.Notifications;

public interface INotificationSender
{
    Task SendAsync(OutboxMessage message, CancellationToken ct);
}

public sealed partial class LoggingNotificationSender(ILogger<LoggingNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(OutboxMessage message, CancellationToken ct)
    {
        LogSent(message.Kind, message.Recipient, message.DocumentId);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification {Kind} to {Recipient} for document {DocumentId}")]
    private partial void LogSent(NotificationKind kind, string recipient, Guid documentId);
}
