using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalTitles.Api;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
using Jellyfin.Plugin.OriginalTitles.Tests.Display;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Api;

/// <summary>
/// The /Search/Hints filter: SearchController (v12.2 ~141/178) copies item.Name and SeriesName into SearchHint directly, bypassing
/// IDtoService. The filter rewrites hint.Name (Movie/Series) and hint.Series (Episode/Season) after the action ran, for the user the
/// controller resolved. Every fixture value is a literal; expected values are derived from them in comments.
/// </summary>
[Collection(BaseItemLibraryManagerCollection.Name)]
public sealed class SearchHintsFilterTests : IDisposable
{
    private const string MovieName = "Mouch Fi Thniti";
    private const string MovieOriginal = "موش في ثنيتي";

    private const string SeriesName = "Khottab Al Bab";
    private const string SeriesOriginal = "الخطاب على الباب";

    private const string FrenchName = "Amelie";
    private const string FrenchOriginal = "Le Fabuleux Destin d'Amelie Poulain";

    private const string EpisodeName = "Episode One";
    private const string SeasonName = "Season 1";
    private const string PersonName = "Some Actor";

    private static readonly Guid ArabicUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FrenchUser = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public SearchHintsFilterTests()
    {
        BaseItem.LibraryManager = null!;
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = null!;
    }

    // ---------- ResolveUserId: mirrors RequestHelpers.GetUserId (v12.2 Jellyfin.Api/Helpers/RequestHelpers.cs 67-85) ----------

    [Fact]
    public void ResolveUserId_ArgumentWinsOverClaim()
    {
        // Argument ArabicUser, claim FrenchUser -> the argument (RequestHelpers returns userId.Value when it is not null/empty).
        Assert.Equal(ArabicUser, SearchHintsFilter.ResolveUserId(ArabicUser, Principal(FrenchUser.ToString("N"))));
    }

    [Fact]
    public void ResolveUserId_NullOrEmptyArgumentFallsBackToClaim()
    {
        // userId.IsNullOrEmpty() -> authenticatedUserId = the Jellyfin-UserId claim.
        Assert.Equal(FrenchUser, SearchHintsFilter.ResolveUserId(null, Principal(FrenchUser.ToString("N"))));
        Assert.Equal(FrenchUser, SearchHintsFilter.ResolveUserId(Guid.Empty, Principal(FrenchUser.ToString("N"))));

        // Core compares the claim type with OrdinalIgnoreCase (ClaimsPrincipalExtensions.GetClaimValue).
        Assert.Equal(FrenchUser, SearchHintsFilter.ResolveUserId(null, Principal(FrenchUser.ToString("N"), "jellyfin-userid")));
    }

    [Fact]
    public void ResolveUserId_NoUsableClaimGivesEmpty()
    {
        // Presence companion: a real claim resolves, so the empty results below are not an always-empty implementation.
        Assert.Equal(ArabicUser, SearchHintsFilter.ResolveUserId(null, Principal(ArabicUser.ToString("N"))));

        // API key without userId: the harness logged the claim as 32 zeros -> Guid.Empty. No claim at all -> Guid.Empty (core: default).
        Assert.Equal(Guid.Empty, SearchHintsFilter.ResolveUserId(null, Principal("00000000000000000000000000000000")));
        Assert.Equal(Guid.Empty, SearchHintsFilter.ResolveUserId(null, new ClaimsPrincipal(new ClaimsIdentity())));
    }

    // ---------- the filter ----------

