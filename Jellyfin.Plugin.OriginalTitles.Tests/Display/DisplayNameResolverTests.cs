using System;
using System.Collections.Generic;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Display;

[Collection(BaseItemLibraryManagerCollection.Name)]
public sealed class DisplayNameResolverTests : IDisposable
{
    // Fixture literals shared by every test: the translated Name, the original title and the language that gates it.
    private const string Translated = "Mouch Fi Thniti";
    private const string Original = "موش في ثنيتي";

    private static readonly Guid ArabicUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FrenchUser = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid EmptyListUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NoEntryUser = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public DisplayNameResolverTests()
    {
        BaseItem.LibraryManager = null!;
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = null!;
    }

    private static DisplayNameResolver Resolver()
    {
        var config = new PluginConfiguration();
        config.Users.Add(new UserLanguageEntry { UserId = ArabicUser, Languages = new List<string> { "ar" } });
        config.Users.Add(new UserLanguageEntry { UserId = FrenchUser, Languages = new List<string> { "fr" } });
        config.Users.Add(new UserLanguageEntry { UserId = EmptyListUser, Languages = new List<string>() });
        return new DisplayNameResolver(new UserLanguageSettings(() => config));
    }

    private static Movie ArabicMovie(string? originalTitle = Original, string? language = "ar", string name = Translated)
        => new() { Id = Guid.NewGuid(), Name = name, OriginalTitle = originalTitle, OriginalLanguage = language };

    private static Series ArabicSeries(string? originalTitle = Original, string? language = "ar", string name = Translated)
        => new() { Id = Guid.NewGuid(), Name = name, OriginalTitle = originalTitle, OriginalLanguage = language };

    private static void AssertUntouched((string? Name, string? SeriesName) result)
    {
        Assert.Null(result.Name);
        Assert.Null(result.SeriesName);
    }

    [Fact]
    public void Movie_EnabledLanguageUser_GetsOriginalTitle_OtherLanguageUserDoesNot()
    {
        // Movie Name "Mouch Fi Thniti", OriginalTitle "موش في ثنيتي", language "ar".
        // ArabicUser enabled ["ar"] -> all three conditions hold -> Name = OriginalTitle, SeriesName stays null.
        // FrenchUser enabled ["fr"] -> "ar" not listed -> untouched (absence companion on the same item).
        var movie = ArabicMovie();
        var resolver = Resolver();

        var enabled = resolver.Resolve(movie, ArabicUser);
        Assert.Equal(Original, enabled.Name);
        Assert.Null(enabled.SeriesName);

        AssertUntouched(resolver.Resolve(movie, FrenchUser));

        // The decision never writes to the item.
        Assert.Equal(Translated, movie.Name);
        Assert.Equal(Original, movie.OriginalTitle);
    }

    [Fact]
    public void Movie_UserWithoutEntryOrWithEmptyList_IsUntouched_WhileEnabledUserIsRewritten()
    {
        // NoEntryUser has no settings entry, EmptyListUser has an empty list -> nothing enabled -> untouched.
        // ArabicUser ["ar"] on the same movie is rewritten (presence companion).
        var movie = ArabicMovie();
        var resolver = Resolver();

        AssertUntouched(resolver.Resolve(movie, NoEntryUser));
        AssertUntouched(resolver.Resolve(movie, EmptyListUser));
        Assert.Equal(Original, resolver.Resolve(movie, ArabicUser).Name);
    }

