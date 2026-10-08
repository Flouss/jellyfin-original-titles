using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// Matches a query against a title using <see cref="RomanisationKey"/> keys. A query matches when EVERY query word (stopwords
/// <c>al</c>/<c>el</c> and empty-key words are not query words) has some title word whose key is equal, or, when both keys have at
/// least <see cref="MinFuzzyLength"/> characters, within one edit, or when the query key has at least <see cref="MinPrefixLength"/>
/// characters and the title key starts with it. Pure logic, no I/O.
/// </summary>
public static class TitleMatcher
{
    /// <summary>Both keys need at least this many characters before a distance-1 match is allowed; shorter keys need equality.</summary>
    private const int MinFuzzyLength = 3;

    /// <summary>A query key needs at least this many characters to match as a prefix of a longer title key.</summary>
    private const int MinPrefixLength = 2;

    /// <summary>
    /// Whether one query key matches one title key. Empty keys never match.
    /// </summary>
    /// <param name="queryKey">A query key.</param>
    /// <param name="titleKey">A title key.</param>
    /// <returns>True on equality, when a query key of 2 or more characters is a prefix of the title key, or at distance 1 when both keys have 3 or more characters.</returns>
    public static bool WordsMatch(string queryKey, string titleKey)
    {
        if (string.IsNullOrEmpty(queryKey) || string.IsNullOrEmpty(titleKey))
        {
            return false;
        }

        if (string.Equals(queryKey, titleKey, StringComparison.Ordinal))
        {
            return true;
        }

        // A query key of 2+ characters also matches a longer title key it starts with: the title word is being typed
        // ("shouf" -> cf finds "choufli" -> cfl). A 1-character key never does (R16: it would match every title word starting with it).
        if (queryKey.Length >= MinPrefixLength && titleKey.StartsWith(queryKey, StringComparison.Ordinal))
        {
            return true;
        }

        return queryKey.Length >= MinFuzzyLength && titleKey.Length >= MinFuzzyLength && WithinOneEdit(queryKey, titleKey);
    }

    /// <summary>
    /// Fraction of the query keys covered by some title key; 0 when the query has no keys.
    /// </summary>
    /// <param name="queryKeys">Keys of the query (see <see cref="RomanisationKey.KeysOf"/>).</param>
    /// <param name="titleKeys">Keys of the title.</param>
    /// <returns>A value between 0 and 1.</returns>
    public static double Coverage(IReadOnlyList<string> queryKeys, IReadOnlyList<string> titleKeys)
    {
        ArgumentNullException.ThrowIfNull(queryKeys);
        ArgumentNullException.ThrowIfNull(titleKeys);
        return queryKeys.Count == 0 ? 0.0 : (double)CountCovered(queryKeys, titleKeys) / queryKeys.Count;
    }

    /// <summary>
    /// Coverage of already transliterated (Latin) text, as <see cref="Coverage(IReadOnlyList{string}, IReadOnlyList{string})"/>.
    /// </summary>
    /// <param name="query">The query (Latin).</param>
    /// <param name="latinTitle">The title, already transliterated to Latin.</param>
    /// <returns>A value between 0 and 1; 0 for null/empty/stopword-only input.</returns>
    public static double Coverage(string? query, string? latinTitle)
    {
        return Coverage(RomanisationKey.KeysOf(query), RomanisationKey.KeysOf(latinTitle));
    }

    /// <summary>
    /// A match needs at least one query key and every query key covered (threshold = all words).
    /// </summary>
    /// <param name="queryKeys">Keys of the query.</param>
    /// <param name="titleKeys">Keys of the title.</param>
    /// <returns>True when the title matches.</returns>
    public static bool IsMatch(IReadOnlyList<string> queryKeys, IReadOnlyList<string> titleKeys)
    {
        ArgumentNullException.ThrowIfNull(queryKeys);
        ArgumentNullException.ThrowIfNull(titleKeys);
        return queryKeys.Count > 0 && CountCovered(queryKeys, titleKeys) == queryKeys.Count;
    }

    /// <summary>
    /// Match of already transliterated (Latin) text, as <see cref="IsMatch(IReadOnlyList{string}, IReadOnlyList{string})"/>.
    /// </summary>
    /// <param name="query">The query (Latin).</param>
    /// <param name="latinTitle">The title, already transliterated to Latin.</param>
    /// <returns>True when the title matches; false for null/empty/stopword-only input.</returns>
    public static bool IsMatch(string? query, string? latinTitle)
    {
        return IsMatch(RomanisationKey.KeysOf(query), RomanisationKey.KeysOf(latinTitle));
    }

    private static int CountCovered(IReadOnlyList<string> queryKeys, IReadOnlyList<string> titleKeys)
    {
        var covered = 0;
        foreach (var queryKey in queryKeys)
        {
            foreach (var titleKey in titleKeys)
            {
                if (WordsMatch(queryKey, titleKey))
                {
                    covered++;
                    break;
                }
            }
        }

        return covered;
    }

    private static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var i = 0;
        var j = 0;
        var edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1)
            {
                return false;
            }

            if (a.Length > b.Length)
            {
                i++;
            }
            else if (a.Length < b.Length)
            {
                j++;
            }
            else
            {
                i++;
                j++;
            }
        }

        // a trailing unmatched character is one more edit
        edits += (a.Length - i) + (b.Length - j);
        return edits <= 1;
    }
}
