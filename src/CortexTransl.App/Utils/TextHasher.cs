using System.Security.Cryptography;
using System.Text;

namespace CortexTransl.App.Utils;

public static class TextHasher
{
    public static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
