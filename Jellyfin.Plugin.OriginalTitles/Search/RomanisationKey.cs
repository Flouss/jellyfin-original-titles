using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Extensions;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// Reduces a word to a coarse "romanisation key" so that colloquial Latin spellings of Arabic words (Arabizi) and the
/// server's ICU transliteration of the original title land on the same key. Pure logic apart from
/// <see cref="TransliteratedKeysOf"/>, which calls the same ICU transliterator the server uses for sort names.
/// </summary>
/// <remarks>
/// Rule order for one word (each step is pinned by a test in RomanisationKeyTests):
/// lowercase; drop U+02BF/U+02BE and apostrophes; strip a leading <c>al</c>/<c>el</c> (no hyphen, only when at least
/// <see cref="MinArticleRest"/> characters remain); digits (<c>3</c>/<c>2</c> dropped, <c>7</c> to <c>h</c>, <c>5</c> to <c>kh</c>,
/// <c>9</c> to <c>q</c>); digraphs (kh to x, sh/ch to c, th to t, dh/dz to d, gh to g); drop <c>a e i o u</c> and <c>w</c>/<c>y</c>
/// everywhere except at the start of the word (ruling R7); collapse doubled letters last.
/// A hyphenated article (<c>al-</c>, <c>el-</c>, <c>l-</c>) is stripped by <see cref="KeysOf"/>; a bare <c>l</c> is never an article.
/// Blind spots (see the Task 6 report): dropping w/y raises key collisions among short words (<c>bayt</c> and <c>bt</c>),
/// unmapped digits (0, 1, 4, 6, 8) stay as characters, and a word whose key is empty (for example <c>3</c> or <c>a</c>) is ignored.
/// </remarks>
public static class RomanisationKey
{
    /// <summary>Minimum number of characters that must remain after stripping an unhyphenated al/el article.</summary>
    private const int MinArticleRest = 3;

    private const char AynModifier = '\u02BF';
    private const char HamzaModifier = '\u02BE';

    // Serialises ICU use: the transliterator's first concurrent calls can return the input UNtransliterated (measured: 18 of 40
    // process runs with 8 racing threads, 0 of 40 behind this lock). Guards only calls made through this class; it does not cover
    // other callers of Transliterated() (for example the server's own sort-name code) racing on first use.
    private static readonly object TransliteratorGate = new();

    private static readonly Regex HyphenatedArticle = new(@"^(?:al|el|l)-(?=.)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Matched on the lower-cased word, before any key rule runs.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "al", "el" };

    private static readonly (string Pair, char Replacement)[] Digraphs =
    {
        ("kh", 'x'),
        ("sh", 'c'),
        ("ch", 'c'),
        ("th", 't'),
        ("dh", 'd'),
        ("dz", 'd'),
        ("gh", 'g'),
    };

    /// <summary>
    /// Computes the key of one word (no whitespace). Returns an empty string when nothing is left (for example <c>3</c> or <c>aeiou</c>).
    /// </summary>
    /// <param name="word">A single word.</param>
    /// <returns>The key, possibly empty.</returns>
    public static string KeyOf(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return string.Empty;
        }

        var w = StripMarks(word.ToLowerInvariant());

        if ((w.StartsWith("al", StringComparison.Ordinal) || w.StartsWith("el", StringComparison.Ordinal))
            && w.Length - 2 >= MinArticleRest)
        {
            w = w.Substring(2);
        }

        w = MapDigits(w);
        w = MergeDigraphs(w);
        w = DropVowels(w);
        return CollapseDoubles(w);
    }

    /// <summary>
    /// Tokenises Latin text into keys: whitespace and punctuation separate words, a hyphenated leading article is stripped,
    /// the stopwords <c>al</c>/<c>el</c> are ignored and words whose key is empty are skipped. Never throws.
    /// </summary>
    /// <param name="text">Latin text (a query, or an already transliterated title); may be null.</param>
    /// <returns>The keys in word order, duplicates kept; empty when there is nothing to key.</returns>
    public static IReadOnlyList<string> KeysOf(string? text)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return keys;
        }

        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = HyphenatedArticle.Replace(StripMarks(raw.ToLowerInvariant()), string.Empty);
            foreach (var piece in SplitOnNonAlphanumeric(cleaned))
            {
                if (StopWords.Contains(piece))
                {
                    continue;
                }

                var key = KeyOf(piece);
                if (key.Length > 0)
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Transliterates <paramref name="text"/> with the server's own ICU transliterator
    /// (<c>string.Transliterated()</c> from Jellyfin.Extensions, the same call <c>BaseItem.GetSortName</c> makes), then keys it.
    /// Used for the original title and, symmetrically, for the query. <c>GetSortName</c> itself is not used because it
    /// zero-pads digit runs (which would break the <c>7</c> to <c>h</c> mapping) and depends on server configuration.
    /// </summary>
    /// <param name="text">Text in any script; may be null.</param>
    /// <returns>The keys, as <see cref="KeysOf"/>.</returns>
    public static IReadOnlyList<string> TransliteratedKeysOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        string latin;
        lock (TransliteratorGate)
        {
            latin = text.Transliterated();
        }

        return KeysOf(latin);
    }

    private static string StripMarks(string lowered)
    {
        // U+02BF / U+02BE are what ICU emits for ayn / hamza, the letters Arabizi writes as 3 / 2 (which are dropped too).
        return lowered
            .Replace(AynModifier.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(HamzaModifier.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("’", string.Empty, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitOnNonAlphanumeric(string text)
    {
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string MapDigits(string w)
    {
        var sb = new StringBuilder(w.Length + 2);
        foreach (var c in w)
        {
            switch (c)
            {
                case '3':
                case '2':
                    break;
                case '7':
                    sb.Append('h');
                    break;
                case '5':
                    sb.Append("kh");
                    break;
                case '9':
                    sb.Append('q');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string MergeDigraphs(string w)
    {
        var sb = new StringBuilder(w.Length);
        var i = 0;
        while (i < w.Length)
        {
            var merged = false;
            if (i + 1 < w.Length)
            {
                foreach (var (pair, replacement) in Digraphs)
                {
                    if (w[i] == pair[0] && w[i + 1] == pair[1])
                    {
                        sb.Append(replacement);
                        i += 2;
                        merged = true;
                        break;
                    }
                }
            }

            if (!merged)
            {
                sb.Append(w[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private static string DropVowels(string w)
    {
        var sb = new StringBuilder(w.Length);
        for (var i = 0; i < w.Length; i++)
        {
            var c = w[i];
            if (c is 'a' or 'e' or 'i' or 'o' or 'u')
            {
                continue;
            }

            // w/y are dropped everywhere except as the first character of the (already digit-mapped, article-stripped) word
            // (ruling R7: lets Arabizi "fi" meet ICU "fy" and "3la" meet "ly").
            if (c is 'w' or 'y' && i > 0)
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string CollapseDoubles(string w)
    {
        var sb = new StringBuilder(w.Length);
        foreach (var c in w)
        {
            if (sb.Length == 0 || sb[sb.Length - 1] != c)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
