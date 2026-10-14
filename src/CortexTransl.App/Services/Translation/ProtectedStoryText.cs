using CortexTransl.App.Utils;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CortexTransl.App.Services.Translation;

// DeepL uses ignored XML elements; plain-text engines use checked opaque tokens.
// Both paths validate every protected occurrence before returning display text.
internal sealed partial record ProtectedStoryText(string Text, IReadOnlyList<string> Terms, bool UsesXml = true)
{
    public static ProtectedStoryText? Create(string text, string source, string target, StoryTranslationContext context,
        bool useXml = true)
    {
        if (!target.Equals("ar", StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith("ar-", StringComparison.OrdinalIgnoreCase)) return null;
        var terms = context.Terminology.Concat(context.SpeakerNames)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .Take(100).GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);
        var pattern = ProtectedSyntax().ToString();
        if (terms.Count > 0)
            pattern += @"|(?<![\p{L}\p{N}_])(?:" + string.Join('|', terms.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])";
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (matches.Count == 0) return null;
        var root = new XElement("cortex-dialogue");
        var plain = new StringBuilder();
        var protectedTerms = new List<string>();
        var position = 0;
        foreach (Match match in matches)
        {
            var literal = text[position..match.Index];
            var replacement = terms.GetValueOrDefault(match.Value, match.Value);
            root.Add(new XText(literal), new XElement("cortex-term", replacement));
            plain.Append(literal).Append("[[ZXQ").Append(protectedTerms.Count).Append("QXZ]]");
            protectedTerms.Add(replacement);
            position = match.Index + match.Length;
        }
        root.Add(new XText(text[position..]));
        plain.Append(text[position..]);
        return new(useXml ? root.ToString(SaveOptions.DisableFormatting) : plain.ToString(), protectedTerms, useXml);
    }

    public string ReadTranslation(string translated)
    {
        try
        {
            translated = DirectionControls().Replace(translated, "");
            return UsesXml ? ReadXml(translated) : ReadTokens(translated);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or RegexMatchTimeoutException)
        {
            throw new TranslationProviderException("The translation did not preserve protected names or variables. Retrying.",
                "invalid protected translation", ex);
        }
    }

    private string ReadXml(string translated)
    {
        translated = XmlTags().Replace(translated, match =>
        {
            var closing = match.Groups["close"].Success || match.Groups["endclose"].Success;
            return "<" + (closing ? "/" : "") + "cortex-" + match.Groups["name"].Value.ToLowerInvariant() + ">";
        });
        using var reader = XmlReader.Create(new StringReader(translated), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 });
        var root = XElement.Load(reader);
        if (root.Name != "cortex-dialogue" || root.Descendants().Any(x => x.Name != "cortex-term" || x.HasElements))
            throw new XmlException("Unexpected protected markup.");
        var remaining = Terms.ToList();
        foreach (var element in root.Elements("cortex-term"))
        {
            var value = TextNormalizer.Normalize(element.Value);
            var index = remaining.FindIndex(term => TextNormalizer.Normalize(term).Equals(value, StringComparison.OrdinalIgnoreCase));
            var prefix = "";
            var suffix = "";
            if (index < 0)
            {
                // Some engines attach adjacent sentence punctuation to ignored
                // content. Keep it outside the restored spelling semantically.
                index = remaining.FindIndex(term =>
                {
                    var canonical = TextNormalizer.Normalize(term);
                    var at = value.IndexOf(canonical, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) return false;
                    var before = value[..at];
                    var after = value[(at + canonical.Length)..];
                    if (!before.Concat(after).All(character => char.IsWhiteSpace(character) || char.IsPunctuation(character))) return false;
                    prefix = before;
                    suffix = after;
                    return true;
                });
            }
            if (index < 0) throw new XmlException("Protected content was altered or duplicated.");
            element.Value = prefix + remaining[index] + suffix;
            remaining.RemoveAt(index);
        }
        if (remaining.Count != 0) throw new XmlException("Protected content was omitted.");
        return root.Value.Trim();
    }

    private string ReadTokens(string translated)
    {
        var counts = new int[Terms.Count];
        var matches = PlainTokens().Matches(translated);
        var ids = new List<int>();
        foreach (Match match in matches)
        {
            var digits = string.Concat(match.Groups["id"].Value.Where(char.IsDigit)
                .Select(character => ((int)char.GetNumericValue(character)).ToString(CultureInfo.InvariantCulture)));
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id >= counts.Length || ++counts[id] != 1) throw new FormatException("Unknown or duplicated protected token.");
            ids.Add(id);
        }
        var remainder = PlainTokens().Replace(translated, "");
        if (counts.Any(count => count != 1) || TokenResidue().IsMatch(remainder))
            throw new FormatException("Protected tokens were omitted or corrupted.");
        var index = 0;
        return PlainTokens().Replace(translated, _ => Terms[ids[index++]]).Trim();
    }

    [GeneratedRegex(@"\{\{[^{}\r\n]+\}\}|\{[^{}\r\n]+\}|<[^<>\r\n]+>|\[[^\[\]\r\n]+\]|%(?:\w+%|\d+\$[a-z]|[a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ProtectedSyntax();

    [GeneratedRegex(@"[\u200E\u200F\u202A-\u202E\u2066-\u2069]")]
    private static partial Regex DirectionControls();

    [GeneratedRegex(@"(?:[<>]|&lt;|&gt;)\s*(?<close>/\s*)?cortex[\s_-]*(?<name>term|dialogue)\s*(?<endclose>/\s*)?(?:[<>]|&lt;|&gt;)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XmlTags();

    [GeneratedRegex(@"(?:[\[\]<>_]\s*){0,2}Z\s*X\s*Q\s*(?<id>\p{Nd}(?:\s*\p{Nd})*)\s*Q\s*X\s*Z(?:\s*[\[\]<>_]){0,2}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainTokens();

    [GeneratedRegex(@"Z\s*X\s*Q|Q\s*X\s*Z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenResidue();
}
