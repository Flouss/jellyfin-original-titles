using System;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// Production <see cref="ISearchItemAccess"/>: the server's own single-item access check.
/// </summary>
/// <remarks>
/// <c>ILibraryManager.GetItemById&lt;T&gt;(Guid, User?)</c> (v12.2 LibraryManager ~1733) returns the item only when
/// <c>ItemIsVisible</c> holds, i.e. <c>item.IsVisibleStandalone(user)</c>: parental rating and tags on the item and every parent, plus
/// library access (EnabledFolders / BlockedMediaFolders / EnableAllFolders) through the item's collection folders (BaseItem ~1541).
/// It does NOT check a user for null: with a null user it returns any item, so this class refuses an unknown user itself.
/// Deliberately not used: <c>GetItemList(new InternalItemsQuery(user) { ItemIds = ... })</c>, because <c>AddUserToQuery</c> skips the
/// library (TopParentIds) restriction whenever ItemIds is set (LibraryManager ~2097, and SearchManager's own comment).
/// </remarks>
public class LibrarySearchItemAccess : ISearchItemAccess
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySearchItemAccess"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    public LibrarySearchItemAccess(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public BaseItem? GetVisibleItem(Guid itemId, Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        return _libraryManager.GetItemById<BaseItem>(itemId, user);
    }

    /// <inheritdoc />
    public bool IsUnder(BaseItem item, Guid ancestorId)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ParentId == ancestorId || item.GetAncestorIds().Contains(ancestorId);
    }
}
