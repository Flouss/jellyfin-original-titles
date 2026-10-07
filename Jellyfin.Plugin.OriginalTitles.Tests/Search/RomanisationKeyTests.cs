using Jellyfin.Plugin.OriginalTitles.Search;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

/// <summary>
/// Every expected key below is derived BY HAND from the rules (spec as amended) and, for the transliterated titles, from the
/// transliterator output recorded in docs/spike.md (real ICU via Jellyfin.Extensions, observed 2026-10-07).
/// Pipeline order (one word): lowercase; drop U+02BF/U+02BE and apostrophes; strip a leading al/el (only when 3+ chars remain);
/// digits (3,2 dropped; 7 to h; 5 to kh; 9 to q); digraphs (kh to x, sh/ch to c, th to t, dh/dz to d, gh to g);
/// drop a e i o u and w/y everywhere EXCEPT at the start of the word (ruling R7); collapse doubled letters LAST.
/// </summary>
public class RomanisationKeyTests
{
    [Theory]

    // khottab -> khottab -> digraph kh: xottab -> drop vowels: xttb -> collapse tt: xtb
    [InlineData("khottab", "xtb")]
    [InlineData("KHOTTAB", "xtb")]

    // beb -> drop e: bb -> collapse: b
    [InlineData("beb", "b")]

    // alkhtab (ICU output of the title) -> strip al (rest "khtab", 5 chars): khtab -> xtab -> drop a: xtb
    [InlineData("alkhtab", "xtb")]

    // albab -> strip al (rest "bab", 3 chars): bab -> drop a: bb -> b
    [InlineData("albab", "b")]
    [InlineData("elbab", "b")]

    // ali: the rest "i" is only 1 char, so NO strip (otherwise the word would vanish): ali -> drop a,i -> l
    [InlineData("ali", "l")]

    // lion / lbn: a bare leading l is NOT an article without a hyphen: lion -> drop i,o -> ln ; lbn -> lbn
    [InlineData("lion", "ln")]
    [InlineData("lbn", "lbn")]

    // U+02BF (ayn as emitted by ICU) is dropped like Arabizi 3: ly -> y is not initial, dropped -> l
    [InlineData("\u02BFly", "l")]

    // Arabizi 3la -> 3 dropped: la -> drop a -> l (the same key as the ICU word above)
    [InlineData("3la", "l")]

    // U+02BE (hamza as emitted by ICU) is dropped like Arabizi 2: amr -> drop a -> mr
    [InlineData("\u02BEamr", "mr")]

    // collapse after vowel dropping: kaak -> kk -> k
    [InlineData("kaak", "k")]
    [InlineData("mmm", "m")]
    [InlineData("", "")]
    [InlineData("aeiou", "")]
    public void KeyOf_AppliesTheRulesInOrder(string word, string expected)
    {
        Assert.Equal(expected, RomanisationKey.KeyOf(word));
    }

    [Theory]

    // 7bibi -> 7 to h: hbibi -> drop i,i: hbb -> collapse: hb
    [InlineData("7bibi", "hb")]

    // 5ottab -> 5 to kh: khottab -> xtb (digits run BEFORE digraphs, otherwise the 5 would stay a character)
    [InlineData("5ottab", "xtb")]

    // 9alb -> 9 to q: qalb -> drop a: qlb
    [InlineData("9alb", "qlb")]

    // ba3d -> 3 dropped: bad -> drop a: bd (presence companion: "bd" itself keys to bd, so nothing else is left over)
    [InlineData("ba3d", "bd")]
    [InlineData("ba2d", "bd")]
    [InlineData("bd", "bd")]

    // a lone 3 / 2 carries nothing: empty key
    [InlineData("3", "")]
    [InlineData("2", "")]

    // lone 7 -> h ; lone 5 -> kh -> x ; lone 9 -> q
    [InlineData("7", "h")]
    [InlineData("5", "x")]
    [InlineData("9", "q")]
    public void KeyOf_ArabiziDigits(string word, string expected)
    {
        Assert.Equal(expected, RomanisationKey.KeyOf(word));
    }

    [Theory]

    // shams -> sh to c: cams -> drop a: cms ; chams -> same
    [InlineData("shams", "cms")]
    [InlineData("chams", "cms")]

    // thawra -> th to t: tawra -> a,a dropped; w is not initial, dropped: tr
    [InlineData("thawra", "tr")]

    // dhahab -> dh to d: dahab -> drop a,a: dhb (the second h is a plain h) ; dzair -> dair -> dr
    [InlineData("dhahab", "dhb")]
    [InlineData("dzair", "dr")]

    // ghanm -> ganm -> gnm ; khalid -> xalid -> xld
    [InlineData("ghanm", "gnm")]
    [InlineData("khalid", "xld")]
    public void KeyOf_DigraphMerge(string word, string expected)
    {
        Assert.Equal(expected, RomanisationKey.KeyOf(word));
    }

