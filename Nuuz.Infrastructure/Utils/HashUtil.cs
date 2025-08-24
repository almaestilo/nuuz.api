// Nuuz.Infrastructure/Utils/HashUtil.cs
using System.Security.Cryptography;
using System.Text;

namespace Nuuz.Infrastructure.Utils;

public static class HashUtil
{
    public static string UrlHash(string url)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(url.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
