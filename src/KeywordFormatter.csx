#nullable enable
// KeywordFormatter.csx
// Formats keywords/tags as camelCase purely for note.md presentation. Underlying data files
// (keywords.json, vocabulary.json) always keep the original/canonical (trimmed, lowercased)
// term — this formatting is display-only and never round-tripped back into stored data.

using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public static class KeywordFormatter
{
    private static readonly Regex WordSplitRegex = new Regex(@"[\s_\-/]+", RegexOptions.Compiled);

    /// <summary>
    /// Converts a keyword/phrase into camelCase, e.g. "cold brew" -> "coldBrew",
    /// "Bloomridge" -> "bloomridge", "AI nástroje" -> "aiNástroje". Preserves non-ASCII
    /// characters (e.g. diacritics) within each word.
    /// </summary>
    public static string ToCamelCase(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return term;
        }

        var words = WordSplitRegex.Split(term.Trim())
            .Where(w => !string.IsNullOrEmpty(w))
            .ToList();

        if (words.Count == 0)
        {
            return term;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (i == 0)
            {
                sb.Append(LowerFirst(word));
            }
            else
            {
                sb.Append(UpperFirst(word));
            }
        }

        return sb.ToString();
    }

    private static string LowerFirst(string word)
    {
        if (word.Length == 0) return word;
        return char.ToLowerInvariant(word[0]) + word.Substring(1).ToLowerInvariant();
    }

    private static string UpperFirst(string word)
    {
        if (word.Length == 0) return word;
        return char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant();
    }
}
