using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalTitles.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// In-memory romanisation index: for every Movie/Series with a non-blank <c>OriginalTitle</c> and <c>OriginalLanguage</c>, the keys
/// of its transliterated original title. Built once from the library (first <see cref="EnsureBuilt"/>), then kept current from the
/// shared <see cref="LibraryItemEvents"/>. Thread-safe: reads are lock-free over a concurrent dictionary, writes take one gate.
/// </summary>
/// <remarks>
/// Scope deviation (reported): the index holds items in EVERY language, not only languages enabled for some user. The language gate
/// is applied per requesting user at query time (<see cref="Match"/>), so an admin enabling a language takes effect without a rebuild.
/// ICU: keys come only from <see cref="RomanisationKey.TransliteratedKeysOf"/> (its lock serialises every transliteration).
/// Not covered: two concurrent events for the SAME item computed out of order can leave the older title until its next event; an
/// item whose type changes away from Movie/Series is dropped only if an event reports it.
/// </remarks>
public sealed class TitleIndex : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryItemEvents _events;
    private readonly ILogger<TitleIndex> _logger;
    private readonly ConcurrentDictionary<Guid, TitleIndexEntry> _entries = new();

    // Ids an event touched (upsert or removal) before the build merged its snapshot: the event is newer, so the build must not
    // overwrite or resurrect them. Guarded by _writeGate; cleared once the build has merged.
    private readonly HashSet<Guid> _touchedBeforeBuild = new();
    private readonly object _writeGate = new();
    private readonly object _buildGate = new();
    private volatile bool _built;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TitleIndex"/> class and starts listening to library events.
    /// </summary>
    /// <param name="libraryManager">Source of the startup snapshot.</param>
    /// <param name="events">The plugin's shared library-event source.</param>
    /// <param name="logger">The logger.</param>
    public TitleIndex(ILibraryManager libraryManager, LibraryItemEvents events, ILogger<TitleIndex> logger)
    {
        _libraryManager = libraryManager;
        _events = events;
        _logger = logger;
        _events.ItemAdded += OnItemChanged;
        _events.ItemUpdated += OnItemChanged;
        _events.ItemRemoved += OnItemRemoved;
    }

    /// <summary>Gets the number of indexed items.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Builds the index from the library once (single-threaded, under a lock; concurrent callers wait). A failed build is logged and
    /// retried on the next call. Never throws.
    /// </summary>
    public void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }

        lock (_buildGate)
        {
            if (_built)
            {
                return;
            }

            try
            {
                var snapshot = new List<TitleIndexEntry>();
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                    Recursive = true,
                    IsVirtualItem = false,
                };

                foreach (var item in _libraryManager.GetItemList(query))
                {
                    var entry = EntryFor(item);
                    if (entry is not null)
                    {
                        snapshot.Add(entry);
                    }
                }

                lock (_writeGate)
                {
                    foreach (var entry in snapshot)
                    {
                        if (!_touchedBeforeBuild.Contains(entry.ItemId))
                        {
                            _entries[entry.ItemId] = entry;
                        }
                    }

                    _touchedBeforeBuild.Clear();
                    _built = true;
                }

                _logger.LogInformation("Original Titles: search index built with {Count} items", _entries.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Original Titles: building the search index failed; retrying on the next search");
            }
        }
    }

    /// <summary>
    /// Starts <see cref="EnsureBuilt"/> on the thread pool so the first search does not pay for it. Never throws.
    /// </summary>
    public void BuildInBackground() => _ = Task.Run(EnsureBuilt);

    /// <summary>
    /// Gets the entries whose language passes <paramref name="languageAllowed"/> and whose keys match every query key
    /// (<see cref="TitleMatcher.IsMatch(IReadOnlyList{string}, IReadOnlyList{string})"/>).
    /// </summary>
    /// <param name="queryKeys">Keys of the query.</param>
    /// <param name="languageAllowed">Per-user language gate, called with the entry's raw language code.</param>
    /// <returns>The matching entries, in no particular order.</returns>
    public IReadOnlyList<TitleIndexEntry> Match(IReadOnlyList<string> queryKeys, Func<string, bool> languageAllowed)
    {
        ArgumentNullException.ThrowIfNull(queryKeys);
        ArgumentNullException.ThrowIfNull(languageAllowed);

        var hits = new List<TitleIndexEntry>();
        if (queryKeys.Count == 0)
        {
            return hits;
        }

        foreach (var entry in _entries.Values)
        {
            if (languageAllowed(entry.Language) && TitleMatcher.IsMatch(queryKeys, entry.Keys))
            {
                hits.Add(entry);
            }
        }

        return hits;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.ItemAdded -= OnItemChanged;
        _events.ItemUpdated -= OnItemChanged;
        _events.ItemRemoved -= OnItemRemoved;
    }

    private static TitleIndexEntry? EntryFor(BaseItem? item)
    {
        if (item is not (Movie or Series) || item.IsVirtualItem)
        {
            return null;
        }

        var language = item.OriginalLanguage;
        if (string.IsNullOrWhiteSpace(item.OriginalTitle) || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var keys = RomanisationKey.TransliteratedKeysOf(item.OriginalTitle);
        if (keys.Count == 0)
        {
            return null;
        }

        return new TitleIndexEntry(item.Id, item is Movie ? BaseItemKind.Movie : BaseItemKind.Series, language, keys);
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var item = e?.Item;
        if (item is null)
        {
            return;
        }

        // Kinds other than Movie/Series never enter the index; only a Movie/Series can need adding, replacing or dropping.
        if (item is not (Movie or Series))
        {
            return;
        }

        var entry = EntryFor(item); // transliterate outside the write gate
        lock (_writeGate)
        {
            if (entry is null)
            {
                _entries.TryRemove(item.Id, out _);
            }
            else
            {
                _entries[item.Id] = entry;
            }

            if (!_built)
            {
                _touchedBeforeBuild.Add(item.Id);
            }
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var item = e?.Item;
        if (item is null)
        {
            return;
        }

        lock (_writeGate)
        {
            _entries.TryRemove(item.Id, out _);
            if (!_built)
            {
                _touchedBeforeBuild.Add(item.Id);
            }
        }
    }
}