    [Theory]

    // bayt: b,a,y,t -> a dropped, y not initial dropped: bt
    [InlineData("bayt", "bt")]

    // mwsh -> sh to c: mwc ; w not initial, dropped: mc
    [InlineData("mwsh", "mc")]

    // thnyty -> th to t: tnyty ; both y are not initial, dropped: tnt
    [InlineData("thnyty", "tnt")]

    // fy: the final y is dropped too: f (so ICU fy meets Arabizi fi -> f)
    [InlineData("fy", "f")]
    [InlineData("fi", "f")]

    // yalla: y is the first char, kept; a,a dropped; ll collapsed: yl
    [InlineData("yalla", "yl")]

    // initial y/w are kept: yasmine -> y,s,m,n (a,i,e dropped) = ysmn ; walid -> w,l,d (a,i dropped) = wld
    [InlineData("yasmine", "ysmn")]
    [InlineData("walid", "wld")]

    // absence companions, the same letters NOT first are dropped: ayasmine -> a,y,a,s,m,i,n,e -> smn ; awalid -> ld
    [InlineData("ayasmine", "smn")]
    [InlineData("awalid", "ld")]
    public void KeyOf_WAndYAreDroppedEverywhereButTheStart(string word, string expected)
    {
        Assert.Equal(expected, RomanisationKey.KeyOf(word));
    }

    [Fact]
    public void KeysOf_QueryDropsTheStopwordAl()
    {
        // khottab al beb -> khottab (xtb), al (stopword, ignored), beb (b)
        Assert.Equal(new[] { "xtb", "b" }, RomanisationKey.KeysOf("khottab al beb"));

        // presence companion: "ali" is NOT a stopword, so it counts as a word (key l)
        Assert.Equal(new[] { "xtb", "l", "b" }, RomanisationKey.KeysOf("khottab ali beb"));
    }

    [Theory]
    [InlineData("al-khattab", "xtb")] // al- stripped: khattab -> xattab -> xttb -> xtb
    [InlineData("el-khattab", "xtb")]
    [InlineData("l-beb", "b")] // l- with hyphen IS an article: beb -> b
    public void KeysOf_StripsHyphenatedArticles(string text, string expected)
    {
        Assert.Equal(new[] { expected }, RomanisationKey.KeysOf(text));
    }

    [Fact]
    public void KeysOf_SplitsOnPunctuationAndWhitespace()
    {
        // "khottab, beb!" -> khottab (xtb), beb (b)
        Assert.Equal(new[] { "xtb", "b" }, RomanisationKey.KeysOf("  khottab,   beb! "));

        // bab-el-oued -> bab (b), el (stopword), oued (o,u,e dropped: d)
        Assert.Equal(new[] { "b", "d" }, RomanisationKey.KeysOf("bab-el-oued"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("al")]
    [InlineData("el")]
    [InlineData("al el")]
    [InlineData("AL  El")]
    [InlineData("al-")]
    [InlineData("3")]
    [InlineData("aeiou")]
    public void KeysOf_NothingToKey_IsEmptyAndDoesNotThrow(string? text)
    {
        Assert.Empty(RomanisationKey.KeysOf(text));
    }

    [Fact]
    public void TransliteratedKeysOf_RealIcuOutputOfTheAcceptanceTitle()
    {
        // docs/spike.md: ICU renders الخطاب على الباب as "alkhtab \u02BFly albab".
        // alkhtab -> xtb ; \u02BFly -> ly (ayn dropped) -> y not initial, dropped: l ; albab -> b
        Assert.Equal(new[] { "xtb", "l", "b" }, RomanisationKey.TransliteratedKeysOf("الخطاب على الباب"));
    }

    [Fact]
    public void TransliteratedKeysOf_RealIcuOutputOfTheSecondTitle()
    {
        // docs/spike.md: ICU renders موش في ثنيتي as "mwsh fy thnyty" (matches the live SortName).
        // mwsh -> mc ; fy -> f ; thnyty -> tnt
        Assert.Equal(new[] { "mc", "f", "tnt" }, RomanisationKey.TransliteratedKeysOf("موش في ثنيتي"));
    }

    [Fact]
    public void TransliteratedKeysOf_LatinInputKeepsItsShape_AndNullIsEmpty()
    {
        // Latin text: ICU only lower-cases and strips punctuation: khottab al beb -> xtb, b
        Assert.Equal(new[] { "xtb", "b" }, RomanisationKey.TransliteratedKeysOf("Khottab al Beb!"));
        Assert.Empty(RomanisationKey.TransliteratedKeysOf(null));
        Assert.Empty(RomanisationKey.TransliteratedKeysOf(string.Empty));
    }
}