    [Fact]
    public async Task MovieHint_EnabledUserSeesOriginal_OtherUserSeesTranslated()
    {
        var arabic = ArabicMovie();
        var french = FrenchMovie();
        var filter = Build(out _, arabic, french);

        // ArabicUser enables "ar": the "ar" movie's Name becomes its OriginalTitle (MovieOriginal); "fr" stays FrenchName.
        var forArabic = await RunAsync(filter, ArabicUser, Hint(arabic, MovieName), Hint(french, FrenchName));
        Assert.Equal(MovieOriginal, forArabic.SearchHints[0].Name);
        Assert.Equal(FrenchName, forArabic.SearchHints[1].Name);

        // FrenchUser enables only "fr": the "ar" movie stays MovieName; the "fr" movie becomes FrenchOriginal (presence companion).
        var forFrench = await RunAsync(filter, FrenchUser, Hint(arabic, MovieName), Hint(french, FrenchName));
        Assert.Equal(MovieName, forFrench.SearchHints[0].Name);
        Assert.Equal(FrenchOriginal, forFrench.SearchHints[1].Name);

        // Display layer is read-only: the items keep their stored values.
        Assert.Equal(MovieName, arabic.Name);
        Assert.Equal(FrenchName, french.Name);
    }

    [Fact]
    public async Task SeriesHint_NameRewritten_SeriesFieldUntouched()
    {
        var series = ArabicSeries();
        var filter = Build(out _, series);

        // Series is a Movie/Series kind: Name -> SeriesOriginal. Controller never sets hint.Series for a Series item: stays null.
        var result = await RunAsync(filter, ArabicUser, Hint(series, SeriesName));
        Assert.Equal(SeriesOriginal, result.SearchHints[0].Name);
        Assert.Null(result.SearchHints[0].Series);
    }

    [Fact]
    public async Task EpisodeAndSeasonHints_SeriesRewritten_OwnNameUntouched()
    {
        var series = ArabicSeries();
        var episode = new Episode { Id = Guid.NewGuid(), Name = EpisodeName, SeriesName = SeriesName, SeriesId = series.Id };
        var season = new Season { Id = Guid.NewGuid(), Name = SeasonName, SeriesName = SeriesName, SeriesId = series.Id };
        BaseItem.LibraryManager = FakeLibraryManager.Create(series);
        var filter = Build(out _, series, episode, season);

        // Controller: hint.Series = IHasSeries.SeriesName (= SeriesName). The series qualifies for ArabicUser -> SeriesOriginal.
        // The episode/season's own Name is never touched.
        var forArabic = await RunAsync(filter, ArabicUser, Hint(episode, EpisodeName, SeriesName), Hint(season, SeasonName, SeriesName));
        Assert.Equal(EpisodeName, forArabic.SearchHints[0].Name);
        Assert.Equal(SeriesOriginal, forArabic.SearchHints[0].Series);
        Assert.Equal(SeasonName, forArabic.SearchHints[1].Name);
        Assert.Equal(SeriesOriginal, forArabic.SearchHints[1].Series);

        // FrenchUser does not enable "ar": Series stays SeriesName.
        var forFrench = await RunAsync(filter, FrenchUser, Hint(episode, EpisodeName, SeriesName), Hint(season, SeasonName, SeriesName));
        Assert.Equal(SeriesName, forFrench.SearchHints[0].Series);
        Assert.Equal(SeriesName, forFrench.SearchHints[1].Series);
    }

    [Fact]
    public async Task HintOfAnotherKind_IsNotLookedUpOrRewritten()
    {
        var arabic = ArabicMovie();
        var filter = Build(out _, arabic);

        // A Person hint whose Id happens to resolve to a qualifying Movie: only hint kinds Movie/Series/Episode/Season are
        // considered, so PersonName stays. The Movie hint for the same item (presence companion) becomes MovieOriginal.
        var person = Hint(arabic, PersonName);
        person.Type = BaseItemKind.Person;
        var result = await RunAsync(filter, ArabicUser, person, Hint(arabic, MovieName));
        Assert.Equal(PersonName, result.SearchHints[0].Name);
        Assert.Equal(MovieOriginal, result.SearchHints[1].Name);
    }

