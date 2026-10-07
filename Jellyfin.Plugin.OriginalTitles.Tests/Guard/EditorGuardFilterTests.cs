using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Jellyfin.Plugin.OriginalTitles.Api;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
using Jellyfin.Plugin.OriginalTitles.Guard;
using Jellyfin.Plugin.OriginalTitles.Tests.Display;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Guard;

/// <summary>
/// The editor guard. Real v12.2 behaviour it defends against (ItemUpdateController.UpdateItem, Jellyfin.Api/Controllers/ItemUpdateController.cs
/// 75/241/286-288/316-357; reproduced on the harness, see the task report): the posted BaseItemDto's Name is copied into the item, a series'
/// Name cascades into every season/episode SeriesName, and an episode/season's non-null SeriesName is copied. A save of the DISPLAYED item
/// (wrapper on) would therefore persist the original title. The filter runs before the action and puts the stored value back into the
/// request, so the controller's one write keeps the real name; the filter itself never writes.
/// Every fixture value is a literal; expected values are derived from them in comments.
/// </summary>
[Collection(BaseItemLibraryManagerCollection.Name)]
public sealed class EditorGuardFilterTests : IDisposable
{
    private const string MovieName = "Mouch Fi Thniti";
    private const string MovieOriginal = "موش في ثنيتي";

    private const string SeriesName = "Khottab Al Bab";
    private const string SeriesOriginal = "الخطاب على الباب";

    private const string FrenchName = "Amelie";
    private const string FrenchOriginal = "Le Fabuleux Destin d'Amelie Poulain";

    // Nobody enables "de".
    private const string GermanName = "The Boat";
    private const string GermanOriginal = "Das Boot";

    private const string EpisodeName = "Episode One";
    private const string SeasonName = "Season 1";

    private static readonly Guid ArabicUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FrenchUser = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public EditorGuardFilterTests()
    {
        BaseItem.LibraryManager = null!;
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = null!;
    }

