using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ProxyIdP.Services;

public static class Crypto
{
    public static string Sha256(string value) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string RandomToken(int bytes = 32) =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
