using System.Security.Cryptography;
using System.Text;

namespace KefRemote.App;

/// <summary>Encrypts the device password for the current Windows user (DPAPI). Settings only ever hold the ciphertext.</summary>
static class Secrets
{
    public static string? Protect(string? plain) => string.IsNullOrEmpty(plain)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Copied from another account or machine. The user enters the password again.
            return "";
        }
    }
}
