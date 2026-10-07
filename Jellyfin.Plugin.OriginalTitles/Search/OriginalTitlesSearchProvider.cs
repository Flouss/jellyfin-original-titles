using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// External search provider that finds Movies/Series by a romanised (Arabizi / Latin) spelling of their original title.
/// Discovered by the server's type scan (<c>GetExports&lt;ISearchProvider&gt;()</c>, ApplicationHost v12.2 ~748) and constructed with
/// <c>ActivatorUtilities</c>; its dependencies come from <see cref="SearchServiceCollectionExtensions.AddOriginalTitlesSearch"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why external, and why it returns SQL's matches too (ruling R1, verified in SearchManager v12.2): the manager runs external and
/// internal providers concurrently, then returns the external results whenever they are non-empty and DISCARDS the internal (SQL)
/// ones. So whenever this provider has a romanised hit it also returns the results of every <see cref="IInternalSearchProvider"/>
/// found through <see cref="ISearchManager.GetProviders"/> (today: the SQL provider, which matches CleanName, OriginalTitle and the
/// transliterated SortName and applies types, media types, parent, and user access itself). When it has no romanised hit it returns
/// nothing, and the manager behaves exactly as without the plugin (Meilisearch results, else SQL). The result is a superset of the
/// plugin-less results under every Meilisearch option. Cost: on a romanised hit the SQL query runs twice (ours, and the manager's
/// discarded fallback).
/// </para>
/// <para>
/// Users: a user with no settings entry (no enabled language) is declined by <see cref="CanSearch"/>, so a query from them costs no
/// scan and no transliteration; a user with an entry but an empty list still scans (the language gate then matches nothing).
/// </para>
/// <para>
/// Access, three layers: (1) SearchManager post-filters EVERY external result through the user's access filter when the query has a
/// UserId (authoritative; it never runs without a UserId); (2) delegated SQL results are access-filtered by the SQL provider itself;
/// (3) defence in depth here: a romanised hit is returned only if <see cref="ISearchItemAccess.GetVisibleItem"/> (server's
/// <c>IsVisibleStandalone</c>) returns it for the requesting user. Without a UserId this provider returns nothing.
/// Romanised hits are also gated by the requesting user's enabled languages (no languages = the plugin does nothing for that user).
/// </para>
/// <para>
/// Fail open = "as if the plugin were absent": any failure yields NO results (never a romanised-only list, which would make the
/// manager drop every plain SQL match) and is logged once at warning level, later ones at debug.
/// </para>
/// Priority: 50. The manager runs all applicable providers concurrently and merges by best score per item, so priority only orders
/// the provider list (logs, <c>GetProviders</c>); 50 sits between Meilisearch (10) and the SQL provider (100).
/// Score: romanised hits score 90, below SQL's exact CleanName match (100) and above its prefix tiers (80/75) and contains (50);
/// Meilisearch scores 0-100.
/// </remarks>
public class OriginalTitlesSearchProvider : IExternalSearchProvider
{
    /// <summary>The score of a romanised hit.</summary>
    public const float RomanisedMatchScore = 90f;

    /// <summary>The manager's own default when the query has no limit (SearchManager v12.2 CollectFromProvidersAsync).</summary>
    private const int DefaultLimit = 100;

    private readonly ISearchManager _searchManager;
    private readonly TitleIndex _index;
    private readonly IUserLanguageSettings _settings;
    private readonly ISearchItemAccess _access;
    private readonly ILogger<OriginalTitlesSearchProvider> _logger;
    private int _failuresLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="OriginalTitlesSearchProvider"/> class and starts the index build in the background.
    /// </summary>
    /// <param name="searchManager">The search manager (only <see cref="ISearchManager.GetProviders"/> is used, at search time).</param>
    /// <param name="index">The romanisation index.</param>
    /// <param name="settings">Per-user enabled languages.</param>
    /// <param name="access">Per-user item access.</param>
    /// <param name="logger">The logger.</param>
    public OriginalTitlesSearchProvider(
        ISearchManager searchManager,
        TitleIndex index,
        IUserLanguageSettings settings,
        ISearchItemAccess access,
        ILogger<OriginalTitlesSearchProvider> logger)
    {
        _searchManager = searchManager;
        _index = index;
        _settings = settings;
        _access = access;
        _logger = logger;
        _index.BuildInBackground();
    }

