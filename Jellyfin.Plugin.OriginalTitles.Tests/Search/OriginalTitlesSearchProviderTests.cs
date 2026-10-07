using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Library;
using Jellyfin.Plugin.OriginalTitles.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

/// <summary>
/// Provider behaviour with fakes. Hand-derived keys (pipeline in RomanisationKeyTests, ICU output in docs/spike.md):
/// title <c>الخطاب على الباب</c> -> ICU <c>alkhtab ʿly albab</c> -> keys [xtb, l, b];
/// query <c>khottab al beb</c> -> keys [xtb, b] (al is a stopword) -> every query key equals a title key -> match.
/// Romanised hits carry the provider's fixed score 90 (design constant: below SQL's exact 100, above its prefix 80).
/// </summary>
public class OriginalTitlesSearchProviderTests
{
    private const string KhattabTitle = "الخطاب على الباب";
    private const string RomanisedQuery = "khottab al beb";
    private const float Romanised = 90f;

    private static readonly Guid UserAr = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid UserHidden = new("11111111-0000-0000-0000-000000000002");
    private static readonly Guid UserNoLanguage = new("11111111-0000-0000-0000-000000000003");

    private static readonly Guid IdA1 = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid IdA2 = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid IdA3 = new("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid IdB = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid IdC = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid IdS = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Library = new("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid FolderP1 = new("eeeeeeee-0000-0000-0000-000000000002");
    private static readonly Guid FolderP2 = new("eeeeeeee-0000-0000-0000-000000000003");

    [Fact]
    public async Task RomanisedQuery_HitsTheMovieByKey_ForAUserWithArabicEnabled()
    {
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr, UserNoLanguage);

        Assert.Equal(new[] { (IdA1, Romanised) }, await f.Search(Query(UserAr)));

        // absence companion: same item, same access, but this user enabled no language -> the plugin does nothing for them
        Assert.Empty(await f.Search(Query(UserNoLanguage)));
    }

    [Fact]
    public async Task ArabicScriptQuery_IsTransliteratedSymmetrically_AndHits()
    {
        // query الخطاب -> ICU alkhtab -> key xtb (strip al, kh->x, drop a) -> equals title key xtb
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);

