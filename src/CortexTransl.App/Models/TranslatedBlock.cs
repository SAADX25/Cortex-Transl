namespace CortexTransl.App.Models;

public sealed record TranslatedBlock(string OriginalText, string TranslatedText, CaptureRegion Bounds);
