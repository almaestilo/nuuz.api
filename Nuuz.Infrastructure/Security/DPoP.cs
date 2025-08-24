using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nuuz.Infrastructure.Security;

/// <summary>
/// DPoP helper: P-256 keygen, JWK thumbprint, and DPoP proof (JWT) creation
/// with optional nonce and "ath" (access token hash) support. Handles both DER
/// and P-1363 (raw R||S) signatures from ECDSA providers.
/// </summary>
public static class DPoP
{
    public sealed class P256Key
    {
        public string PrivateJwkJson { get; init; } = default!;
        public string Thumbprint { get; init; } = default!;
    }

    private sealed class EcJwk
    {
        [JsonPropertyName("kty")] public string Kty { get; init; } = "EC";
        [JsonPropertyName("crv")] public string Crv { get; init; } = "P-256";
        [JsonPropertyName("x")] public string X { get; init; } = default!;
        [JsonPropertyName("y")] public string Y { get; init; } = default!;
        [JsonPropertyName("d")] public string? D { get; init; }
    }

    public static P256Key GenerateP256()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdsa.ExportParameters(true);
        var pub = ecdsa.ExportParameters(false);

        string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var x = B64Url(pub.Q.X!);
        var y = B64Url(pub.Q.Y!);
        var d = B64Url(priv.D!);

        var privateJwk = new EcJwk { X = x, Y = y, D = d };
        var privateJson = JsonSerializer.Serialize(privateJwk);

        var publicJwk = new EcJwk { X = x, Y = y, D = null };

        // RFC 7638 thumbprint over {"crv","kty","x","y"} (lexicographic order)
        string Canonicalize(EcJwk jwk) =>
            $"{{\"crv\":\"{jwk.Crv}\",\"kty\":\"{jwk.Kty}\",\"x\":\"{jwk.X}\",\"y\":\"{jwk.Y}\"}}";

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(publicJwk)));
        var jkt = B64Url(digest);

        return new P256Key { PrivateJwkJson = privateJson, Thumbprint = jkt };
    }

    /// <summary>
    /// Create a DPoP proof JWT. If you pass a non-empty accessToken, we include
    /// ath = base64url( SHA-256(accessToken) ).
    /// </summary>
    public static string CreateProof(
        string httpMethod,
        string url,
        string privateJwkJson,
        string? nonce = null,
        string? accessTokenForAth = null)
    {
        // Parse private JWK
        var jwk = JsonSerializer.Deserialize<EcJwk>(privateJwkJson)
                  ?? throw new InvalidOperationException("Invalid private JWK JSON.");

        if (string.IsNullOrWhiteSpace(jwk.D) || string.IsNullOrWhiteSpace(jwk.X) || string.IsNullOrWhiteSpace(jwk.Y))
            throw new InvalidOperationException("Private JWK must include d, x, and y.");

        static byte[] FromB64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = FromB64Url(jwk.X), Y = FromB64Url(jwk.Y) },
            D = FromB64Url(jwk.D!)
        });

        // Header (public JWK only)
        var headerObj = new
        {
            alg = "ES256",
            typ = "dpop+jwt",
            jwk = new { kty = "EC", crv = "P-256", x = jwk.X, y = jwk.Y }
        };

        // Payload
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object?>
        {
            ["htm"] = httpMethod.ToUpperInvariant(),
            ["htu"] = url,
            ["iat"] = now,
            ["jti"] = Guid.NewGuid().ToString("N")
        };
        if (!string.IsNullOrWhiteSpace(nonce)) payload["nonce"] = nonce;
        if (!string.IsNullOrWhiteSpace(accessTokenForAth))
        {
            var athHash = SHA256.HashData(Encoding.ASCII.GetBytes(accessTokenForAth));
            payload["ath"] = ToB64Url(athHash);
        }

        var header = ToB64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headerObj)));
        var body = ToB64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        var signingInput = $"{header}.{body}";

        // Sign; providers may return DER or P-1363. Normalize to JOSE R||S (64 bytes).
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);

        byte[] joseSig = sig.Length switch
        {
            64 => sig,                     // already raw P-1363: 32-byte R || 32-byte S
            _ => DerToJose(sig, 32)       // DER -> raw
        };

        return $"{signingInput}.{ToB64Url(joseSig)}";

        static string ToB64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Convert a DER-encoded ECDSA signature (SEQUENCE { r, s }) to raw P-1363 R||S with fixed component length.
    /// </summary>
    private static byte[] DerToJose(byte[] der, int compLen)
    {
        int idx = 0;
        if (der[idx++] != 0x30) throw new InvalidOperationException("Invalid DER ECDSA signature.");
        _ = ReadLen(der, ref idx);

        if (der[idx++] != 0x02) throw new InvalidOperationException("Invalid DER ECDSA signature (r).");
        var rLen = ReadLen(der, ref idx);
        var r = ReadIntAsFixed(der, ref idx, rLen, compLen);

        if (der[idx++] != 0x02) throw new InvalidOperationException("Invalid DER ECDSA signature (s).");
        var sLen = ReadLen(der, ref idx);
        var s = ReadIntAsFixed(der, ref idx, sLen, compLen);

        var outSig = new byte[compLen * 2];
        Buffer.BlockCopy(r, 0, outSig, 0, compLen);
        Buffer.BlockCopy(s, 0, outSig, compLen, compLen);
        return outSig;

        static int ReadLen(byte[] b, ref int i)
        {
            int len = b[i++];
            if ((len & 0x80) == 0) return len;
            int n = len & 0x7F;
            if (n == 0 || n > 4) throw new InvalidOperationException("Invalid DER length.");
            len = 0;
            for (int k = 0; k < n; k++) len = (len << 8) | b[i++];
            return len;
        }

        static byte[] ReadIntAsFixed(byte[] b, ref int i, int len, int fixedLen)
        {
            if (len > 0 && b[i] == 0x00) { i++; len--; }
            if (len > fixedLen) throw new InvalidOperationException("Invalid DER INTEGER length.");
            var outBytes = new byte[fixedLen];
            Buffer.BlockCopy(b, i, outBytes, fixedLen - len, len);
            i += len;
            return outBytes;
        }
    }
}
