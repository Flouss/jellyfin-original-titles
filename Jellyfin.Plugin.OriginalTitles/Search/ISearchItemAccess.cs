using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// The provider's seam to the server's per-user item access and ancestry (defence in depth for romanised hits).
/// </summary>
public interface ISearchItemAccess
{
    /// <summary>
    /// Gets the live item when <paramref name="userId"/> may see it; null for an unknown user, a missing item or an item the user
    /// cannot access (hidden library, parental rating, blocked/allowed tags). Must never return an item for an unknown user.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">The requesting user id.</param>
    /// <returns>The item, or null.</returns>
    BaseItem? GetVisibleItem(Guid itemId, Guid userId);

    /// <summary>
    /// Gets whether <paramref name="item"/> lies in the subtree of <paramref name="ancestorId"/> (direct child or any deeper level),
    /// with the same meaning as the SQL provider's ParentId filter (<c>ParentId == id</c> or the AncestorIds table, which the server
    /// fills from <c>BaseItem.GetAncestorIds()</c>).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="ancestorId">The requested parent id.</param>
    /// <returns>True when the item is below the ancestor.</returns>
    bool IsUnder(BaseItem item, Guid ancestorId);
}
