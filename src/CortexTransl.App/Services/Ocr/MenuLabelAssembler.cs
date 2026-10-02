using CortexTransl.App.Models;
using CortexTransl.App.Utils;

namespace CortexTransl.App.Services.Ocr;

internal static class MenuLabelAssembler
{
    public static IReadOnlyList<OcrTextBlock> MergeKnownWrappedLabels(IReadOnlyList<OcrTextBlock> blocks)
    {
        var ordered = blocks.OrderBy(block => block.Bounds.Y).ThenBy(block => block.Bounds.X).ToArray();
        var consumed = new HashSet<int>();
        var result = new List<OcrTextBlock>();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (consumed.Contains(index)) continue;
            var chain = new List<int> { index };
            var merged = false;
            for (var line = 1; line < 3; line++)
            {
                var previous = ordered[chain[^1]];
                var next = Enumerable.Range(index + 1, ordered.Length - index - 1)
                    .Where(candidate => !consumed.Contains(candidate) && !chain.Contains(candidate)
                        && IsNextWrappedLine(previous.Bounds, ordered[candidate].Bounds))
                    .OrderBy(candidate => ordered[candidate].Bounds.Y)
                    .ThenBy(candidate => Math.Abs(CenterX(previous.Bounds) - CenterX(ordered[candidate].Bounds)))
                    .DefaultIfEmpty(-1).First();
                if (next < 0) break;
                chain.Add(next);
                var text = string.Join(Environment.NewLine, chain.Select(candidate => ordered[candidate].Text));
                if (!ArabicUiLabels.IsKnownPhrase(text)) continue;

                var pieces = chain.Select(candidate => ordered[candidate].Bounds).ToArray();
                var left = pieces.Min(bounds => bounds.X);
                var top = pieces.Min(bounds => bounds.Y);
                var right = pieces.Max(bounds => bounds.X + bounds.Width);
                var bottom = pieces.Max(bounds => bounds.Y + bounds.Height);
                result.Add(new OcrTextBlock(text, new CaptureRegion(left, top, right - left, bottom - top)));
                consumed.UnionWith(chain);
                merged = true;
                break;
            }

            if (!merged) result.Add(ordered[index]);
        }

        return result;
    }

    private static double CenterX(CaptureRegion bounds) => bounds.X + bounds.Width / 2.0;

    private static bool IsNextWrappedLine(CaptureRegion first, CaptureRegion second)
    {
        var lineHeight = Math.Min(first.Height, second.Height);
        var gap = second.Y - first.Y - first.Height;
        var overlap = Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X);
        return second.Y > first.Y + first.Height * 0.5
            && gap >= -lineHeight * 0.35 && gap <= lineHeight * 0.25
            && overlap >= Math.Min(first.Width, second.Width) * 0.65
            && Math.Abs(CenterX(first) - CenterX(second)) <= Math.Max(12, Math.Max(first.Width, second.Width) * 0.25);
    }
}
