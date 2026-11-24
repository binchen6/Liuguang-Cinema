using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TV.Services.Cloud;

/// <summary>
/// Generates persistent per-account UC PC client device identifiers (24-byte UTDID)
/// and derives the <c>ut</c> query token expected by <c>pc-api.uc.cn/1/clouddrive/file/download</c>.
/// Without a valid <c>ut</c> device token, UC Drive serves a ~14 MB "use UC Browser / buy VIP"
/// placeholder video for original-quality video downloads on many accounts.
/// </summary>
internal static class UcDeviceToken
{
    private const ushort TokenPrefix = 13801;

    // Protocol constant: ASCII of the first 16 hex digits of MD5("C88EAC2B-F07C-4FE2-ABDF-C8F049C46C7B").
    private static readonly byte[] CipherKey = "2106f55fd41e800c"u8.ToArray();

    /// <summary>
    /// Creates a new printable 24-character UTDID (18 random bytes Base64-encoded).
    /// </summary>
    public static string NewDeviceId()
    {
        Span<byte> random = stackalloc byte[18];
        RandomNumberGenerator.Fill(random);
        return Convert.ToBase64String(random);
    }

    public static bool IsValidDeviceId(string? utdid) =>
        !string.IsNullOrEmpty(utdid) &&
        utdid.Length == 24 &&
        Encoding.ASCII.GetByteCount(utdid) == 24;

    /// <summary>
    /// Derives the Base64 <c>ut</c> token: <c>Base64(UInt16BE(13801) || AES-128-CBC-PKCS7(UTDID, key, iv=key))</c>.
    /// </summary>
    public static string CreateToken(string utdid)
    {
        if (!IsValidDeviceId(utdid))
            throw new ArgumentException("UC UTDID must be 24 ASCII characters.", nameof(utdid));

        var plaintext = Encoding.ASCII.GetBytes(utdid);
        using var aes = Aes.Create();
        aes.Key = CipherKey;
        aes.IV = CipherKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        var encrypted = aes.EncryptCbc(plaintext, CipherKey, PaddingMode.PKCS7);

        var token = new byte[2 + encrypted.Length];
        BinaryPrimitives.WriteUInt16BigEndian(token.AsSpan(0, 2), TokenPrefix);
        encrypted.CopyTo(token.AsSpan(2));
        return Convert.ToBase64String(token);
    }
}
