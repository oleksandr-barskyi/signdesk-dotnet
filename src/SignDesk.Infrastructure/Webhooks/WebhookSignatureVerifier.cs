using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SignDesk.Infrastructure.Webhooks;

public enum SignatureCheck
{
    Valid,
    Missing,
    Malformed,
    Stale,
    Mismatch,
}

public sealed class WebhookSignatureVerifier(string secret, TimeProvider clock)
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    public SignatureCheck Verify(string? header, string payload)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return SignatureCheck.Missing;
        }

        long? timestamp = null;
        var candidates = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                return SignatureCheck.Malformed;
            }

            if (kv[0] == "t" && long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (kv[0] == "v1")
            {
                try
                {
                    candidates.Add(Convert.FromHexString(kv[1]));
                }
                catch (FormatException)
                {
                    return SignatureCheck.Malformed;
                }
            }
        }

        if (timestamp is null || candidates.Count == 0)
        {
            return SignatureCheck.Malformed;
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if ((clock.GetUtcNow() - signedAt).Duration() > Tolerance)
        {
            return SignatureCheck.Stale;
        }

        var expected = Compute(timestamp.Value, payload);
        return candidates.Exists(c => CryptographicOperations.FixedTimeEquals(c, expected))
            ? SignatureCheck.Valid
            : SignatureCheck.Mismatch;
    }

    public string Sign(long timestamp, string payload) =>
        $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexStringLower(Compute(timestamp, payload))}";

    private byte[] Compute(long timestamp, string payload) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{payload}"));
}
