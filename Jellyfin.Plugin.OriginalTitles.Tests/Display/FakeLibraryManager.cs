using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Display;

/// <summary>
/// xUnit collection for every test that sets the process-wide static <see cref="BaseItem.LibraryManager"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class BaseItemLibraryManagerCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "BaseItem.LibraryManager";
}

/// <summary>
/// Builds an <see cref="ILibraryManager"/> that answers only the non-generic <c>GetItemById(Guid)</c>
/// (the only member <c>Episode.Series</c> / <c>Season.Series</c> call, measured in docs/spike.md); every other member throws.
/// </summary>
public class FakeLibraryManager : DispatchProxy
{
    private readonly Dictionary<Guid, BaseItem> _items = new();

    /// <summary>Creates the fake backed by the given items.</summary>
    /// <param name="items">Items resolvable by id.</param>
    /// <returns>The proxy to assign to <see cref="BaseItem.LibraryManager"/>.</returns>
    public static ILibraryManager Create(params BaseItem[] items)
    {
        var proxy = Create<ILibraryManager, FakeLibraryManager>();
        var fake = (FakeLibraryManager)(object)proxy;
        foreach (var item in items)
        {
            fake._items[item.Id] = item;
        }

        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is { Name: "GetItemById", IsGenericMethod: false }
            && args is { Length: 1 } && args[0] is Guid id)
        {
            return _items.TryGetValue(id, out var item) ? item : null;
        }

        throw new NotSupportedException("FakeLibraryManager only answers GetItemById(Guid), got " + targetMethod?.Name);
    }
}
