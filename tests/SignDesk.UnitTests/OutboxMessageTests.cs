using SignDesk.Domain;

namespace SignDesk.UnitTests;

public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]
    [InlineData(10, 300)]
    public void Backoff_doubles_and_is_capped(int attempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxMessage.Backoff(attempts));
    }

    [Fact]
    public void Failure_schedules_a_retry_and_keeps_the_error()
    {
        var message = new OutboxMessage(Guid.NewGuid(), NotificationKind.SignatureRequested, "a@x.io", Now);

        message.MarkFailed("smtp timeout", Now);

        Assert.Equal(1, message.Attempts);
        Assert.Equal(Now.AddSeconds(5), message.NextAttemptAt);
        Assert.Equal("smtp timeout", message.LastError);
        Assert.Null(message.DeadLetteredAt);
    }

    [Fact]
    public void Message_is_dead_lettered_after_max_attempts()
    {
        var message = new OutboxMessage(Guid.NewGuid(), NotificationKind.SignatureRequested, "a@x.io", Now);

        for (var i = 0; i < OutboxMessage.MaxAttempts; i++)
        {
            message.MarkFailed(new string('x', 900), Now);
        }

        Assert.Equal(Now, message.DeadLetteredAt);
        Assert.Equal(500, message.LastError!.Length);
    }

    [Fact]
    public void Success_clears_the_last_error()
    {
        var message = new OutboxMessage(Guid.NewGuid(), NotificationKind.SignatureRequested, "a@x.io", Now);
        message.MarkFailed("boom", Now);

        message.MarkSent(Now.AddSeconds(5));

        Assert.Equal(2, message.Attempts);
        Assert.Null(message.LastError);
        Assert.Equal(Now.AddSeconds(5), message.SentAt);
    }
}
