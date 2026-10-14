using System.Security.Cryptography;
using System.Text;

namespace CortexTransl.App.Utils;

public static class TextHasher
{
    // Cache callers receive the assembled utterance; collapse any remaining
    // display wrapping and OCR noise consistently for reads and writes.
    public static string TranslationKey(string value) => Sha256(OcrTextCanonicalizer.Normalize(value));

    public static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
