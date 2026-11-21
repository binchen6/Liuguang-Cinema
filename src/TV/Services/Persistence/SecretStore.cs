using System.Security.Cryptography;
using System.Text;

namespace TV.Services.Persistence;

public static class SecretStore
{
    public static string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch (Exception) { return string.Empty; }
    }
}
