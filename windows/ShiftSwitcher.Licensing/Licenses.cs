using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace ShiftSwitcher.Licensing;

public sealed record LicensePayload(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("issued_at")] long IssuedAt,
    [property: JsonPropertyName("valid_until")] long ValidUntil,
    [property: JsonPropertyName("key_id")] string KeyId,
    [property: JsonPropertyName("environment")] string Environment);

public static class LicenseCheck
{
    public const string Product = "shift-switcher-pro-v1";
    public static bool ValidKey(string key) => key.Length is >= 30 and <= 100 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static string Device(string installation) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(installation)));

    public static LicensePayload? Verify(string certificate, string device, IReadOnlyDictionary<string, string> keys, string environment, long now)
    {
        if (string.IsNullOrEmpty(certificate) || certificate.Length > 16384 || now < 0 || environment is not ("DEMO" or "LIVE") ||
            device.Length != 64 || device.Any(c => !char.IsAsciiHexDigitLower(c))) return null;
        try
        {
            var parts = certificate.Split('.');
            if (parts.Length != 3 || parts[0] != "ss1") return null;
            var body = Decode(parts[1]); var signature = Decode(parts[2]);
            var payload = JsonSerializer.Deserialize<LicensePayload>(body);
            if (payload is null || payload.Schema != 1 || payload.Product != Product || payload.Device != device ||
                payload.Environment != environment || string.IsNullOrEmpty(payload.License) || string.IsNullOrEmpty(payload.KeyId) ||
                payload.IssuedAt < 0 || payload.IssuedAt > now + 300 || payload.ValidUntil <= now || payload.ValidUntil <= payload.IssuedAt ||
                !keys.TryGetValue(environment + ":" + payload.KeyId, out var encoded)) return null;
            var maximum = payload.Kind switch { null => 31L * 86400, "owner" when payload.License.StartsWith("owner_", StringComparison.Ordinal) => 366L * 86400, _ => 0 };
            if (payload.ValidUntil - payload.IssuedAt > maximum || signature.Length != 64) return null;
            var key = Convert.FromBase64String(encoded);
            if (key.Length != 32) return null;
            var verifier = new Ed25519Signer(); verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            var message = Encoding.UTF8.GetBytes("ss1." + parts[1]); verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature) ? payload : null;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or InvalidOperationException)
        { return null; }
    }
    private static byte[] Decode(string value)
    {
        if (value.Length == 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException();
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    }
}

public sealed record TrialState(long Started, long Latest)
{
    public int DaysRemaining(long now)
    {
        if (now < 0 || Started < 0 || Latest < Started || now < Latest - 300) return 0;
        var elapsed = Math.Max(Latest, now) - Started;
        if (elapsed >= 14L * 86400) return 0;
        return (int)((14L * 86400 - elapsed + 86399) / 86400);
    }
    public TrialState Observe(long now) => this with { Latest = Math.Max(Latest, now) };
}
