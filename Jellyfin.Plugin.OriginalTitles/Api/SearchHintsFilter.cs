using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalTitles.Display;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalTitles.Api;

/// <summary>
/// Global MVC action filter that gives <c>GET /Search/Hints</c> the same names as the <see cref="OriginalTitleDtoService"/> wrapper.
/// </summary>
/// <remarks>
/// Why: <c>SearchController.GetSearchHintResult</c> (v12.2, Jellyfin.Api/Controllers/SearchController.cs ~141/178) builds each
/// <see cref="SearchHint"/> from <c>item.Name</c> and <c>IHasSeries.SeriesName</c> directly, bypassing <c>IDtoService</c>.
/// After the action ran, this filter rewrites <see cref="SearchHint.Name"/> of Movie/Series hints and <see cref="SearchHint.Series"/> of
/// Episode/Season hints per <see cref="DisplayNameResolver"/>; an episode or season hint's own <c>Name</c> is never touched.
/// Registered once for all actions (measured on a 12.2 harness: a filter added through <c>Configure&lt;MvcOptions&gt;</c> from the plugin
/// registrator runs for <c>/Search/Hints</c>); it acts only when the action result is an <see cref="ObjectResult"/> carrying a
/// <see cref="SearchHintResult"/>, which no other core action returns.
/// Invariants: never writes to a <c>BaseItem</c>; an unresolved user (<see cref="Guid.Empty"/>, e.g. an API key without
/// <c>userId</c>) is never rewritten; fail open: a failure on one hint leaves it exactly as core built it, the others are still
/// rewritten, nothing escapes, and each exception type is logged once at Warning per process (afterwards at Debug).
/// Not covered: hints of other kinds; any other
/// endpoint that builds names without <c>IDtoService</c>.
/// </remarks>
public class SearchHintsFilter : IAsyncActionFilter
{
    /// <summary>
    /// The claim carrying the authenticated user id (v12.2 Jellyfin.Api/Constants/InternalClaimTypes.cs: <c>UserId = "Jellyfin-UserId"</c>).
    /// </summary>
    public const string UserIdClaimType = "Jellyfin-UserId";

    // Filters.Add<T>() builds a new filter instance per request, so the log-once state must outlive the instance.
    private static readonly ConcurrentDictionary<Type, byte> _loggedFailureKinds = new();

    private readonly DisplayNameResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SearchHintsFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchHintsFilter"/> class.
    /// </summary>
    /// <param name="resolver">Decides the displayed names.</param>
    /// <param name="libraryManager">Looks up the item behind each hint.</param>
    /// <param name="logger">The logger.</param>
    public SearchHintsFilter(DisplayNameResolver resolver, ILibraryManager libraryManager, ILogger<SearchHintsFilter> logger)
    {
        _resolver = resolver;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the user the controller searched for, the way <c>RequestHelpers.GetUserId</c> does (v12.2
    /// Jellyfin.Api/Helpers/RequestHelpers.cs 67-85): the bound <c>userId</c> argument unless it is null or empty, otherwise the
    /// <see cref="UserIdClaimType"/> claim (type compared case-insensitively, as ClaimsPrincipalExtensions.GetClaimValue does).
    /// </summary>
    /// <remarks>
    /// The controller's admin check is not repeated: when it fails the action throws and there is no result to rewrite.
    /// A missing, empty or unparsable claim gives <see cref="Guid.Empty"/> (core would throw on an unparsable one; then there is no
    /// result either).
    /// </remarks>
    /// <param name="userIdArgument">The model-bound <c>userId</c> query argument, if any.</param>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The user id, or <see cref="Guid.Empty"/> when none can be resolved.</returns>
    public static Guid ResolveUserId(Guid? userIdArgument, ClaimsPrincipal principal)
    {
        if (userIdArgument is { } argument && !argument.Equals(Guid.Empty))
        {
            return argument;
        }

        var claim = principal?.Claims.FirstOrDefault(c => c.Type.Equals(UserIdClaimType, StringComparison.OrdinalIgnoreCase))?.Value;
        return Guid.TryParse(claim, out var claimed) ? claimed : Guid.Empty;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Read before the action runs: the bound value exactly as the controller receives it (absent when the query has no userId).
        context.ActionArguments.TryGetValue("userId", out var userIdArgument);

        var executed = await next().ConfigureAwait(false);

        if (executed.Result is not ObjectResult { Value: SearchHintResult result })
        {
            return;
        }

        Guid userId;
        try
        {
            userId = ResolveUserId(userIdArgument as Guid?, context.HttpContext.User);
        }
#pragma warning disable CA1031 // Fail open: never break search.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailure(ex, Guid.Empty);
            return;
        }

        if (userId.Equals(Guid.Empty) || result.SearchHints is null)
        {
            return;
        }

        foreach (var hint in result.SearchHints)
        {
            Rewrite(hint, userId);
        }
    }

    private void Rewrite(SearchHint? hint, Guid userId)
    {
        // Only kinds the resolver can change; skips the lookup for people, genres, studios, music, ...
        if (hint is null || hint.Type is not (BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Episode or BaseItemKind.Season))
        {
            return;
        }

        try
        {
            var item = _libraryManager.GetItemById(hint.Id);
            if (item is null)
            {
                return;
            }

            var (name, seriesName) = _resolver.Resolve(item, userId);
            if (name is not null)
            {
                hint.Name = name;
            }

            if (seriesName is not null)
            {
                hint.Series = seriesName;
            }
        }
#pragma warning disable CA1031 // Fail open: never break search.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailure(ex, hint.Id);
        }
    }

    private void LogFailure(Exception ex, Guid itemId)
    {
        if (_loggedFailureKinds.TryAdd(ex.GetType(), 0))
        {
            _logger.LogWarning(
                ex,
                "Original Titles: could not decide the search hint name for item {ItemId}; it is shown unchanged. Further {ExceptionType} failures are logged at Debug level only",
                itemId,
                ex.GetType().Name);
        }
        else
        {
            _logger.LogDebug(ex, "Original Titles: search hint name decision failed for item {ItemId}; shown unchanged", itemId);
        }
    }
}