    [Fact]
    public async Task EmptyUser_IsNeverRewritten()
    {
        var arabic = ArabicMovie();
        var filter = Build(out _, arabic);

        // The config also enables "ar" for Guid.Empty: a filter that rewrote for an unresolved user would show MovieOriginal.
        // API key, no userId -> claim of zeros -> Guid.Empty -> untouched.
        var anonymous = await RunAsync(filter, null, Principal("00000000000000000000000000000000"), Hint(arabic, MovieName));
        Assert.Equal(MovieName, anonymous.SearchHints[0].Name);

        // Presence companion: no userId, but the session's claim is ArabicUser -> rewritten through the claim path.
        var session = await RunAsync(filter, null, Principal(ArabicUser.ToString("N")), Hint(arabic, MovieName));
        Assert.Equal(MovieOriginal, session.SearchHints[0].Name);
    }

    [Fact]
    public async Task FailOpen_ThrowingHintStaysUnchanged_OthersRewritten()
    {
        var arabic = ArabicMovie();

        // Own language null + SeriesId set + BaseItem.LibraryManager null -> Episode.Series throws NullReferenceException (docs/spike.md (c)).
        var throwing = new Episode { Id = Guid.NewGuid(), Name = EpisodeName, SeriesName = SeriesName, SeriesId = Guid.NewGuid(), OriginalLanguage = null };
        var filter = Build(out var log, arabic, throwing);

        var result = await RunAsync(filter, ArabicUser, Hint(throwing, EpisodeName, SeriesName), Hint(arabic, MovieName));
        Assert.Equal(EpisodeName, result.SearchHints[0].Name);
        Assert.Equal(SeriesName, result.SearchHints[0].Series);
        Assert.Equal(MovieOriginal, result.SearchHints[1].Name);

        // The failure is logged (Warning the first time per exception type in the process, Debug afterwards).
        var logged = Assert.Single(log.Entries);
        Assert.IsType<NullReferenceException>(logged);
    }

