using Jellyfin.Plugin.OriginalTitles.Search;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

/// <summary>
/// Hand-derived expectations; keys follow the pipeline documented in <see cref="RomanisationKeyTests"/>.
/// Match rule: every query key (stopwords al/el ignored) needs some title key at Levenshtein distance 0 or 1;
/// distance 1 only when BOTH keys have 3+ characters, shorter keys need exact equality; threshold = all words (coverage 1.0).
/// </summary>
public class TitleMatcherTests
{
    // ICU output for الخطاب على الباب (docs/spike.md); title keys by hand: xtb, ly, b.
    private const string AcceptanceTitle = "alkhtab \u02BFly albab";

    [Fact]
    public void Acceptance_KhottabAlBeb_MatchesTheArabicTitle_WithFullCoverage()
    {
        // query keys [xtb, b]; title keys [xtb, ly, b]; xtb = xtb, b = b -> 2/2
        Assert.Equal(1.0, TitleMatcher.Coverage("khottab al beb", AcceptanceTitle));
        Assert.True(TitleMatcher.IsMatch("khottab al beb", AcceptanceTitle));
    }

    [Fact]
    public void Acceptance_ThroughTheRealTransliterator()
    {
        // The Arabic title goes through real ICU (Jellyfin.Extensions); title keys [xtb, ly, b] derived by hand in RomanisationKeyTests.
        var titleKeys = RomanisationKey.TransliteratedKeysOf("الخطاب على الباب");
        var queryKeys = RomanisationKey.TransliteratedKeysOf("khottab al beb");
        Assert.Equal(1.0, TitleMatcher.Coverage(queryKeys, titleKeys));
        Assert.True(TitleMatcher.IsMatch(queryKeys, titleKeys));
    }

    [Fact]
    public void PartialCoverage_IsNotAMatch_WithPresenceCompanion()
    {
        // khottab zzzz: keys [xtb, z] (zzzz collapses to z); z matches none of [xtb, ly, b] -> 1/2
        Assert.Equal(0.5, TitleMatcher.Coverage("khottab zzzz", AcceptanceTitle));
        Assert.False(TitleMatcher.IsMatch("khottab zzzz", AcceptanceTitle));

        // companion: the covered word on its own matches fully
        Assert.Equal(1.0, TitleMatcher.Coverage("khottab", AcceptanceTitle));
        Assert.True(TitleMatcher.IsMatch("khottab", AcceptanceTitle));
    }

    [Fact]
    public void LongKeys_WithinOneEdit_Match_AndTwoEditsDoNot()
    {
        // kottab -> k,o,t,t,a,b -> kttb -> ktb ; ktb vs xtb: distance 1, both 3 chars -> match
        Assert.True(TitleMatcher.IsMatch("kottab", AcceptanceTitle));

        // kzzab -> kzzb -> kzb ; kzb vs xtb: distance 2 (k/x and z/t) and no other title key is close -> no match
        Assert.False(TitleMatcher.IsMatch("kzzab", AcceptanceTitle));
    }

    [Theory]

    // 1-char keys: exact only
    [InlineData("b", "b", true)]
    [InlineData("b", "c", false)]

    // 2-char keys: exact only
    [InlineData("ab", "ab", true)]
    [InlineData("ab", "ac", false)]

    // boundary: 2-char vs 3-char at distance 1 must NOT match (either direction)
    [InlineData("xt", "xtb", false)]
    [InlineData("xtb", "xt", false)]

    // companion: 3-char vs 3-char at distance 1 matches (substitution), and 3 vs 4 (insertion / deletion)
    [InlineData("xtb", "xtp", true)]
    [InlineData("xtb", "xtbb", true)]
    [InlineData("xtbb", "xtb", true)]

    // distance 2 and a transposition (distance 2) do not match
    [InlineData("xtb", "xpp", false)]
    [InlineData("xtb", "xbt", false)]

    // empty keys never match anything, not even each other
    [InlineData("", "", false)]
    [InlineData("xtb", "", false)]
    [InlineData("", "xtb", false)]
    public void WordsMatch_ShortKeysNeedExactEquality(string queryKey, string titleKey, bool expected)
    {
        Assert.Equal(expected, TitleMatcher.WordsMatch(queryKey, titleKey));
    }

