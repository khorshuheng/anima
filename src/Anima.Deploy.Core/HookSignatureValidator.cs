using System.Security.Cryptography;
using System.Text;

namespace Anima.Deploy;

/// <summary>
/// Verifies HMAC-SHA256 hook signatures. GitHub sends <c>sha256=&lt;hex&gt;</c>,
/// Gitea sends raw hex; the generic provider accepts either.
/// </summary>
public static class HookSignatureValidator
{
    public static bool Verify(string? secret, string payload, string? signature)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        string value = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature[7..]
            : signature;

        string expected = Compute(secret, payload);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(value.ToLowerInvariant()));
    }

    public static string Compute(string secret, string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
