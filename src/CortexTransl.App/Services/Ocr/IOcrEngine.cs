using System.Drawing;

namespace CortexTransl.App.Services.Ocr;

public interface IOcrEngine
{
    string Id { get; }

    Task<OcrResult> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default);
}
