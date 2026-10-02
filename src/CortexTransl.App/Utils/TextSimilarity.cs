namespace CortexTransl.App.Utils;

public enum DialogueStability
{
    Unchanged,
    Confirm,
    Changed
}

public static class TextSimilarity
{
    public static DialogueStability Classify(string previous, string current)
    {
        var first = TextNormalizer.Normalize(previous);
        var second = TextNormalizer.Normalize(current);

        if (first.Length == 0 && second.Length == 0)
        {
            return DialogueStability.Unchanged;
        }

        if (first.Length == 0)
        {
            return DialogueStability.Changed;
        }

        if (first.Equals(second, StringComparison.Ordinal))
        {
            return DialogueStability.Unchanged;
        }

        var maxLength = Math.Max(first.Length, second.Length);
        var distance = Levenshtein(first, second);
        var noise = Math.Max(1, (int)Math.Ceiling(maxLength * 0.02));
        if (distance <= noise)
        {
            return DialogueStability.Unchanged;
        }

        if (IsProgressiveChange(first, second))
        {
            return DialogueStability.Confirm;
        }

        return DialogueStability.Changed;
    }

    public static bool LooksComplete(string text)
    {
        var value = TextNormalizer.Normalize(text);
        if (value.Length < 2)
        {
            return false;
        }

        var last = value[^1];
        return last is '.' or '!' or '?' or '…' or '。' or '！' or '？' or '」' or '』';
    }

    private static bool IsProgressiveChange(string first, string second)
    {
        var shorter = first.Length <= second.Length ? first : second;
        var longer = first.Length <= second.Length ? second : first;
        if (shorter.Length == 0)
        {
            return false;
        }

        if (longer.StartsWith(shorter, StringComparison.Ordinal))
        {
            return true;
        }

        return shorter.Length >= 8 && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
