using System.Security.Cryptography;
using System.Text;
using MambaSplit.Api.Exceptions;

namespace MambaSplit.Api.Security;

public static class TokenCodec
{
    public static string RandomUrlToken(int bytes)
    {
        if (bytes < 16)
        {
            throw new ValidationException("Token size too small");
        }

        var raw = RandomNumberGenerator.GetBytes(bytes);
        return Base64UrlEncode(raw);
    }

    public static string Sha256Base64Url(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