    /// <inheritdoc />
    public string Name => "Original Titles (romanised)";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    /// <remarks>
    /// Never throws: the manager calls this OUTSIDE any try/catch (SearchManager v12.2 :273 filters the provider list before its
    /// per-provider try at :306), so a throw would fail every search for every user. A failure declines (false) and is logged once at
    /// warning level, later ones at debug.
    /// </remarks>
    public bool CanSearch(SearchProviderQuery query)
    {
        try
        {
            return query?.UserId is { } userId && userId != Guid.Empty && _settings.GetConfiguredUserIds().Contains(userId);
        }
#pragma warning disable CA1031 // Fail open: CanSearch runs outside the manager's per-provider try; a throw would break every search.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailure(ex, "Original Titles could not decide whether to search; declining");
            return false;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var result in await SearchSafeAsync(query, cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    /// <inheritdoc />
    Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
        => SearchSafeAsync(query, cancellationToken);

    private static bool TypeAllowed(BaseItemKind kind, SearchProviderQuery query)
    {
        // SearchProviderQuery docs: a non-empty include list is authoritative; excludes apply only without includes.
        if (query.IncludeItemTypes.Length > 0)
        {
            return query.IncludeItemTypes.Contains(kind);
        }

        return !query.ExcludeItemTypes.Contains(kind);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchSafeAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await SearchCoreAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "Original Titles search failed; returning no results");
            return Array.Empty<SearchResult>();
        }
    }

    private void LogFailure(Exception ex, string what)
    {
        if (Interlocked.Increment(ref _failuresLogged) == 1)
        {
            _logger.LogWarning(ex, "{What} (later failures are logged at debug level)", what);
        }
        else
        {
            _logger.LogDebug(ex, "{What} (again)", what);
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchCoreAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        if (!CanSearch(query))
        {
            return Array.Empty<SearchResult>();
        }

        var userId = query.UserId!.Value;
        var romanised = RomanisedHits(query, userId);
        if (romanised.Count == 0)
        {
            // Silent: the manager then uses Meilisearch's or its own SQL results exactly as without this plugin.
            return Array.Empty<SearchResult>();
        }

        var best = new Dictionary<Guid, float>();
        foreach (var id in romanised)
        {
            best[id] = RomanisedMatchScore;
        }

        foreach (var provider in _searchManager.GetProviders().OfType<IInternalSearchProvider>())
        {
            if (!provider.CanSearch(query))
            {
                continue;
            }

            // A throw here propagates to SearchSafeAsync: returning the romanised hits alone would hide every plain match.
            foreach (var result in await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false))
            {
                if (!best.TryGetValue(result.ItemId, out var score) || result.Score > score)
                {
                    best[result.ItemId] = result.Score;
                }
            }
        }

        // Best first: the manager stops reading an external provider's stream at its limit.
        return best
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(query.Limit ?? DefaultLimit)
            .Select(kv => new SearchResult(kv.Key, kv.Value))
            .ToList();
    }

    private List<Guid> RomanisedHits(SearchProviderQuery query, Guid userId)
    {
        var hits = new List<Guid>();

        // Before the query is transliterated: the build's ICU warm-up must come first, or the very first query would run
        // transliteration ahead of it (EnsureBuilt is a flag check once built).
        _index.EnsureBuilt();
        var queryKeys = RomanisationKey.TransliteratedKeysOf(query.SearchTerm);
        if (queryKeys.Count == 0)
        {
            return hits;
        }

        foreach (var entry in _index.Match(queryKeys, language => _settings.IsEnabled(userId, language)))
        {
            if (!TypeAllowed(entry.Kind, query))
            {
                continue;
            }

            // Defence in depth: the live item only if the server says this user may see it.
            var item = _access.GetVisibleItem(entry.ItemId, userId);
            if (item is null)
            {
                continue;
            }

            if (query.MediaTypes.Length > 0 && !query.MediaTypes.Contains(item.MediaType))
            {
                continue;
            }

            if (query.ParentId is { } parentId && parentId != Guid.Empty && !_access.IsUnder(item, parentId))
            {
                continue;
            }

            hits.Add(entry.ItemId);
        }

        return hits;
    }
}
