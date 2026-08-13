using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Ocr;

public sealed record OcrResult(string Text, IReadOnlyList<OcrTextBlock> Blocks)
{
    public OcrResult(string text)
        : this(text, [])
    {
    }
}
