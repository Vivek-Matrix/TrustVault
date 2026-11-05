using System.Security.Cryptography;

namespace TrustVault.Api.Services;

public static class TemporaryPasswordGenerator
{
    public static string Generate()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@$%*-_";
        var alphabet = upper + lower + digits + symbols;
        var password = new char[32];
        password[0] = Pick(upper);
        password[1] = Pick(lower);
        password[2] = Pick(digits);
        password[3] = Pick(symbols);
        for (var index = 4; index < password.Length; index++)
        {
            password[index] = Pick(alphabet);
        }

        for (var index = password.Length - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (password[index], password[swapIndex]) = (password[swapIndex], password[index]);
        }

        return new string(password);

        static char Pick(string source) => source[RandomNumberGenerator.GetInt32(source.Length)];
    }
}
