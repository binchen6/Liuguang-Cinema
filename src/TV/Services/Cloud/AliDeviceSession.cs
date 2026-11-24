using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace TV.Services.Cloud;

/// <summary>
/// Minimal zero-dependency secp256k1 ECDSA session signer for Alipan web API device sessions
/// (<c>POST /users/v1/users/device/create_session</c> + <c>x-device-id</c> / <c>x-signature</c>).
/// </summary>
internal sealed class AliDeviceSession
{
    private const string WebAppId = "5dde4e1bdf9e4966b387ba58f4b3fdc3";

    private static readonly BigInteger P = ParseUnsignedHex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F");
    private static readonly BigInteger N = ParseUnsignedHex("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");
    private static readonly BigInteger HalfN = N >> 1;
    private static readonly Point G = new(
        ParseUnsignedHex("79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798"),
        ParseUnsignedHex("483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8"),
        false);

    private readonly BigInteger _privateKey;

    private AliDeviceSession(string deviceId, string userId, BigInteger privateKey, string publicKeyHex, string signatureHex)
    {
        _privateKey = privateKey;
        DeviceId = deviceId;
        UserId = userId;
        PublicKeyHex = publicKeyHex;
        SignatureHex = signatureHex;
    }

    public string DeviceId { get; }
    public string UserId { get; }
    public string PublicKeyHex { get; }
    public string SignatureHex { get; }

    public static AliDeviceSession Create(string userId, string? existingDeviceId = null)
    {
        var deviceId = !string.IsNullOrWhiteSpace(existingDeviceId)
            ? existingDeviceId.Trim()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var privateKey = GenerateScalar();
        var publicPoint = Multiply(G, privateKey);
        var publicKeyHex = "04" + ToFixedHex(publicPoint.X) + ToFixedHex(publicPoint.Y);
        var signatureHex = SignSession(privateKey, deviceId, userId, nonce: 0);
        return new AliDeviceSession(deviceId, userId, privateKey, publicKeyHex, signatureHex);
    }

    public bool VerifySelf()
    {
        var hash = HashMessage(DeviceId, UserId, nonce: 0);
        var z = new BigInteger(hash, isUnsigned: true, isBigEndian: true);
        var r = ParseUnsignedHex(SignatureHex[..64]);
        var s = ParseUnsignedHex(SignatureHex[64..128]);
        if (r <= 0 || r >= N || s <= 0 || s >= N) return false;
        var w = ModInverse(s, N);
        var u1 = Mod(z * w, N);
        var u2 = Mod(r * w, N);
        var pub = Multiply(G, _privateKey);
        var check = Add(Multiply(G, u1), Multiply(pub, u2));
        return !check.Infinity && Mod(check.X, N) == r;
    }

    private static string SignSession(BigInteger privateKey, string deviceId, string userId, int nonce)
    {
        var hash = HashMessage(deviceId, userId, nonce);
        var z = new BigInteger(hash, isUnsigned: true, isBigEndian: true);
        while (true)
        {
            var k = GenerateScalar();
            var point = Multiply(G, k);
            var r = Mod(point.X, N);
            if (r.IsZero) continue;
            var s = Mod(ModInverse(k, N) * (z + r * privateKey), N);
            if (s.IsZero) continue;
            if (s > HalfN) s = N - s;
            return ToFixedHex(r) + ToFixedHex(s) + "01";
        }
    }

    private static byte[] HashMessage(string deviceId, string userId, int nonce) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{WebAppId}:{deviceId}:{userId}:{nonce.ToString(CultureInfo.InvariantCulture)}"));

    private static BigInteger GenerateScalar()
    {
        Span<byte> bytes = stackalloc byte[32];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            if (value >= 1 && value < N) return value;
        }
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, bool Infinity)
    {
        public static readonly Point Zero = new(BigInteger.Zero, BigInteger.Zero, true);
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        var result = Point.Zero;
        var addend = point;
        var k = scalar;
        while (k > 0)
        {
            if (!k.IsEven) result = Add(result, addend);
            addend = Double(addend);
            k >>= 1;
        }
        return result;
    }

    private static Point Double(Point p)
    {
        if (p.Infinity || p.Y.IsZero) return Point.Zero;
        var lambda = Mod(3 * p.X * p.X * ModInverse(Mod(2 * p.Y, P), P), P);
        var x = Mod(lambda * lambda - 2 * p.X, P);
        var y = Mod(lambda * (p.X - x) - p.Y, P);
        return new Point(x, y, false);
    }

    private static Point Add(Point a, Point b)
    {
        if (a.Infinity) return b;
        if (b.Infinity) return a;
        if (a.X == b.X)
            return Mod(a.Y + b.Y, P).IsZero ? Point.Zero : Double(a);
        var lambda = Mod((b.Y - a.Y) * ModInverse(Mod(b.X - a.X, P), P), P);
        var x = Mod(lambda * lambda - a.X - b.X, P);
        var y = Mod(lambda * (a.X - x) - a.Y, P);
        return new Point(x, y, false);
    }

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var rem = value % modulus;
        return rem.Sign < 0 ? rem + modulus : rem;
    }

    private static BigInteger ModInverse(BigInteger value, BigInteger modulus)
    {
        var a = Mod(value, modulus);
        return BigInteger.ModPow(a, modulus - 2, modulus);
    }

    private static BigInteger ParseUnsignedHex(string hex) =>
        BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string ToFixedHex(BigInteger value)
    {
        Span<byte> buffer = stackalloc byte[32];
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length >= 32) raw[^32..].CopyTo(buffer);
        else raw.CopyTo(buffer[(32 - raw.Length)..]);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }
}
