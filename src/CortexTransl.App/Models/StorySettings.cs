namespace CortexTransl.App.Models;

public sealed class StorySettings
{
    public bool FastSubtitles { get; set; }
    public bool PerformanceDiagnostics { get; set; }
    public int EffectiveCaptureInterval => FastSubtitles ? 80 : Math.Clamp(CaptureIntervalMilliseconds, 50, 500);
    public int EffectiveStability => FastSubtitles ? 150 : Math.Clamp(StabilityMilliseconds, 100, 2000);
    public int EffectiveCompletedStability => FastSubtitles ? 50 : Math.Clamp(CompletedStabilityMilliseconds, 0, 500);
    public string Speaker { get; set; } = string.Empty;
    public string SceneState { get; set; } = string.Empty;
    public Dictionary<string, string> Terminology { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> SpeakerNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DeepLGlossaryId { get; set; } = string.Empty;
    public int StabilityMilliseconds { get; set; } = 350;
    public int CompletedStabilityMilliseconds { get; set; } = 80;
    public int DisappearanceGraceMilliseconds { get; set; } = 650;
    public int OcrFailureGraceMilliseconds { get; set; } = 1500;
    public int MaximumVisibilityMilliseconds { get; set; } = 15000;
    public int SceneResetMilliseconds { get; set; } = 4000;
    public int CaptureIntervalMilliseconds { get; set; } = 100;
}
