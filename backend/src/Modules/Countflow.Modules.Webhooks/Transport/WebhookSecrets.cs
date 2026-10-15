using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
namespace Countflow.Modules.Webhooks.Transport;

public sealed class WebhookSecrets(IConfiguration configuration)
{
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Sign(string body, string secret) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
    private byte[] Key()
    {
        var configured = configuration["Webhooks:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("webhooks.key_not_configured");
        var key = Convert.FromBase64String(configured);
        if (key.Length != 32) throw new InvalidOperationException("webhooks.key_not_configured");
        return key;
    }
    public string Protect(string secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16];
        var input = Encoding.UTF8.GetBytes(secret); var cipher = new byte[input.Length];
        using var aes = new AesGcm(Key(), 16); aes.Encrypt(nonce, input, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public string Reveal(string protectedSecret)
    {
        var data = Convert.FromBase64String(protectedSecret); var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(Key(), 16); aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
