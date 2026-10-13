using CortexTransl.App.Models;
using CortexTransl.App.Utils;

namespace CortexTransl.App.Services.Capture;

public sealed record DialogueObservation(long Sequence, string Text, bool Changed, bool Ready, bool Clear);

// Monotonic time and exact text comparisons: semantic edits are never swallowed.
public sealed class DialogueTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private string _candidate = string.Empty;
    private long _changedAt, _lastSeen, _firstSeen;
    private int _observations;
    private bool _expired;
    private bool _progressive;
    public bool RequiresOcrConfirmation => _candidate.Length > 0 && _observations < 2;
    public long Sequence { get; private set; }
    public StorySettings Settings { get; set; } = new();

    public DialogueObservation Observe(string text, bool ocrFailed = false, bool freshObservation = true)
    {
        var now = _clock.GetTimestamp();
        text = OcrTextCanonicalizer.Normalize(text);
        if (text.Length == 0)
        {
            var grace = ocrFailed ? Settings.OcrFailureGraceMilliseconds : Settings.DisappearanceGraceMilliseconds;
            if (_candidate.Length > 0 && (Elapsed(_lastSeen, now) >= Math.Clamp(grace, 0, 5000)
                || Elapsed(_firstSeen, now) >= Math.Clamp(Settings.MaximumVisibilityMilliseconds, 500, 60000)))
            {
                Reset();
                return new(Sequence, "", true, false, true);
            }
            return new(Sequence, _candidate, false, false, _candidate.Length == 0 || _expired);
        }

        _lastSeen = now;
        if (DialogueTextAssembler.LooksLikeOcrGlitch(_candidate, text)
            && Elapsed(_changedAt, now) < Settings.EffectiveStability)
            return new(Sequence, _candidate, false, false, _expired);

        var changed = !_candidate.Equals(text, StringComparison.Ordinal);
        if (changed)
        {
            _progressive = _candidate.Length > 0 && !TextSimilarity.LooksComplete(_candidate)
                && text.StartsWith(_candidate, StringComparison.Ordinal);
            Sequence++;
            _candidate = text;
            _changedAt = _firstSeen = now;
            _observations = 0;
            _expired = false;
        }
        if (freshObservation) _observations++;
        if (Elapsed(_firstSeen, now) >= Math.Clamp(Settings.MaximumVisibilityMilliseconds, 500, 60000))
        {
            if (!_expired) Sequence++;
            _expired = true;
        }
        // Complete short exclamations are immediately eligible. Unpunctuated or
        // growing text must settle across at least two observations.
        var complete = TextSimilarity.LooksComplete(text);
        var quietPeriod = complete ? Settings.EffectiveCompletedStability : Settings.EffectiveStability;
        if (Settings.FastSubtitles)
        {
            if (_progressive) quietPeriod = Math.Max(quietPeriod, complete ? 80 : 180);
            else if (_observations >= 3) quietPeriod = Math.Min(quietPeriod, complete ? 50 : 120);
        }
        var ready = !_expired && ((complete && text.Length <= 16)
            || (_observations >= 2 && Elapsed(_changedAt, now) >= quietPeriod));
        return new(Sequence, text, changed, ready, changed || _expired);
    }

    public void Reset()
    {
        Sequence++;
        _candidate = "";
        _observations = 0;
        _expired = false;
        _progressive = false;
    }

    private double Elapsed(long start, long end) => _clock.GetElapsedTime(start, end).TotalMilliseconds;
}
