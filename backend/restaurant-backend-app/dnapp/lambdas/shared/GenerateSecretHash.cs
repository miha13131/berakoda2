using System.Security.Cryptography;
using System.Text;

namespace shared;

public class SecretHashGenerator
{
    public static string GenerateSecretHash(
        string username,
        string clientId,
        string clientSecret)
    {
        var message = username + clientId;

        var key = Encoding.UTF8.GetBytes(clientSecret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(messageBytes);

        return Convert.ToBase64String(hash);
    }
}