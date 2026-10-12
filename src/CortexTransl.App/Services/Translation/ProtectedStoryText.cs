using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.IO;

namespace CortexTransl.App.Services.Translation;

// DeepL's documented XML ignore_tags protects player-supplied Arabic spellings
// inside complete sentences, without replacing text after translation.
internal sealed record ProtectedStoryText(string Xml, IReadOnlyList<string> Terms)
{
    public static ProtectedStoryText? Create(string text, string source, string target, StoryTranslationContext context)
    {
        if (!source.Equals("en", StringComparison.OrdinalIgnoreCase) || !target.Equals("ar", StringComparison.OrdinalIgnoreCase)) return null;
        var terms = context.Terminology.Concat(context.SpeakerNames)
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .Take(100).GroupBy(x => x.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);
        if (terms.Count == 0) return null;
        var pattern = @"(?<![\p{L}\p{N}_])(?:" + string.Join('|', terms.Keys.OrderByDescending(x => x.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])";
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (matches.Count == 0) return null;
        var root = new XElement("cortex-dialogue");
        var protectedTerms = new List<string>();
        var position = 0;
        foreach (Match match in matches)
        {
            root.Add(new XText(text[position..match.Index]));
            var arabic = terms[match.Value];
            root.Add(new XElement("cortex-term", arabic));
            protectedTerms.Add(arabic);
            position = match.Index + match.Length;
        }
        root.Add(new XText(text[position..]));
        return new(root.ToString(SaveOptions.DisableFormatting), protectedTerms);
    }

    public string ReadTranslation(string translated)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(translated), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 });
            var root = XElement.Load(reader);
            var returned = root.Descendants("cortex-term").Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal);
            if (root.Name != "cortex-dialogue" || !returned.SequenceEqual(Terms.OrderBy(x => x, StringComparer.Ordinal)))
                throw new XmlException("Protected terminology was altered or omitted.");
            return root.Value.Trim();
        }
        catch (XmlException ex)
        {
            throw new TranslationProviderException("DeepL did not preserve the configured terminology. Retrying.", "DeepL invalid terminology result", ex);
        }
    }
}