    [Fact]
    public void OneCharKey_DoesNotMatchEverything()
    {
        // query "bab" has key b ; title "cab" has key cb (c, a dropped, b): 1 vs 2 chars -> exact only -> no match
        Assert.False(TitleMatcher.IsMatch("bab", "cab"));

        // presence companion: title "beb" (key b) matches
        Assert.True(TitleMatcher.IsMatch("bab", "beb"));

        // a single-letter query "c" does not match title keys [xtb, ly, b]
        Assert.False(TitleMatcher.IsMatch("c", AcceptanceTitle));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("al")]
    [InlineData("al el")]
    [InlineData("el")]
    [InlineData("3")]
    public void EmptyOrStopwordOnlyQuery_NeverMatches_AndDoesNotThrow(string? query)
    {
        Assert.Equal(0.0, TitleMatcher.Coverage(query, AcceptanceTitle));
        Assert.False(TitleMatcher.IsMatch(query, AcceptanceTitle));

        // even against a title made of the same stopwords/empties (an empty query must not match "everything")
        Assert.False(TitleMatcher.IsMatch(query, query));
        Assert.False(TitleMatcher.IsMatch(query, "al el"));
    }

    [Fact]
    public void EmptyTitle_NeverMatches()
    {
        Assert.Equal(0.0, TitleMatcher.Coverage("khottab", string.Empty));
        Assert.Equal(0.0, TitleMatcher.Coverage("khottab", null));
        Assert.False(TitleMatcher.IsMatch("khottab", string.Empty));
        Assert.False(TitleMatcher.IsMatch("khottab", "al el"));
    }

    [Fact]
    public void StopwordInTheQuery_IsIgnored_ButANonStopwordCounts()
    {
        // with the stopword rule "al" is not a query word: [xtb, b] vs [xtb, ly, b] -> 1.0.
        // Without it "al" would key to l, which matches nothing in [xtb, ly, b] (1-char key, exact only) -> 2/3.
        Assert.Equal(1.0, TitleMatcher.Coverage("khottab al beb", AcceptanceTitle));

        // "ali" is not a stopword (key l) and really counts and is uncovered: 2/3
        Assert.Equal(2.0 / 3.0, TitleMatcher.Coverage("khottab ali beb", AcceptanceTitle), 10);
    }

    [Fact]
    public void ArabiziDigits_ReachTheMatcher()
    {
        // 7bibi: hb ; title "habibi" -> hbb -> hb : match
        Assert.True(TitleMatcher.IsMatch("7bibi", "habibi"));

        // 5ottab: xtb ; matches the acceptance title through the digit map
        Assert.True(TitleMatcher.IsMatch("5ottab al beb", AcceptanceTitle));

        // 9alb: qlb ; title "qalb" -> qlb : match
        Assert.True(TitleMatcher.IsMatch("9alb", "qalb"));

        // 3 dropped: "ba3d" (bd) matches title "bad" (bd) and does not match "bat" (bt: both 2 chars, exact only)
        Assert.True(TitleMatcher.IsMatch("ba3d", "bad"));
        Assert.False(TitleMatcher.IsMatch("ba3d", "bat"));
    }

    [Fact]
    public void SecondTitle_KnownLimitation_FinalYIsNotDropped()
    {
        // Title موش في ثنيتي -> ICU "mwsh fy thnyty" -> keys [mc, fy, tnty].
        const string Title = "mwsh fy thnyty";

        // a spelling that avoids the short word matches fully: mouch -> mc ; thniti -> tnt (vs tnty: 3 vs 4 chars, distance 1)
        Assert.True(TitleMatcher.IsMatch("mouch thniti", Title));

        // LIMITATION (reported, not fixed): the natural Arabizi "fi" keys to f, but the title's fy keeps its final y
        // (y is only dropped BETWEEN consonants). f vs fy: 1-char key needs exact equality -> uncovered; coverage 2/3, no match.
        Assert.Equal(2.0 / 3.0, TitleMatcher.Coverage("mouch fi thniti", Title), 10);
        Assert.False(TitleMatcher.IsMatch("mouch fi thniti", Title));
    }
}