    [Fact]
    public void Movie_OriginalTitleEqualToName_IsUntouched_WhileDifferingTitleIsRewritten()
    {
        // Name "Mouch Fi Thniti" and OriginalTitle "Mouch Fi Thniti" are equal -> nothing to show -> untouched.
        // Same item with OriginalTitle "Mouch Fi Thniti 2" differs -> rewritten to "Mouch Fi Thniti 2".
        var resolver = Resolver();

        AssertUntouched(resolver.Resolve(ArabicMovie(originalTitle: Translated), ArabicUser));
        Assert.Equal("Mouch Fi Thniti 2", resolver.Resolve(ArabicMovie(originalTitle: "Mouch Fi Thniti 2"), ArabicUser).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Movie_NullEmptyOrBlankOriginalTitle_IsUntouched_WhileFilledTitleIsRewritten(string? originalTitle)
    {
        // A blank OriginalTitle differs from Name "Mouch Fi Thniti" and the language "ar" is enabled, so only the
        // non-empty check can keep it untouched. Companion: the filled title on an otherwise identical movie is rewritten.
        var resolver = Resolver();

        AssertUntouched(resolver.Resolve(ArabicMovie(originalTitle: originalTitle), ArabicUser));
        Assert.Equal(Original, resolver.Resolve(ArabicMovie(), ArabicUser).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fr")]
    public void Movie_NoLanguageOrNotEnabledLanguage_IsUntouched_WhileEnabledLanguageIsRewritten(string? language)
    {
        // ArabicUser lists only "ar": null / empty / blank ("no language", R5) and "fr" (other language) are all not enabled.
        // Title and Name differ in every case, so only the language gate keeps them untouched. Companion: "ar" rewrites.
        var resolver = Resolver();

        AssertUntouched(resolver.Resolve(ArabicMovie(language: language), ArabicUser));
        Assert.Equal(Original, resolver.Resolve(ArabicMovie(language: "ar"), ArabicUser).Name);
    }

    [Fact]
    public void Movie_LanguageIsMatchedAfterTrimAndLowercase()
    {
        // OriginalLanguage keeps raw values without a LocalizationManager (spike): " AR " must still match the stored "ar".
        var resolver = Resolver();

        Assert.Equal(Original, resolver.Resolve(ArabicMovie(language: " AR "), ArabicUser).Name);
        AssertUntouched(resolver.Resolve(ArabicMovie(language: " AR "), FrenchUser));
    }

    [Fact]
    public void Series_FollowsTheSameThreeConditions()
    {
        // Series fixture identical to the movie: enabled "ar" -> Original; "fr" user, equal title, empty title, empty language -> untouched.
        var resolver = Resolver();

        var enabled = resolver.Resolve(ArabicSeries(), ArabicUser);
        Assert.Equal(Original, enabled.Name);
        Assert.Null(enabled.SeriesName);

        AssertUntouched(resolver.Resolve(ArabicSeries(), FrenchUser));
        AssertUntouched(resolver.Resolve(ArabicSeries(originalTitle: Translated), ArabicUser));
        AssertUntouched(resolver.Resolve(ArabicSeries(originalTitle: string.Empty), ArabicUser));
        AssertUntouched(resolver.Resolve(ArabicSeries(language: string.Empty), ArabicUser));
    }

    [Fact]
    public void Episode_EmptyOwnLanguage_UsesSeriesLanguage_AndNeverTouchesItsOwnName()
    {
        // Series "ar" / OriginalTitle "موش في ثنيتي" / Name "Mouch Fi Thniti". Episode own language "" (measured: the
        // inherited language stays "" -- no fallback). The decision uses the SERIES' language, so ArabicUser still gets
        // SeriesName = series OriginalTitle and Name stays null (the episode's own Name is never replaced).
        // FrenchUser ["fr"] -> series language "ar" not enabled -> untouched (companion).
        var series = ArabicSeries();
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode One", SeriesId = series.Id, OriginalLanguage = string.Empty };
        BaseItem.LibraryManager = FakeLibraryManager.Create(series);
        var resolver = Resolver();

        Assert.Equal(string.Empty, episode.GetInheritedOriginalLanguage());

        var enabled = resolver.Resolve(episode, ArabicUser);
        Assert.Null(enabled.Name);
        Assert.Equal(Original, enabled.SeriesName);
        Assert.Equal("Episode One", episode.Name);

        AssertUntouched(resolver.Resolve(episode, FrenchUser));
    }

    [Fact]
    public void Episode_NullOwnLanguage_InheritsSeriesLanguage()
    {
        // Own language null -> GetInheritedOriginalLanguage() falls back to the series' "ar" (measured). ArabicUser -> SeriesName
        // = Original; FrenchUser -> untouched.
        var series = ArabicSeries();
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode One", SeriesId = series.Id, OriginalLanguage = null };
        BaseItem.LibraryManager = FakeLibraryManager.Create(series);
        var resolver = Resolver();

        Assert.Equal("ar", episode.GetInheritedOriginalLanguage());
        Assert.Equal(Original, resolver.Resolve(episode, ArabicUser).SeriesName);
        AssertUntouched(resolver.Resolve(episode, FrenchUser));
    }

    [Fact]
    public void Episode_OwnLanguageDiffersFromSeries_SeriesLanguageDecides()
    {
        // Episode own language "fr", series "ar". The series name is shown, so the series' language gates it:
        // ArabicUser (["ar"]) -> rewritten even though the episode says "fr"; FrenchUser (["fr"]) -> untouched
        // even though the episode says "fr".
        var series = ArabicSeries();
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode One", SeriesId = series.Id, OriginalLanguage = "fr" };
        BaseItem.LibraryManager = FakeLibraryManager.Create(series);
        var resolver = Resolver();

        Assert.Equal(Original, resolver.Resolve(episode, ArabicUser).SeriesName);
        AssertUntouched(resolver.Resolve(episode, FrenchUser));
    }

    [Fact]
    public void Episode_SeriesFailingAnyCondition_IsUntouched_WhileQualifyingSeriesIsRewritten()
    {
        // Each series below fails exactly one condition (equal title / empty title / blank language), the episode's own
        // fields are irrelevant. ArabicUser is enabled for "ar", so the qualifying series (presence companion) rewrites.
        var equalTitle = ArabicSeries(originalTitle: Translated);
        var emptyTitle = ArabicSeries(originalTitle: string.Empty);
        var blankLanguage = ArabicSeries(language: " ");
        var qualifying = ArabicSeries();
        BaseItem.LibraryManager = FakeLibraryManager.Create(equalTitle, emptyTitle, blankLanguage, qualifying);
        var resolver = Resolver();

        foreach (var series in new[] { equalTitle, emptyTitle, blankLanguage })
        {
            AssertUntouched(resolver.Resolve(new Episode { Id = Guid.NewGuid(), Name = "E", SeriesId = series.Id }, ArabicUser));
        }

        Assert.Equal(Original, resolver.Resolve(new Episode { Id = Guid.NewGuid(), Name = "E", SeriesId = qualifying.Id }, ArabicUser).SeriesName);
    }

    [Fact]
    public void Episode_WithoutResolvableSeries_IsUntouched()
    {
        // SeriesId empty and no parent: Series is null -> nothing to show. The episode's own language "ar" must not matter.
        // Companion: an episode pointing at a resolvable "ar" series is rewritten.
        var series = ArabicSeries();
        BaseItem.LibraryManager = FakeLibraryManager.Create(series);
        var resolver = Resolver();

        AssertUntouched(resolver.Resolve(new Episode { Id = Guid.NewGuid(), Name = "E", OriginalLanguage = "ar", OriginalTitle = Original }, ArabicUser));
        Assert.Equal(Original, resolver.Resolve(new Episode { Id = Guid.NewGuid(), Name = "E", SeriesId = series.Id }, ArabicUser).SeriesName);
    }

    [Fact]
    public void Season_UsesSeriesLanguageAndTitle_AndNeverTouchesItsOwnName()
    {
        // Season own language null, series "ar": inherits "ar"; own empty "": not inherited but the series still decides.
        var series = ArabicSeries();
        var inheriting = new Season { Id = Guid.NewGuid(), Name = "Season 1", SeriesId = series.Id, OriginalLanguage = null };
        var emptyOwn = new Season { Id = Guid.NewGuid(), Name = "Season 2", SeriesId = series.Id, OriginalLanguage = string.Empty };
        BaseItem.LibraryManager = FakeLibraryManager.Create(series);
        var resolver = Resolver();

        Assert.Equal("ar", inheriting.GetInheritedOriginalLanguage());
        Assert.Equal(string.Empty, emptyOwn.GetInheritedOriginalLanguage());

        foreach (var season in new[] { inheriting, emptyOwn })
        {
            var enabled = resolver.Resolve(season, ArabicUser);
            Assert.Null(enabled.Name);
            Assert.Equal(Original, enabled.SeriesName);
            AssertUntouched(resolver.Resolve(season, FrenchUser));
        }

        Assert.Equal("Season 1", inheriting.Name);
        Assert.Equal("Season 2", emptyOwn.Name);
    }

    [Fact]
    public void OtherItemKinds_AreUntouched_WhileMovieWithSameFieldsIsRewritten()
    {
        // BoxSet / Folder carry the same Name, OriginalTitle ("موش في ثنيتي") and language "ar" as the movie, and the user
        // enables "ar", so every condition holds -- only the item kind keeps them untouched. Movie is the presence companion.
        var resolver = Resolver();
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = Translated, OriginalTitle = Original, OriginalLanguage = "ar" };
        var folder = new Folder { Id = Guid.NewGuid(), Name = Translated, OriginalTitle = Original, OriginalLanguage = "ar" };

        AssertUntouched(resolver.Resolve(boxSet, ArabicUser));
        AssertUntouched(resolver.Resolve(folder, ArabicUser));
        Assert.Equal(Original, resolver.Resolve(ArabicMovie(), ArabicUser).Name);
    }
}