    [Fact]
    public async Task LogOnce_SameExceptionTypeWarnsOnceThenDebug_PerFilterInstance()
    {
        // Two hints that both throw NullReferenceException (same setup as FailOpen: own language null, SeriesId set,
        // BaseItem.LibraryManager null).
        var first = new Episode { Id = Guid.NewGuid(), Name = EpisodeName, SeriesName = SeriesName, SeriesId = Guid.NewGuid(), OriginalLanguage = null };
        var second = new Episode { Id = Guid.NewGuid(), Name = EpisodeName, SeriesName = SeriesName, SeriesId = Guid.NewGuid(), OriginalLanguage = null };
        var filter = Build(out var log, first, second);

        // One request, two failures of the same type: first at Warning, second at Debug.
        await RunAsync(filter, ArabicUser, Hint(first, EpisodeName, SeriesName), Hint(second, EpisodeName, SeriesName));
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Debug }, log.Levels);

        // A later request on the same (singleton) filter: that type was already warned about -> Debug only.
        await RunAsync(filter, ArabicUser, Hint(first, EpisodeName, SeriesName));
        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Debug, LogLevel.Debug }, log.Levels);

        // The log-once state belongs to the filter instance, not the process: a fresh instance warns again. (With process-global
        // state this would be Debug, and the outcome would depend on test order.)
        var fresh = Build(out var freshLog, first);
        await RunAsync(fresh, ArabicUser, Hint(first, EpisodeName, SeriesName));
        Assert.Equal(new[] { LogLevel.Warning }, freshLog.Levels);
    }

    [Fact]
    public async Task OtherResults_PassThroughUntouched()
    {
        var filter = Build(out var log, ArabicMovie());

        // Not a SearchHintResult: returned as is.
        var other = new ObjectResult("unrelated");
        var executed = await RunRawAsync(filter, ArabicUser, Principal(ArabicUser.ToString("N")), other);
        Assert.Same(other, executed.Result);
        Assert.Equal("unrelated", ((ObjectResult)executed.Result!).Value);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Registrator_AddsTheFilterGloballyToMvc()
    {
        // The registrator registers SearchHintsFilter once as a singleton and adds exactly one global ServiceFilterAttribute for it,
        // so MVC resolves that one instance (its log-once state lives as long as the process) instead of building one per request.
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(SearchHintsFilter));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        Assert.Single(filters.OfType<ServiceFilterAttribute>(), f => f.ServiceType == typeof(SearchHintsFilter));
        Assert.DoesNotContain(filters.OfType<TypeFilterAttribute>(), f => f.ImplementationType == typeof(SearchHintsFilter));
    }

    // ---------- helpers ----------

    private static ClaimsPrincipal Principal(string userIdClaim, string claimType = "Jellyfin-UserId")
        => new(new ClaimsIdentity(new[] { new Claim(claimType, userIdClaim) }, "Custom-Auth"));

    private static Movie ArabicMovie() => new() { Id = Guid.NewGuid(), Name = MovieName, OriginalTitle = MovieOriginal, OriginalLanguage = "ar" };

    private static Movie FrenchMovie() => new() { Id = Guid.NewGuid(), Name = FrenchName, OriginalTitle = FrenchOriginal, OriginalLanguage = "fr" };

    private static Series ArabicSeries() => new() { Id = Guid.NewGuid(), Name = SeriesName, OriginalTitle = SeriesOriginal, OriginalLanguage = "ar" };

    // Built like SearchController.GetSearchHintResult: Name = item.Name, Id = item.Id, Type = item.GetBaseItemKind(), Series = SeriesName.
    private static SearchHint Hint(BaseItem item, string name, string? series = null)
        => new() { Id = item.Id, Name = name, Type = item.GetBaseItemKind(), Series = series };

    private static SearchHintsFilter Build(out CapturingLogger log, params BaseItem[] items)
    {
        var config = new PluginConfiguration();
        config.Users.Add(new UserLanguageEntry { UserId = ArabicUser, Languages = new List<string> { "ar" } });
        config.Users.Add(new UserLanguageEntry { UserId = FrenchUser, Languages = new List<string> { "fr" } });

        // Guid.Empty also enables "ar": a filter that rewrites for an unresolved user would rewrite.
        config.Users.Add(new UserLanguageEntry { UserId = Guid.Empty, Languages = new List<string> { "ar" } });

        log = new CapturingLogger();
        ILibraryManager library = FakeLibraryManager.Create(items);
        return new SearchHintsFilter(new DisplayNameResolver(new UserLanguageSettings(() => config)), library, log);
    }

    private static Task<SearchHintResult> RunAsync(SearchHintsFilter filter, Guid userIdArgument, params SearchHint[] hints)
        => RunAsync(filter, userIdArgument, Principal("00000000000000000000000000000000"), hints);

    private static async Task<SearchHintResult> RunAsync(SearchHintsFilter filter, Guid? userIdArgument, ClaimsPrincipal principal, params SearchHint[] hints)
    {
        var executed = await RunRawAsync(filter, userIdArgument, principal, new ObjectResult(new SearchHintResult(hints, hints.Length)));
        return (SearchHintResult)((ObjectResult)executed.Result!).Value!;
    }

    // Shapes measured on the harness (docs in the task report): ActionArguments carries "userId" only when the query has it, and the
    // action result reaches OnActionExecuted as an ObjectResult whose Value is the SearchHintResult.
    private static async Task<ActionExecutedContext> RunRawAsync(SearchHintsFilter filter, Guid? userIdArgument, ClaimsPrincipal principal, IActionResult result)
    {
        var actionContext = new ActionContext(new DefaultHttpContext { User = principal }, new RouteData(), new ActionDescriptor());
        var arguments = new Dictionary<string, object?> { ["searchTerm"] = "x" };
        if (userIdArgument.HasValue)
        {
            arguments["userId"] = userIdArgument.Value;
        }

        var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());
        var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object()) { Result = result };
        await filter.OnActionExecutionAsync(executing, () => Task.FromResult(executed));
        return executed;
    }

    public sealed class CapturingLogger : ILogger<SearchHintsFilter>
    {
        public List<Exception?> Entries { get; } = new();

        public List<LogLevel> Levels { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel is LogLevel.Warning or LogLevel.Debug)
            {
                Entries.Add(exception);
                Levels.Add(logLevel);
            }
        }
    }
}
