using System;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalTitles.Library;

/// <summary>
/// The plugin's ONE subscription to <see cref="ILibraryManager"/>'s <c>ItemAdded</c>/<c>ItemUpdated</c>/<c>ItemRemoved</c> events
/// (ruling R3). Plugin consumers (the search title index; later the editor guard) subscribe here instead of to the library manager,
/// so the plugin never holds duplicate core subscriptions. Register as a singleton (see
/// <c>Search.SearchServiceCollectionExtensions.AddOriginalTitlesSearch</c>).
/// </summary>
/// <remarks>
/// Each consumer handler runs in its own try/catch: core (LibraryManager v12.2) invokes the multicast delegate inside ONE try/catch,
/// so a throwing plugin handler would otherwise skip every handler subscribed after it, including other plugins' and core's own.
/// Not covered: handlers run synchronously on the core's thread; a slow consumer still slows library scans.
/// </remarks>
public sealed class LibraryItemEvents : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryItemEvents> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryItemEvents"/> class and subscribes to the library manager.
    /// </summary>
    /// <param name="libraryManager">The library manager whose events are relayed.</param>
    /// <param name="logger">The logger.</param>
    public LibraryItemEvents(ILibraryManager libraryManager, ILogger<LibraryItemEvents> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
    }

    /// <summary>Relayed <see cref="ILibraryManager.ItemAdded"/>.</summary>
    public event EventHandler<ItemChangeEventArgs>? ItemAdded;

    /// <summary>Relayed <see cref="ILibraryManager.ItemUpdated"/>.</summary>
    public event EventHandler<ItemChangeEventArgs>? ItemUpdated;

    /// <summary>Relayed <see cref="ILibraryManager.ItemRemoved"/>.</summary>
    public event EventHandler<ItemChangeEventArgs>? ItemRemoved;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemRemoved -= OnItemRemoved;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Relay(ItemAdded, sender, e, nameof(ItemAdded));

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => Relay(ItemUpdated, sender, e, nameof(ItemUpdated));

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => Relay(ItemRemoved, sender, e, nameof(ItemRemoved));

    private void Relay(EventHandler<ItemChangeEventArgs>? handlers, object? sender, ItemChangeEventArgs e, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ItemChangeEventArgs>)handler)(sender, e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Original Titles: a {EventName} consumer failed for item {ItemId}", eventName, e.Item?.Id);
            }
        }
    }
}
