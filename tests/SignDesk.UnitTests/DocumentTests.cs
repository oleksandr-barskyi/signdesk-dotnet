using SignDesk.Domain;

namespace SignDesk.UnitTests;

public class DocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static Document Draft(params string[] signerEmails)
    {
        var document = new Document("NDA", "Owner@Example.com", Now);
        foreach (var email in signerEmails)
        {
            document.AddSigner(email, email.Split('@')[0]);
        }

        return document;
    }

    private static DomainException Fails(Action act) => Assert.Throws<DomainException>(act);

    [Fact]
    public void New_document_is_a_draft_with_normalized_owner()
    {
        var document = Draft();

        Assert.Equal(DocumentStatus.Draft, document.Status);
        Assert.Equal("owner@example.com", document.OwnerEmail);
        Assert.Contains(document.Events, e => e.Action == "document.created");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.com")]
    [InlineData("trailing@")]
    [InlineData("with space@example.com")]
    public void Invalid_emails_are_rejected(string email)
    {
        var document = Draft();

        Assert.Equal("invalid_email", Fails(() => document.AddSigner(email, "x")).Code);
    }

    [Fact]
    public void Signers_get_sequential_order_and_duplicates_are_rejected()
    {
        var document = Draft("a@x.io", "b@x.io");

        Assert.Equal([1, 2], document.Signers.Select(s => s.Order));
        Assert.Equal("duplicate_signer", Fails(() => document.AddSigner("A@X.io", "again")).Code);
    }

    [Fact]
    public void Removing_a_signer_closes_the_gap_in_order()
    {
        var document = Draft("a@x.io", "b@x.io", "c@x.io");

        document.RemoveSigner(document.Signers[1].Id);

        Assert.Equal(["a@x.io", "c@x.io"], document.Signers.OrderBy(s => s.Order).Select(s => s.Email));
        Assert.Equal([1, 2], document.Signers.OrderBy(s => s.Order).Select(s => s.Order));
    }

    [Fact]
    public void Signer_limit_is_enforced()
    {
        var document = Draft([.. Enumerable.Range(1, Document.MaxSigners).Select(i => $"s{i}@x.io")]);

        Assert.Equal("too_many_signers", Fails(() => document.AddSigner("extra@x.io", "extra")).Code);
    }

    [Fact]
    public void Cannot_send_without_signers()
    {
        Assert.Equal("no_signers", Fails(() => Draft().Send(Now, TimeSpan.FromDays(7))).Code);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(24 * 91)]
    public void Lifetime_must_be_within_bounds(double hours)
    {
        var document = Draft("a@x.io");

        Assert.Equal("invalid_lifetime", Fails(() => document.Send(Now, TimeSpan.FromHours(hours))).Code);
    }

    [Fact]
    public void Sending_requests_the_first_signature_only()
    {
        var document = Draft("a@x.io", "b@x.io");
        document.ClearEvents();

        document.Send(Now, TimeSpan.FromDays(7));

        var request = Assert.Single(document.Events, e => e.Notify == NotificationKind.SignatureRequested);
        Assert.Equal("a@x.io", request.Recipient);
        Assert.Equal(Now.AddDays(7), document.ExpiresAt);
    }

    [Fact]
    public void Sent_document_cannot_be_edited()
    {
        var document = Draft("a@x.io");
        document.Send(Now, TimeSpan.FromDays(7));

        Assert.Equal("not_draft", Fails(() => document.AddSigner("b@x.io", "b")).Code);
        Assert.Equal("not_draft", Fails(() => document.RemoveSigner(document.Signers[0].Id)).Code);
    }

    [Fact]
    public void Signers_must_sign_in_order()
    {
        var document = Draft("a@x.io", "b@x.io");
        document.Send(Now, TimeSpan.FromDays(7));

        Assert.Equal("not_your_turn", Fails(() => document.Sign(document.Signers[1].Id, Now)).Code);
    }

    [Fact]
    public void Each_signature_requests_the_next_and_the_last_completes_the_document()
    {
        var document = Draft("a@x.io", "b@x.io");
        document.Send(Now, TimeSpan.FromDays(7));
        document.ClearEvents();

        document.Sign(document.Signers[0].Id, Now.AddHours(1));
        Assert.Equal("b@x.io", Assert.Single(document.Events, e => e.Notify == NotificationKind.SignatureRequested).Recipient);
        Assert.Equal(DocumentStatus.Sent, document.Status);

        document.Sign(document.Signers[1].Id, Now.AddHours(2));
        Assert.Equal(DocumentStatus.Completed, document.Status);
        Assert.Equal(Now.AddHours(2), document.ClosedAt);
        Assert.Contains(document.Events, e => e.Notify == NotificationKind.DocumentCompleted && e.Recipient == "owner@example.com");
        Assert.Null(document.CurrentSigner);
    }

    [Fact]
    public void Signing_twice_is_rejected()
    {
        var document = Draft("a@x.io", "b@x.io");
        document.Send(Now, TimeSpan.FromDays(7));
        var first = document.Signers[0].Id;
        document.Sign(first, Now);

        Assert.Equal("already_acted", Fails(() => document.Sign(first, Now)).Code);
    }

    [Fact]
    public void Decline_closes_the_document_and_notifies_the_owner()
    {
        var document = Draft("a@x.io", "b@x.io");
        document.Send(Now, TimeSpan.FromDays(7));

        document.Decline(document.Signers[0].Id, "  wrong amount  ", Now);

        Assert.Equal(DocumentStatus.Declined, document.Status);
        Assert.Equal("wrong amount", document.Signers[0].DeclineReason);
        Assert.Contains(document.Events, e => e.Notify == NotificationKind.DocumentDeclined);
        Assert.Equal("not_in_signing", Fails(() => document.Sign(document.Signers[1].Id, Now)).Code);
    }

    [Fact]
    public void Decline_requires_a_reason()
    {
        var document = Draft("a@x.io");
        document.Send(Now, TimeSpan.FromDays(7));

        Assert.Equal("reason_required", Fails(() => document.Decline(document.Signers[0].Id, " ", Now)).Code);
    }

    [Fact]
    public void Nobody_can_sign_after_the_window_closes_even_before_the_sweeper_runs()
    {
        var document = Draft("a@x.io");
        document.Send(Now, TimeSpan.FromDays(1));

        Assert.Equal("document_expired", Fails(() => document.Sign(document.Signers[0].Id, Now.AddDays(1))).Code);
        Assert.Equal(DocumentStatus.Sent, document.Status);
    }

    [Fact]
    public void Expire_only_applies_to_sent_documents_past_their_deadline()
    {
        var draft = Draft("a@x.io");
        Assert.False(draft.TryExpire(Now.AddYears(1)));

        var sent = Draft("a@x.io");
        sent.Send(Now, TimeSpan.FromDays(1));
        Assert.False(sent.TryExpire(Now.AddHours(23)));
        Assert.True(sent.TryExpire(Now.AddDays(1)));
        Assert.Equal(DocumentStatus.Expired, sent.Status);
        Assert.False(sent.TryExpire(Now.AddDays(2)));
    }
}
