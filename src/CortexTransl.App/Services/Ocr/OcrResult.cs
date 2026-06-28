using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Ocr;

public sealed record OcrResult(string Text, IReadOnlyList<RecognizedTextBlock> Blocks)
{
    public OcrResult(string text)
        : this(text, [])
    {
    }
}
