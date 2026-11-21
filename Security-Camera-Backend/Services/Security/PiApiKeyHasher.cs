using System.Security.Cryptography;
using System.Text;

namespace Security_Camera_Backend.Services.Security;

/// <summary>Hashes Pi API keys before persistence and compares them without timing leaks.</summary>
public static class PiApiKeyHasher
{
    /// <summary>Uppercase hexadecimal SHA-256 digest, matching the <c>PiDevices.ApiKeyHash</c> column.</summary>
    public static string HashToHex(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    public static byte[] Hash(string apiKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

    /// <summary>Constant-time comparison of two raw API keys.</summary>
    public static bool FixedTimeEquals(string? left, string? right)
    {
        var leftHash = Hash(left ?? string.Empty);
        var rightHash = Hash(right ?? string.Empty);

        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
