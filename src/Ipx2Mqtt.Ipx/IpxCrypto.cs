using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ipx2Mqtt.Ipx;

/// <summary>
/// Secure login helper reproducing what the IPX web UI does: AES-ECB / PKCS7 with the UTF-8 bytes of the
/// <c>secureKey</c> as key, output encoded in Base64.
/// </summary>
public static class IpxCrypto
{
    public static string EncryptLogin(string login, string password, string secureKey)
    {
        var plain = JsonSerializer.Serialize(new { login, passwd = password });
        return EncryptAesEcb(plain, secureKey);
    }

    public static string EncryptAesEcb(string plaintext, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key); // 16 chars => AES-128
        var cipher = aes.EncryptEcb(Encoding.UTF8.GetBytes(plaintext), PaddingMode.PKCS7);
        return Convert.ToBase64String(cipher);
    }

    public static string DecryptAesEcb(string base64, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        var plain = aes.DecryptEcb(Convert.FromBase64String(base64), PaddingMode.PKCS7);
        return Encoding.UTF8.GetString(plain);
    }
}
