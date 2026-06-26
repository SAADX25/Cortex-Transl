namespace CortexTransl.App.Models;

public sealed class GameProfile
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public CaptureRegion Region { get; set; } = CaptureRegion.Empty;

    public string SourceLanguage { get; set; } = "en";

    public string TargetLanguage { get; set; } = "ar";

    public string OcrEngine { get; set; } = "windows";

    public string TranslationProvider { get; set; } = "placeholder";

    public override string ToString()
    {
        return Name;
    }
}
