using Microsoft.Extensions.Time.Testing;
using SignDesk.Infrastructure.Webhooks;

namespace SignDesk.UnitTests;

public class WebhookSignatureVerifierTests
{
    private const string Payload = """{"id":"evt_1","type":"signer.signed"}""";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    private WebhookSignatureVerifier Verifier(string secret = "s3cret") => new(secret, _clock);

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    [Fact]
    public void Valid_signature_passes()
    {
        var verifier = Verifier();

        Assert.Equal(SignatureCheck.Valid, verifier.Verify(verifier.Sign(Now, Payload), Payload));
    }

    [Fact]
    public void Tampered_payload_fails()
    {
        var verifier = Verifier();

        Assert.Equal(SignatureCheck.Mismatch, verifier.Verify(verifier.Sign(Now, Payload), Payload.Replace("evt_1", "evt_2", StringComparison.Ordinal)));
    }

    [Fact]
    public void Signature_from_another_secret_fails()
    {
        Assert.Equal(SignatureCheck.Mismatch, Verifier().Verify(Verifier("other").Sign(Now, Payload), Payload));
    }

    [Fact]
    public void Old_timestamp_is_rejected_to_stop_replays()
    {
        var verifier = Verifier();
        var header = verifier.Sign(Now, Payload);

        _clock.Advance(WebhookSignatureVerifier.Tolerance + TimeSpan.FromSeconds(1));

        Assert.Equal(SignatureCheck.Stale, verifier.Verify(header, Payload));
    }

    [Fact]
    public void One_matching_signature_among_several_is_enough_for_secret_rotation()
    {
        var current = Verifier().Sign(Now, Payload);
        var old = Verifier("old").Sign(Now, Payload);
        var header = $"{old},{current.Split(',')[1]}";

        Assert.Equal(SignatureCheck.Valid, Verifier().Verify(header, Payload));
    }

    [Theory]
    [InlineData(null, SignatureCheck.Missing)]
    [InlineData("", SignatureCheck.Missing)]
    [InlineData("garbage", SignatureCheck.Malformed)]
    [InlineData("t=123", SignatureCheck.Malformed)]
    [InlineData("v1=abcd", SignatureCheck.Malformed)]
    [InlineData("t=123,v1=not-hex", SignatureCheck.Malformed)]
    public void Malformed_headers_are_rejected(string? header, SignatureCheck expected)
    {
        Assert.Equal(expected, Verifier().Verify(header, Payload));
    }
}
