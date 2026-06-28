namespace CortexTransl.App.Models;

public sealed record PipelineSettings(
    string SourceLanguage,
    string TargetLanguage,
    string OcrEngine,
    string TranslationProvider,
    string TranslationMode,
    string OcrPreset);
