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

        return false;
    }

}