        Assert.Equal(new[] { (IdA1, Romanised) }, await f.Search(Query(UserAr, "الخطاب")));
    }

    [Fact]
    public async Task PlainTranslatedTitleMatches_AreReturned_AlongsideTheRomanisedHit()
    {
        // R1: once this external provider returns anything the manager skips SQL, so the provider must surface SQL's plain
        // (translated Name / CleanName / OriginalTitle) matches itself. Fake SQL: A1 matches its translated Name exactly (100),
        // B contains the words (50). Merge = best score per item: A1 max(90, 100) = 100, B 50; ordered by score.
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.Sql.Results.Add(new SearchResult(IdA1, 100f));
        f.Sql.Results.Add(new SearchResult(IdB, 50f));

        var query = Query(UserAr);
        Assert.Equal(new[] { (IdA1, 100f), (IdB, 50f) }, await f.Search(query));
        Assert.Same(query, Assert.Single(f.Sql.Queries)); // delegated with the caller's own filters (types, parent, user, limit)
    }

    [Theory]
    [InlineData(BaseItemKind.Movie)]
    [InlineData(BaseItemKind.Series)]
    public async Task Security_UserWithoutAccess_GetsNoHit_CompanionWithAccessDoes(BaseItemKind kind)
    {
        // Review Focus 6, for BOTH indexed kinds (review round 1: a Series-only access bypass survived the Movie-only version).
        // Both users have Arabic enabled; only UserAr may see A1 (UserHidden: hidden library / parental block).
        var f = new Fixture();
        f.AddVisible(kind == BaseItemKind.Movie ? Movie(IdA1) : SeriesItem(IdA1), UserAr);

        Assert.Empty(await f.Search(Query(UserHidden)));
        Assert.Contains((UserHidden, IdA1), f.Access.Asked); // the access seam was consulted for that pair
        Assert.Empty(f.Sql.Queries); // nothing romanised survived, so the provider stays silent and the manager falls back

        Assert.Equal(new[] { (IdA1, Romanised) }, await f.Search(Query(UserAr)));
    }

    [Fact]
    public async Task IncludeItemTypes_Series_ExcludesTheMovie_AndExcludeOnlyAppliesWithoutIncludes()
    {
        // A1 (Movie) and S (Series) carry the same Arabic title -> both match the romanised query.
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.AddVisible(SeriesItem(IdS), UserAr);

        // presence companion: no type filter -> both, ordered by id at equal score (aaaa... < dddd...)
        Assert.Equal(new[] { (IdA1, Romanised), (IdS, Romanised) }, await f.Search(Query(UserAr)));

        Assert.Equal(new[] { (IdS, Romanised) }, await f.Search(Query(UserAr, include: new[] { BaseItemKind.Series })));
        Assert.Equal(new[] { (IdS, Romanised) }, await f.Search(Query(UserAr, exclude: new[] { BaseItemKind.Movie })));

        // SearchProviderQuery docs: a non-empty include list is authoritative and the exclude list does not apply
        Assert.Equal(
            new[] { (IdA1, Romanised) },
            await f.Search(Query(UserAr, include: new[] { BaseItemKind.Movie }, exclude: new[] { BaseItemKind.Movie })));
    }

    [Fact]
    public async Task MediaTypes_Video_KeepsTheMovie_DropsTheSeries()
    {
        // Movie : Video -> MediaType.Video (Video.cs 253); Series : Folder : BaseItem -> MediaType.Unknown (BaseItem.cs 458)
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.AddVisible(SeriesItem(IdS), UserAr);

        Assert.Equal(new[] { (IdA1, Romanised) }, await f.Search(Query(UserAr, mediaTypes: new[] { MediaType.Video })));
    }

    [Fact]
    public async Task ParentId_RestrictsToThatSubtree()
    {
        // A1 lives under P1 (inside Library), A2 under P2 (inside Library); same title.
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), new[] { FolderP1, Library }, UserAr);
        f.AddVisible(Movie(IdA2), new[] { FolderP2, Library }, UserAr);

        Assert.Equal(new[] { (IdA1, Romanised) }, await f.Search(Query(UserAr, parentId: FolderP1)));

        // presence companion: the library contains both
        Assert.Equal(new[] { (IdA1, Romanised), (IdA2, Romanised) }, await f.Search(Query(UserAr, parentId: Library)));
    }

    [Fact]
    public async Task Limit_IsRespected_BestScoresFirst()
    {
        // merged: B 100, A1/A2/A3 90 (ties by id), C 50 -> Limit 2 keeps B and A1
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.AddVisible(Movie(IdA2), UserAr);
        f.AddVisible(Movie(IdA3), UserAr);
        f.Sql.Results.Add(new SearchResult(IdC, 50f));
        f.Sql.Results.Add(new SearchResult(IdB, 100f));

        Assert.Equal(new[] { (IdB, 100f), (IdA1, Romanised) }, await f.Search(Query(UserAr, limit: 2)));

        // companion: without a limit all five come back
        Assert.Equal(
            new[] { (IdB, 100f), (IdA1, Romanised), (IdA2, Romanised), (IdA3, Romanised), (IdC, 50f) },
            await f.Search(Query(UserAr)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("al")]
    [InlineData("el al")]
    [InlineData("3")]
    public async Task EmptyWhitespaceOrStopwordOnlyQuery_AddsNothing_AndDoesNotDelegate(string term)
    {
        // "al"/"el" are stopwords and "3" keys to empty -> no query keys -> no romanised hit -> silent (the manager's own SQL runs)
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.Sql.Results.Add(new SearchResult(IdB, 50f));

        Assert.Empty(await f.Search(Query(UserAr, term)));
        Assert.Empty(f.Sql.Queries);

        // presence companion: a real romanised query on the same fixture hits and delegates
        Assert.Equal(new[] { (IdA1, Romanised), (IdB, 50f) }, await f.Search(Query(UserAr)));
    }

    [Fact]
    public async Task NoUser_CannotSearch_AndAddsNothing()
    {
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);

        var anonymous = new SearchProviderQuery { SearchTerm = RomanisedQuery };
        Assert.False(f.Provider.CanSearch(anonymous));
        Assert.Empty(await f.Search(anonymous));

        Assert.True(f.Provider.CanSearch(Query(UserAr)));
    }

    [Fact]
    public async Task FailOpen_DelegateThrows_ReturnsNothing_NotAPartialRomanisedList()
    {
        // A romanised-only list would make the manager skip SQL and lose every plain match; nothing lets it fall back instead.
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.Sql.Throw = true;

        Assert.Empty(await f.Search(Query(UserAr)));

        f.Sql.Throw = false;
        Assert.Equal(new[] { (IdA1, Romanised) }, await f.Search(Query(UserAr)));
    }

    [Fact]
    public async Task FailOpen_AccessCheckThrows_ReturnsNothing()
    {
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.Access.Throw = true;

        Assert.Empty(await f.Search(Query(UserAr)));

        f.Access.Throw = false;
        Assert.Equal(new[] { (IdA1, Romanised) }, await f.Search(Query(UserAr)));
    }

    [Fact]
    public async Task BaseInterfaceSearchAsync_ReturnsTheSameResults()
    {
        var f = new Fixture();
        f.AddVisible(Movie(IdA1), UserAr);
        f.Sql.Results.Add(new SearchResult(IdB, 50f));

        var list = await ((ISearchProvider)f.Provider).SearchAsync(Query(UserAr), CancellationToken.None);

        Assert.Equal(new[] { (IdA1, Romanised), (IdB, 50f) }, list.Select(r => (r.ItemId, r.Score)).ToArray());
    }

    private static Movie Movie(Guid id) =>
        new() { Id = id, Name = "The Suitor at the Door", OriginalTitle = KhattabTitle, OriginalLanguage = "ar" };

    private static Series SeriesItem(Guid id) =>
        new() { Id = id, Name = "Suitors", OriginalTitle = KhattabTitle, OriginalLanguage = "ar" };

    private static SearchProviderQuery Query(
        Guid? user,
        string term = RomanisedQuery,
        BaseItemKind[]? include = null,
        BaseItemKind[]? exclude = null,
        MediaType[]? mediaTypes = null,
        Guid? parentId = null,
        int? limit = null) =>
        new()
        {
            SearchTerm = term,
            UserId = user,
            IncludeItemTypes = include ?? Array.Empty<BaseItemKind>(),
            ExcludeItemTypes = exclude ?? Array.Empty<BaseItemKind>(),
            MediaTypes = mediaTypes ?? Array.Empty<MediaType>(),
            ParentId = parentId,
            Limit = limit,
        };

    private sealed class Fixture
    {
        private readonly SearchLibraryManager _library;
        private readonly Lazy<OriginalTitlesSearchProvider> _provider;

        public Fixture()
        {
            var (manager, fake) = SearchLibraryManager.Create();
            _library = fake;
            var events = new LibraryItemEvents(manager, NullLogger<LibraryItemEvents>.Instance);
            var index = new TitleIndex(manager, events, NullLogger<TitleIndex>.Instance);

            var config = new PluginConfiguration();
            config.Users.Add(new UserLanguageEntry { UserId = UserAr, Languages = { "ar" } });
            config.Users.Add(new UserLanguageEntry { UserId = UserHidden, Languages = { "ar" } });
            var settings = new UserLanguageSettings(() => config);

            var searchManager = new FakeSearchManager();
            searchManager.Providers.Add(Sql);

            // Created on first use, after the test has filled the snapshot: the constructor starts a background index build.
            _provider = new Lazy<OriginalTitlesSearchProvider>(() => new OriginalTitlesSearchProvider(
                searchManager, index, settings, Access, NullLogger<OriginalTitlesSearchProvider>.Instance));
        }

        public FakeInternalProvider Sql { get; } = new();

        public FakeItemAccess Access { get; } = new();

        public OriginalTitlesSearchProvider Provider => _provider.Value;

        public void AddVisible(BaseItem item, params Guid[] users) => AddVisible(item, Array.Empty<Guid>(), users);

        public void AddVisible(BaseItem item, Guid[] ancestors, params Guid[] users)
        {
            _library.Snapshot.Add(item);
            Access.Add(item, ancestors, users);
        }

        public async Task<(Guid, float)[]> Search(SearchProviderQuery query)
        {
            var results = new List<(Guid, float)>();
            await foreach (var r in Provider.SearchAsync(query, CancellationToken.None))
            {
                results.Add((r.ItemId, r.Score));
            }

            return results.ToArray();
        }
    }
}