    [Fact]
    public void MovieSave_CarryingTheDisplayedOriginal_KeepsTheStoredName_AndTheFilterNeverWrites()
    {
        // Stored: Name MovieName, OriginalTitle MovieOriginal, language "ar". ArabicUser enables "ar", so the wrapper shows MovieOriginal
        // (non-blank, != Name, enabled). The editor posts that DTO back unchanged: request.Name = MovieOriginal -> the guard puts the stored
        // MovieName back. Every other request field stays as posted (OriginalTitle = MovieOriginal).
        var movie = ArabicMovie();
        var filter = Build(out var library, out var log, movie);
        var request = new BaseItemDto { Name = MovieOriginal, OriginalTitle = MovieOriginal, OriginalLanguage = "ar" };

        Run(filter, movie.Id, request);

        Assert.Equal(MovieName, request.Name);
        Assert.Equal(MovieOriginal, request.OriginalTitle);

        // No write path at all: the only library call is the one read of the stored item (one GetItemById(Guid)).
        Assert.Equal(new[] { "GetItemById" }, library.Calls);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void SeriesSave_CarryingTheDisplayedOriginal_KeepsTheStoredName()
    {
        // Same rule for a Series: stored SeriesName/SeriesOriginal/"ar", ArabicUser enables "ar" -> displayed SeriesOriginal.
        // Fixing request.Name before the action is what keeps the controller's cascade (season/episode SeriesName = series Name) real.
        var series = ArabicSeries();
        var filter = Build(out _, out _, series);
        var request = new BaseItemDto { Name = SeriesOriginal };

        Run(filter, series.Id, request);

        Assert.Equal(SeriesName, request.Name);
    }

    [Fact]
    public void GenuineRename_IsKept_WhileTheDisplayedOriginalIsReverted()
    {
        // "Mouch Edited" is not what anybody is shown (the only displayed value is MovieOriginal) -> a real edit, kept as posted.
        var movie = ArabicMovie();
        var filter = Build(out _, out var log, movie);
        var edited = new BaseItemDto { Name = "Mouch Edited" };

        Run(filter, movie.Id, edited);

        Assert.Equal("Mouch Edited", edited.Name);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Information);

        // Companion: the displayed value on the same item is reverted to the stored MovieName.
        var displayed = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, displayed);
        Assert.Equal(MovieName, displayed.Name);
    }

    [Fact]
    public void StoredNameAlreadyEqualToOriginal_IsUntouched_AndNothingIsRestored()
    {
        // Stored Name == OriginalTitle == MovieOriginal ("ar" enabled): the wrapper shows nothing different (Name == OriginalTitle), so
        // there is no displayed swap and no real name to restore. This is also the shape of an item that is ALREADY swapped in the DB
        // (blind spot): the guard cannot tell, and leaves it. The request keeps MovieOriginal and no restore is logged.
        var same = new Movie { Id = Guid.NewGuid(), Name = MovieOriginal, OriginalTitle = MovieOriginal, OriginalLanguage = "ar" };
        var movie = ArabicMovie();
        var filter = Build(out _, out var log, same, movie);
        var request = new BaseItemDto { Name = MovieOriginal };

        Run(filter, same.Id, request);

        Assert.Equal(MovieOriginal, request.Name);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Information);

        // Companion: the regular item (Name MovieName != OriginalTitle) logs exactly one restore.
        Run(filter, movie.Id, new BaseItemDto { Name = MovieOriginal });
        Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void LanguageEnabledForNobody_DeliberateRenameToTheOriginal_IsKept()
    {
        // German movie ("de"): no configured user enables "de", so nobody is ever shown GermanOriginal; a request setting
        // Name = GermanOriginal is a deliberate admin edit and stays "Das Boot".
        var german = new Movie { Id = Guid.NewGuid(), Name = GermanName, OriginalTitle = GermanOriginal, OriginalLanguage = "de" };
        var french = new Movie { Id = Guid.NewGuid(), Name = FrenchName, OriginalTitle = FrenchOriginal, OriginalLanguage = "fr" };
        var filter = Build(out _, out _, german, french);
        var germanRequest = new BaseItemDto { Name = GermanOriginal };

        Run(filter, german.Id, germanRequest);

        Assert.Equal(GermanOriginal, germanRequest.Name);

        // Companion: "fr" is enabled for FrenchUser only (not for ArabicUser). "At least one user" sees FrenchOriginal, so the same
        // kind of request on the French movie is reverted to FrenchName. The requesting user plays no part (API keys have none).
        var frenchRequest = new BaseItemDto { Name = FrenchOriginal };
        Run(filter, french.Id, frenchRequest);
        Assert.Equal(FrenchName, frenchRequest.Name);
    }

    [Fact]
    public void EpisodeAndSeasonSaves_CarryingTheDisplayedSeriesOriginal_KeepTheStoredSeriesName_GenuineEditsAreKept()
    {
        // Episode/Season of the Arabic series: the wrapper shows SeriesName = SeriesOriginal (series-side decision, ruling R6) and never
        // touches their own Name. The controller copies a non-null request.SeriesName into the item (v12.2 ~286-288), so a posted-back
        // SeriesOriginal is replaced by the stored SeriesName ("Khottab Al Bab"); their own Name stays as posted. The own Names are posted
        // EDITED ("... (Director's Cut)", "... (Remastered)", both differ from the stored EpisodeName/SeasonName), so an implementation that
        // also wrote the stored own Name back would turn them into "Episode One"/"Season 1" and fail.
        var series = ArabicSeries();
        var episode = new Episode { Id = Guid.NewGuid(), Name = EpisodeName, SeriesId = series.Id, SeriesName = SeriesName };
        var season = new Season { Id = Guid.NewGuid(), Name = SeasonName, SeriesId = series.Id, SeriesName = SeriesName };
        var filter = Build(out _, out _, series, episode, season);

        var episodeRequest = new BaseItemDto { Name = EpisodeName + " (Director's Cut)", SeriesName = SeriesOriginal };
        Run(filter, episode.Id, episodeRequest);
        Assert.Equal(SeriesName, episodeRequest.SeriesName);
        Assert.Equal("Episode One (Director's Cut)", episodeRequest.Name);

        var seasonRequest = new BaseItemDto { Name = SeasonName + " (Remastered)", SeriesName = SeriesOriginal };
        Run(filter, season.Id, seasonRequest);
        Assert.Equal(SeriesName, seasonRequest.SeriesName);
        Assert.Equal("Season 1 (Remastered)", seasonRequest.Name);

        // Companion: a SeriesName nobody is shown ("Another Show") is a real edit and is kept.
        var edited = new BaseItemDto { Name = EpisodeName, SeriesName = "Another Show" };
        Run(filter, episode.Id, edited);
        Assert.Equal("Another Show", edited.SeriesName);
    }

    [Fact]
    public void RepairOfATrulySwappedItem_PostedOriginalTitleEqualsStoredName_IsKeptAsPosted()
    {
        // Stored TRULY swapped: Name = MovieOriginal (Arabic), OriginalTitle = MovieName (Latin), "ar". ArabicUser is shown
        // OriginalTitle = MovieName (non-blank, != Name, enabled). The admin repairs it: leaves Name as shown (MovieName) and sets
        // OriginalTitle = MovieOriginal. Restoring Name would give Name = OriginalTitle = MovieOriginal (the Latin title lost). The posted
        // OriginalTitle equals the stored Name -> explicit swap signal -> the request is kept: Name MovieName, OriginalTitle MovieOriginal.
        var swapped = new Movie { Id = Guid.NewGuid(), Name = MovieOriginal, OriginalTitle = MovieName, OriginalLanguage = "ar" };
        var filter = Build(out _, out var log, swapped);
        var request = new BaseItemDto { Name = MovieName, OriginalTitle = MovieOriginal, OriginalLanguage = "ar" };

        Run(filter, swapped.Id, request);

        Assert.Equal(MovieName, request.Name);
        Assert.Equal(MovieOriginal, request.OriginalTitle);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Information);

        // Companion: posting the same swapped item back unchanged (OriginalTitle still MovieName, != stored Name MovieOriginal) is the
        // accidental save and keeps the stored Name MovieOriginal.
        var unchanged = new BaseItemDto { Name = MovieName, OriginalTitle = MovieName };
        Run(filter, swapped.Id, unchanged);
        Assert.Equal(MovieOriginal, unchanged.Name);
    }

    [Fact]
    public void DeliberateSwapOfANormalItem_IsKeptAsPosted_WhileAnOriginalTitleTypoFixIsStillGuarded()
    {
        // Stored MovieName / MovieOriginal ("ar"). The admin swaps on purpose: Name = MovieOriginal (as shown), OriginalTitle = MovieName.
        // Restoring would give Name = OriginalTitle = MovieName (the Arabic title lost). Posted OriginalTitle == stored Name -> kept.
        var movie = ArabicMovie();
        var filter = Build(out _, out _, movie);
        var swap = new BaseItemDto { Name = MovieOriginal, OriginalTitle = MovieName };

        Run(filter, movie.Id, swap);

        Assert.Equal(MovieOriginal, swap.Name);
        Assert.Equal(MovieName, swap.OriginalTitle);

        // Companion: an OriginalTitle typo fix (last letter U+064A -> U+0649) with the displayed Name left as shown is still the
        // accidental case: the posted OriginalTitle differs from the stored Name, so Name goes back to MovieName and the fix is kept.
        const string FixedOriginal = "موش في ثنيتى";
        var typoFix = new BaseItemDto { Name = MovieOriginal, OriginalTitle = FixedOriginal };
        Run(filter, movie.Id, typoFix);
        Assert.Equal(MovieName, typoFix.Name);
        Assert.Equal(FixedOriginal, typoFix.OriginalTitle);
    }

    [Fact]
    public void SessionUser_ComparesAgainstThatUsersViewOnly()
    {
        // A session (the web editor) carries the Jellyfin-UserId claim (v12.2 CustomAuthenticationHandler); the guard compares with
        // what THAT user is shown. Arabic movie, "ar" enabled for ArabicUser only.
        var movie = ArabicMovie();
        var filter = Build(out _, out _, movie);

        // (a) ArabicUser is shown MovieOriginal and posts it back -> restored to MovieName.
        var shown = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, shown, Session(ArabicUser));
        Assert.Equal(MovieName, shown.Name);

        // (b) FrenchUser ("fr" only) is shown MovieName; typing MovieOriginal is deliberate -> kept, although ArabicUser would see it.
        var deliberate = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, deliberate, Session(FrenchUser));
        Assert.Equal(MovieOriginal, deliberate.Name);

        // (b2) A user with no settings entry at all -> also kept.
        var stranger = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, stranger, Session(Guid.Parse("33333333-3333-3333-3333-333333333333")));
        Assert.Equal(MovieOriginal, stranger.Name);
    }

    [Fact]
    public void ApiKeyRequestWithoutUser_FallsBackToAnyConfiguredUser()
    {
        // An API key has no user: core still adds Jellyfin-UserId = Guid.Empty.ToString("N") (AuthorizationInfo.UserId => User?.Id ??
        // Guid.Empty). Then "shown to any configured user" applies: ArabicUser is shown MovieOriginal -> restored to MovieName.
        var movie = ArabicMovie();
        var filter = Build(out _, out _, movie);

        var apiKey = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, apiKey, Session(Guid.Empty));
        Assert.Equal(MovieName, apiKey.Name);

        // Same with no claim at all.
        var noClaim = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, noClaim, new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.Equal(MovieName, noClaim.Name);
    }

    [Fact]
    public void CaseOnlyVariantOfTheDisplayedOriginal_IsAGenuineEdit()
    {
        // French movie ("fr" for FrenchUser): displayed FrenchOriginal. Its upper-case variant is not what anybody was shown (the
        // comparison is ordinal) -> kept as posted.
        var french = new Movie { Id = Guid.NewGuid(), Name = FrenchName, OriginalTitle = FrenchOriginal, OriginalLanguage = "fr" };
        var filter = Build(out _, out _, french);
        var request = new BaseItemDto { Name = "LE FABULEUX DESTIN D'AMELIE POULAIN" };

        Run(filter, french.Id, request);

        Assert.Equal("LE FABULEUX DESTIN D'AMELIE POULAIN", request.Name);

        // Companion: the exact displayed value is reverted to FrenchName.
        var exact = new BaseItemDto { Name = FrenchOriginal };
        Run(filter, french.Id, exact);
        Assert.Equal(FrenchName, exact.Name);
    }

    [Fact]
    public void OtherActions_AreUntouched_AndNotEvenLookedUp()
    {
        // Same argument shape, but not ItemUpdateController.UpdateItem -> no lookup, no change.
        var movie = ArabicMovie();
        var filter = Build(out var library, out _, movie);

        var otherAction = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, otherAction, Descriptor("ItemUpdate", "UpdateItemContentType"));
        var otherController = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, otherController, Descriptor("Items", "UpdateItem"));
        var notAController = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, notAController, new ActionDescriptor());

        Assert.Equal(MovieOriginal, otherAction.Name);
        Assert.Equal(MovieOriginal, otherController.Name);
        Assert.Equal(MovieOriginal, notAController.Name);
        Assert.Empty(library.Calls);

        // Companion: the real action on the same item is reverted.
        var real = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, real);
        Assert.Equal(MovieName, real.Name);
    }

    [Fact]
    public void UpdateItemWithUnexpectedArguments_LogsADiagnosableError_AndChangesNothing()
    {
        // Tripwire: the guard depends on the v12.2 parameter names itemId (Guid) and request (BaseItemDto). If a Jellyfin upgrade renames
        // them, the guard can no longer act: it must say so (Error naming the expected arguments), not silently stop working.
        var movie = ArabicMovie();
        var filter = Build(out _, out var log, movie);
        var request = new BaseItemDto { Name = MovieOriginal };
        var arguments = new Dictionary<string, object?> { ["id"] = movie.Id, ["dto"] = request };

        filter.OnActionExecuting(Context(Descriptor("ItemUpdate", "UpdateItem"), arguments));

        Assert.Equal(MovieOriginal, request.Name);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("itemId", error.Message, StringComparison.Ordinal);
        Assert.Contains("request", error.Message, StringComparison.Ordinal);

        // Companion: the expected shape on the same item is reverted and logs no further error.
        var good = new BaseItemDto { Name = MovieOriginal };
        Run(filter, movie.Id, good);
        Assert.Equal(MovieName, good.Name);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void LookupFailure_FailsOpen_RequestUnchangedAndWarningLogged()
    {
        // Fail open: a throwing library leaves the request exactly as posted and logs a Warning; nothing escapes.
        var movie = ArabicMovie();
        var throwing = Build(out _, out var log, throwOnGet: true, movie);
        var request = new BaseItemDto { Name = MovieOriginal };

        Run(throwing, movie.Id, request);

        Assert.Equal(MovieOriginal, request.Name);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);

        // Companion: a working library reverts the same request.
        var working = Build(out _, out _, movie);
        Run(working, movie.Id, request);
        Assert.Equal(MovieName, request.Name);
    }

    [Fact]
    public void UnknownItem_IsUntouched()
    {
        // GetItemById returns null for an id the library does not know (the controller then answers 404): nothing to compare with.
        var movie = ArabicMovie();
        var filter = Build(out _, out var log, movie);
        var request = new BaseItemDto { Name = MovieOriginal };

        Run(filter, Guid.NewGuid(), request);

        Assert.Equal(MovieOriginal, request.Name);
        Assert.Empty(log.Entries);

        // Companion: the known item is reverted.
        Run(filter, movie.Id, request);
        Assert.Equal(MovieName, request.Name);
    }

    [Fact]
    public void Registrator_AddsTheGuardGloballyToMvc_AsOneSingleton_LikeTheHintsFilter()
    {
        // Same shape as SearchHintsFilter (Task 4 review): one Singleton descriptor and exactly one global ServiceFilterAttribute, so MVC
        // resolves that one instance for every action instead of building a TypeFilterAttribute instance per request. Safe because the
        // guard keeps no per-request state and depends only on singletons.
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(EditorGuardFilter));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        Assert.Single(filters.OfType<ServiceFilterAttribute>(), f => f.ServiceType == typeof(EditorGuardFilter));
        Assert.DoesNotContain(filters.OfType<TypeFilterAttribute>(), f => f.ImplementationType == typeof(EditorGuardFilter));

        // Companion: the hints filter is registered in its current shape next to it.
        Assert.Single(filters.OfType<ServiceFilterAttribute>(), f => f.ServiceType == typeof(SearchHintsFilter));
    }

    [Fact]
    public void OneInstance_LogsEveryFailureAndEveryTripwire_ThereIsNoLogOnceState()
    {
        // The guard is a process-wide singleton. Unlike the hints filter it has NO log-once state: a metadata save is rare and each
        // unguarded save must be visible. On ONE instance: failing lookup, failing lookup, bad arguments, bad arguments -> the level
        // sequence is exactly [Warning, Warning, Error, Error] (nothing demoted to Debug, nothing swallowed).
        var movie = ArabicMovie();
        var filter = Build(out _, out var log, throwOnGet: true, movie);
        var badArguments = new Dictionary<string, object?> { ["id"] = movie.Id, ["dto"] = new BaseItemDto { Name = MovieOriginal } };

        Run(filter, movie.Id, new BaseItemDto { Name = MovieOriginal });
        Run(filter, movie.Id, new BaseItemDto { Name = MovieOriginal });
        filter.OnActionExecuting(Context(Descriptor("ItemUpdate", "UpdateItem"), badArguments));
        filter.OnActionExecuting(Context(Descriptor("ItemUpdate", "UpdateItem"), badArguments));

        Assert.Equal(new[] { LogLevel.Warning, LogLevel.Warning, LogLevel.Error, LogLevel.Error }, log.Entries.Select(e => e.Level));
    }

    // ---------- helpers ----------

    private static Movie ArabicMovie() => new() { Id = Guid.NewGuid(), Name = MovieName, OriginalTitle = MovieOriginal, OriginalLanguage = "ar" };

    private static Series ArabicSeries() => new() { Id = Guid.NewGuid(), Name = SeriesName, OriginalTitle = SeriesOriginal, OriginalLanguage = "ar" };

    private static EditorGuardFilter Build(out RecordingLibraryManager library, out CapturingLogger log, params BaseItem[] items)
        => Build(out library, out log, false, items);

    private static EditorGuardFilter Build(out RecordingLibraryManager library, out CapturingLogger log, bool throwOnGet, params BaseItem[] items)
    {
        var config = new PluginConfiguration();
        config.Users.Add(new UserLanguageEntry { UserId = ArabicUser, Languages = new List<string> { "ar" } });
        config.Users.Add(new UserLanguageEntry { UserId = FrenchUser, Languages = new List<string> { "fr" } });
        var settings = new UserLanguageSettings(() => config);

        var proxy = RecordingLibraryManager.Create(throwOnGet, items);
        library = (RecordingLibraryManager)(object)proxy;

        // Episode.Series / Season.Series resolve through the static (docs/spike.md (c)); the same recording fake answers them.
        BaseItem.LibraryManager = proxy;
        log = new CapturingLogger();
        return new EditorGuardFilter(new DisplayNameResolver(settings), settings, proxy, log);
    }

    private static ControllerActionDescriptor Descriptor(string controller, string action)
        => new() { ControllerName = controller, ActionName = action };

    private static void Run(EditorGuardFilter filter, Guid itemId, BaseItemDto request, ActionDescriptor? descriptor = null)
        => Run(filter, itemId, request, new ClaimsPrincipal(new ClaimsIdentity()), descriptor);

    private static void Run(EditorGuardFilter filter, Guid itemId, BaseItemDto request, ClaimsPrincipal user, ActionDescriptor? descriptor = null)
    {
        // Shape of ItemUpdateController.UpdateItem([FromRoute] Guid itemId, [FromBody] BaseItemDto request) (v12.2 line 75).
        var arguments = new Dictionary<string, object?> { ["itemId"] = itemId, ["request"] = request };
        filter.OnActionExecuting(Context(descriptor ?? Descriptor("ItemUpdate", "UpdateItem"), arguments, user));
    }

    // The claim core's CustomAuthenticationHandler adds (v12.2): InternalClaimTypes.UserId = userId.ToString("N").
    private static ClaimsPrincipal Session(Guid userId)
        => new(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", userId.ToString("N")) }, "CustomAuthentication"));

    private static ActionExecutingContext Context(ActionDescriptor descriptor, IDictionary<string, object?> arguments, ClaimsPrincipal? user = null)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) }, new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());
    }

    /// <summary>Answers only the non-generic GetItemById(Guid) (optionally throwing) and records the name of EVERY member called.</summary>
    public class RecordingLibraryManager : DispatchProxy
    {
        private readonly Dictionary<Guid, BaseItem> _items = new();
        private bool _throwOnGet;

        public List<string> Calls { get; } = new();

        public static ILibraryManager Create(bool throwOnGet, params BaseItem[] items)
        {
            var proxy = Create<ILibraryManager, RecordingLibraryManager>();
            var fake = (RecordingLibraryManager)(object)proxy;
            fake._throwOnGet = throwOnGet;
            foreach (var item in items)
            {
                fake._items[item.Id] = item;
            }

            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod?.Name ?? "?");
            if (targetMethod is { Name: "GetItemById", IsGenericMethod: false } && args is { Length: 1 } && args[0] is Guid id)
            {
                if (_throwOnGet)
                {
                    throw new InvalidOperationException("library unavailable");
                }

                return _items.TryGetValue(id, out var item) ? item : null;
            }

            throw new NotSupportedException("RecordingLibraryManager only answers GetItemById(Guid), got " + targetMethod?.Name);
        }
    }

    public sealed class CapturingLogger : ILogger<EditorGuardFilter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
