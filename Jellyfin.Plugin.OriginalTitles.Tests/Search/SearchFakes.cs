using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OriginalTitles.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

/// <summary>
/// <see cref="ILibraryManager"/> fake for the search tests. Answers: the three item events (add/remove handler, plus Raise helpers),
/// <c>GetItemList(InternalItemsQuery)</c> (returns <see cref="Snapshot"/>, or throws while <see cref="ThrowOnList"/>),
/// non-generic <c>GetItemById(Guid)</c>, generic <c>GetItemById&lt;T&gt;(Guid, User)</c> (records the user, returns from
/// <see cref="Items"/>) and <c>GetCollectionFolders(BaseItem)</c> (empty). Every other member throws.
/// </summary>
public class SearchLibraryManager : DispatchProxy
{
    private EventHandler<ItemChangeEventArgs>? _added;
    private EventHandler<ItemChangeEventArgs>? _updated;
    private EventHandler<ItemChangeEventArgs>? _removed;

    /// <summary>Gets or sets what <c>GetItemList</c> returns (the startup snapshot).</summary>
    public List<BaseItem> Snapshot { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether <c>GetItemList</c> throws.</summary>
    public bool ThrowOnList { get; set; }

    /// <summary>Gets the items answered by GetItemById.</summary>
    public Dictionary<Guid, BaseItem> Items { get; } = new();

    /// <summary>Gets the users passed to the generic access-checking GetItemById.</summary>
    public List<User?> UsersSeen { get; } = new();

    /// <summary>Gets the number of GetItemList calls.</summary>
    public int ListCalls { get; private set; }

    /// <summary>Gets the number of currently attached handlers across the three events.</summary>
    public int HandlerCount =>
        (_added?.GetInvocationList().Length ?? 0) + (_updated?.GetInvocationList().Length ?? 0) + (_removed?.GetInvocationList().Length ?? 0);

    /// <summary>Creates the proxy and returns both views of it.</summary>
    /// <returns>The interface and the fake.</returns>
    public static (ILibraryManager Manager, SearchLibraryManager Fake) Create()
    {
        var proxy = Create<ILibraryManager, SearchLibraryManager>();
        return (proxy, (SearchLibraryManager)(object)proxy);
    }

    /// <summary>Raises ItemAdded.</summary>
    /// <param name="item">The item.</param>
    public void RaiseAdded(BaseItem item) => _added?.Invoke(this, new ItemChangeEventArgs { Item = item });

    /// <summary>Raises ItemUpdated.</summary>
    /// <param name="item">The item.</param>
    public void RaiseUpdated(BaseItem item) => _updated?.Invoke(this, new ItemChangeEventArgs { Item = item });

    /// <summary>Raises ItemRemoved.</summary>
    /// <param name="item">The item.</param>
    public void RaiseRemoved(BaseItem item) => _removed?.Invoke(this, new ItemChangeEventArgs { Item = item });

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name;
        switch (name)
        {
            case "add_ItemAdded": _added += (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
            case "remove_ItemAdded": _added -= (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
            case "add_ItemUpdated": _updated += (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
            case "remove_ItemUpdated": _updated -= (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
            case "add_ItemRemoved": _removed += (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
            case "remove_ItemRemoved": _removed -= (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
        }

        if (name == "GetItemList" && args is { Length: 1 } && args[0] is InternalItemsQuery)
        {
            ListCalls++;
            if (ThrowOnList)
            {
                throw new InvalidOperationException("library not ready (fake)");
            }

            return Snapshot.ToList();
        }

        if (name == "GetItemById" && targetMethod!.IsGenericMethod && args is { Length: 2 } && args[0] is Guid gid)
        {
            UsersSeen.Add(args[1] as User);
            return Items.TryGetValue(gid, out var found) ? found : null;
        }

        if (name == "GetItemById" && !targetMethod!.IsGenericMethod && args is { Length: 1 } && args[0] is Guid id)
        {
            return Items.TryGetValue(id, out var item) ? item : null;
        }

        if (name == "GetCollectionFolders" && args is { Length: 1 })
        {
            return new List<Folder>();
        }

        throw new NotSupportedException("SearchLibraryManager does not answer " + name);
    }
}

/// <summary><see cref="IUserManager"/> fake answering only <c>GetUserById(Guid)</c>.</summary>
public class SearchUserManager : DispatchProxy
{
    /// <summary>Gets the known users.</summary>
    public Dictionary<Guid, User> Users { get; } = new();

    /// <summary>Creates the proxy.</summary>
    /// <returns>The interface and the fake.</returns>
    public static (IUserManager Manager, SearchUserManager Fake) Create()
    {
        var proxy = Create<IUserManager, SearchUserManager>();
        return (proxy, (SearchUserManager)(object)proxy);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetUserById" && args is { Length: 1 } && args[0] is Guid id)
        {
            return Users.TryGetValue(id, out var user) ? user : null;
        }

        throw new NotSupportedException("SearchUserManager does not answer " + targetMethod?.Name);
    }
}

/// <summary>Search manager fake: exposes a provider list through <see cref="GetProviders"/>; nothing else is used.</summary>
public class FakeSearchManager : ISearchManager
{
    /// <summary>Gets the providers returned by <see cref="GetProviders"/>.</summary>
    public List<ISearchProvider> Providers { get; } = new();

    /// <inheritdoc />
    public Task<QueryResult<SearchHintInfo>> GetSearchHintsAsync(SearchQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> GetSearchResultsAsync(SearchProviderQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public void AddParts(IEnumerable<ISearchProvider> providers) => throw new NotSupportedException();

    /// <inheritdoc />
    public IReadOnlyList<ISearchProvider> GetProviders() => Providers;
}

/// <summary>Stands in for the server's SQL provider (an <see cref="IInternalSearchProvider"/>).</summary>
public class FakeInternalProvider : IInternalSearchProvider
{
    /// <summary>Gets the results returned on every call.</summary>
    public List<SearchResult> Results { get; } = new();

    /// <summary>Gets or sets a value indicating whether SearchAsync throws.</summary>
    public bool Throw { get; set; }

    /// <summary>Gets the queries received.</summary>
    public List<SearchProviderQuery> Queries { get; } = new();

    /// <inheritdoc />
    public string Name => "Fake Database";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query) => true;

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        if (Throw)
        {
            throw new InvalidOperationException("database down (fake)");
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(Results.ToList());
    }
}

/// <summary>
/// <see cref="ISearchItemAccess"/> fake: an item is visible to a user only when the pair was granted; ancestry from an explicit map.
/// </summary>
public class FakeItemAccess : ISearchItemAccess
{
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly HashSet<(Guid User, Guid Item)> _visible = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _ancestors = new();

    /// <summary>Gets or sets a value indicating whether GetVisibleItem throws.</summary>
    public bool Throw { get; set; }

    /// <summary>Gets the (user, item) pairs asked for.</summary>
    public List<(Guid User, Guid Item)> Asked { get; } = new();

    /// <summary>Makes <paramref name="item"/> visible to the given users and records its ancestors.</summary>
    /// <param name="item">The item.</param>
    /// <param name="ancestors">Ancestor ids (any depth).</param>
    /// <param name="users">Users who may see it.</param>
    public void Add(BaseItem item, Guid[] ancestors, params Guid[] users)
    {
        _items[item.Id] = item;
        _ancestors[item.Id] = new HashSet<Guid>(ancestors);
        foreach (var u in users)
        {
            _visible.Add((u, item.Id));
        }
    }

    /// <inheritdoc />
    public BaseItem? GetVisibleItem(Guid itemId, Guid userId)
    {
        Asked.Add((userId, itemId));
        if (Throw)
        {
            throw new InvalidOperationException("access check failed (fake)");
        }

        return _visible.Contains((userId, itemId)) && _items.TryGetValue(itemId, out var item) ? item : null;
    }

    /// <inheritdoc />
    public bool IsUnder(BaseItem item, Guid ancestorId)
        => _ancestors.TryGetValue(item.Id, out var set) && set.Contains(ancestorId);
}
