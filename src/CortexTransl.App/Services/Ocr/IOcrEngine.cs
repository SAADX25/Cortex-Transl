using System.Drawing;

namespace CortexTransl.App.Services.Ocr;

public interface IOcrEngine
{
    string Id { get; }

    string DisplayName { get; }

    Task<OcrResult> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        string ocrPreset,
        CancellationToken cancellationToken = default);
}
