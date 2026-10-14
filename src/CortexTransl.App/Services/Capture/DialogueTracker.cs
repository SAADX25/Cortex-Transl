using CortexTransl.App.Models;
using CortexTransl.App.Utils;

namespace CortexTransl.App.Services.Capture;

public sealed record DialogueObservation(long Sequence, string Text, bool Changed, bool Ready, bool Clear);

// Monotonic debouncing; only OCR noise and typographic equivalents are ignored.
public sealed class DialogueTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private string _candidate = string.Empty;
    private long _changedAt, _lastSeen, _firstSeen;
    private int _observations;
    private bool _expired;
    private bool _progressive;
    private ulong? _visualSignature;
    private long _visualChangedAt;
    private int _visualObservations;
    public bool RequiresOcrConfirmation => _candidate.Length > 0 && _observations < 2;
    public long Sequence { get; private set; }
    public StorySettings Settings { get; set; } = new();

    public DialogueObservation Observe(string text, bool ocrFailed = false, bool freshObservation = true,
        ulong? visualSignature = null)
    {
        var now = _clock.GetTimestamp();
        text = OcrTextCanonicalizer.Normalize(text, preserveLineBreaks: true);
        if (text.Length == 0)
        {
            var grace = ocrFailed ? Settings.OcrFailureGraceMilliseconds : Settings.DisappearanceGraceMilliseconds;
            if (_candidate.Length > 0 && (Elapsed(_lastSeen, now) >= Math.Clamp(grace, 0, 5000)
                || Elapsed(_firstSeen, now) >= Math.Clamp(Settings.MaximumVisibilityMilliseconds, 500, 60000)))
            {
                Reset();
                return new(Sequence, "", true, false, true);
            }
            _observations = 0;
            _visualObservations = 0;
            _changedAt = now;
            return new(Sequence, _candidate, false, false, _candidate.Length == 0 || _expired);
        }

        _lastSeen = now;
        if (DialogueTextAssembler.LooksLikeOcrGlitch(_candidate, text)
            && Elapsed(_changedAt, now) < Settings.EffectiveStability)
            return new(Sequence, _candidate, false, false, _expired);

        var comparison = TextSimilarity.Classify(_candidate, text);
        var changed = comparison != DialogueStability.Unchanged;
        if (changed)
        {
            _progressive = _candidate.Length > 0 && text.StartsWith(_candidate, StringComparison.Ordinal);
            Sequence++;
            _candidate = text;
            _changedAt = _firstSeen = now;
            _observations = 0;
            _expired = false;
        }
        if (visualSignature.HasValue && _visualSignature != visualSignature)
        {
            _visualChangedAt = now;
            _visualObservations = 0;
        }
        if (visualSignature.HasValue) _visualObservations++;
        _visualSignature = visualSignature;
        if (freshObservation) _observations++;
        if (Elapsed(_firstSeen, now) >= Math.Clamp(Settings.MaximumVisibilityMilliseconds, 500, 60000))
        {
            if (!_expired) Sequence++;
            _expired = true;
        }
        // Punctuation can occur mid-typewriter sequence. Even short exclamations
        // require independent, consecutive OCR frames and a quiet visual window.
        var complete = TextSimilarity.LooksComplete(_candidate);
        var quietPeriod = complete ? Settings.EffectiveCompletedStability : Settings.EffectiveStability;
        if (_progressive) quietPeriod = Math.Max(quietPeriod, Settings.FastSubtitles ? 180 : 250);
        var visualReady = !visualSignature.HasValue
            || (_visualObservations >= 2 && Elapsed(_visualChangedAt, now) >= quietPeriod)
            // Dark animated scenery can change the mask despite identical OCR.
            // Three independent readings plus the full text window confirm it.
            || (_observations >= 3 && Elapsed(_changedAt, now) >= Settings.EffectiveStability);
        var ready = !_expired && _observations >= 2 && visualReady && Elapsed(_changedAt, now) >= quietPeriod;
        return new(Sequence, _candidate, changed, ready, changed || _expired);
    }

    public void Reset()
    {
        Sequence++;
        _candidate = "";
        _observations = 0;
        _expired = false;
        _progressive = false;
        _visualSignature = null;
        _visualObservations = 0;
    }

    private double Elapsed(long start, long end) => _clock.GetElapsedTime(start, end).TotalMilliseconds;
}
