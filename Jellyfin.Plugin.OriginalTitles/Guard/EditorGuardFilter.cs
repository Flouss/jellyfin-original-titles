using System;
using System.Linq;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalTitles.Guard;

/// <summary>
/// Editor guard: a global MVC action filter that stops a metadata-editor save of a DISPLAYED item from persisting the original title.
/// </summary>
/// <remarks>
/// <para>Why (v12.2 <c>Jellyfin.Api/Controllers/ItemUpdateController.cs</c>, reproduced on the harness): <c>UpdateItem</c> copies the posted
/// <see cref="BaseItemDto"/> onto the item (~241 <c>item.Name = request.Name</c>; ~286-288 a non-null <c>request.SeriesName</c> on an
/// episode/season), and a series' Name cascades into every season/episode <c>SeriesName</c> (~316-357). The editor shows the wrapper's
/// output, so saving it unchanged would store the original title.</para>
/// <para>How: BEFORE the action runs, the stored item still holds the real values. For <c>ItemUpdate.UpdateItem</c> only, the filter asks
/// <see cref="DisplayNameResolver"/> what each configured user is shown; when <c>request.Name</c> (Movie/Series) or
/// <c>request.SeriesName</c> (Episode/Season) equals such a displayed value, it is replaced in the request by the stored value. The
/// controller's own single write then keeps the real name, and the series cascade propagates the real name. The filter itself NEVER
/// writes (no repository call, so no <c>ItemUpdated</c> re-entry and no loop is possible); the only effect is on the bound request.</para>
/// <para>Invariants: other actions are not even looked up; fail open (any exception leaves the request as posted, logged at Warning);
/// the requesting user plays no part ("shown to at least one configured user"), because API-key requests carry no user.</para>
/// <para>Tripwire: the guard depends on the v12.2 argument names <c>itemId</c> (Guid) and <c>request</c> (BaseItemDto). If the matched
/// action arrives without them (an API change), it logs an Error naming them on every save instead of silently doing nothing. It logs
/// rather than throws: throwing would make every metadata save fail. Not covered by the tripwire: a renamed controller or action (the
/// filter then never matches and stays silent; the harness check in the README catches that).</para>
/// <para>Does NOT cover: edits made while the plugin is disabled or uninstalled; writers that bypass <c>POST /Items/{itemId}</c> (direct DB
/// edits, scripts or plugins persisting items themselves, metadata refreshes); an item already stored swapped (stored Name equals the
/// original: nothing is displayed differently, nothing to restore); an item whose real name equals its original title; and a FALSE
/// POSITIVE: an admin who deliberately sets Name (or SeriesName) to the original title while any configured user has that language
/// enabled is reverted, since the request cannot be told apart from the accidental save.</para>
/// </remarks>
public class EditorGuardFilter : IActionFilter
{
    /// <summary>The controller name of <c>Jellyfin.Api.Controllers.ItemUpdateController</c>.</summary>
    public const string GuardedControllerName = "ItemUpdate";

    /// <summary>The guarded action, <c>POST /Items/{itemId}</c>.</summary>
    public const string GuardedActionName = "UpdateItem";

    /// <summary>The action's route argument (v12.2 line 75: <c>[FromRoute, Required] Guid itemId</c>).</summary>
    public const string ItemIdArgument = "itemId";

    /// <summary>The action's body argument (v12.2 line 75: <c>[FromBody, Required] BaseItemDto request</c>).</summary>
    public const string RequestArgument = "request";

    private readonly DisplayNameResolver _resolver;
    private readonly IUserLanguageSettings _settings;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<EditorGuardFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EditorGuardFilter"/> class.
    /// </summary>
    /// <param name="resolver">The display decision the wrapper uses.</param>
    /// <param name="settings">Per-user enabled languages.</param>
    /// <param name="libraryManager">Reads the stored item.</param>
    /// <param name="logger">The logger.</param>
    public EditorGuardFilter(DisplayNameResolver resolver, IUserLanguageSettings settings, ILibraryManager libraryManager, ILogger<EditorGuardFilter> logger)
    {
        _resolver = resolver;
        _settings = settings;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: GuardedControllerName, ActionName: GuardedActionName })
        {
            return;
        }

        var arguments = context.ActionArguments;
        var hasItemId = arguments.TryGetValue(ItemIdArgument, out var itemIdValue);
        var hasRequest = arguments.TryGetValue(RequestArgument, out var requestValue);
        if (hasItemId && hasRequest && requestValue is null)
        {
            // Nothing posted: the controller rejects the call itself.
            return;
        }

        if (itemIdValue is not Guid itemId || requestValue is not BaseItemDto request)
        {
            _logger.LogError(
                "Original Titles: the editor guard expected the {Action} arguments '{ItemIdArgument}' (Guid) and '{RequestArgument}' (BaseItemDto) but got [{Arguments}]. This save is NOT guarded and may store a displayed original title as the real name: the Jellyfin API changed, update the plugin",
                GuardedControllerName + "." + GuardedActionName,
                ItemIdArgument,
                RequestArgument,
                string.Join(", ", arguments.Select(a => a.Key + ":" + (a.Value?.GetType().Name ?? "null"))));
            return;
        }

        try
        {
            Guard(itemId, request);
        }
#pragma warning disable CA1031 // Fail open: never break a metadata save.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Original Titles: the editor guard could not check item {ItemId}; the save goes through as posted", itemId);
        }
    }

    /// <inheritdoc />
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    private void Guard(Guid itemId, BaseItemDto request)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return;
        }

        foreach (var userId in _settings.GetConfiguredUserIds())
        {
            var (displayedName, displayedSeriesName) = _resolver.Resolve(item, userId);

            if (displayedName is not null && string.Equals(request.Name, displayedName, StringComparison.Ordinal))
            {
                request.Name = item.Name;
                LogKept(nameof(BaseItemDto.Name), item.Name, itemId);
            }

            if (displayedSeriesName is not null
                && item is IHasSeries hasSeries
                && string.Equals(request.SeriesName, displayedSeriesName, StringComparison.Ordinal))
            {
                request.SeriesName = hasSeries.SeriesName;
                LogKept(nameof(BaseItemDto.SeriesName), hasSeries.SeriesName, itemId);
            }
        }
    }

    private void LogKept(string field, string? stored, Guid itemId)
        => _logger.LogInformation(
            "Original Titles: editor guard kept the stored {Field} {Stored} of item {ItemId}; the save carried the original title this plugin displays",
            field,
            stored,
            itemId);
}
