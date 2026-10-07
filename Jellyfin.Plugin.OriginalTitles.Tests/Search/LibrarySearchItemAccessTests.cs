using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OriginalTitles.Search;
using Jellyfin.Plugin.OriginalTitles.Tests.Display;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

/// <summary>
/// The production access seam. Only the wiring is testable here: whether the server's <c>IsVisibleStandalone</c> really hides a library
/// is the server's code and is proven in the live harness.
/// </summary>
[Collection(BaseItemLibraryManagerCollection.Name)]
public sealed class LibrarySearchItemAccessTests : IDisposable
{
    private static readonly Guid KnownUserId = new("11111111-0000-0000-0000-000000000001");

    public void Dispose() => BaseItem.LibraryManager = null;

    [Fact]
    public void UnknownUser_GetsNull_EvenThoughTheLibraryWouldReturnTheItem()
    {
        // ILibraryManager.GetItemById<T>(id, null) skips the visibility check entirely, so the seam must refuse unknown users itself.
        var (access, library, users) = Create();
        var movie = new Movie { Id = Guid.NewGuid() };
        library.Items[movie.Id] = movie;
        users.Users[KnownUserId] = new User("known", "auth", "reset") { Id = KnownUserId };

        Assert.Null(access.GetVisibleItem(movie.Id, Guid.NewGuid()));
        Assert.Null(access.GetVisibleItem(movie.Id, Guid.Empty));
        Assert.Empty(library.UsersSeen); // never asked the library without a user

        // presence companion: a known user is passed to the server's access-checking overload
        Assert.Same(movie, access.GetVisibleItem(movie.Id, KnownUserId));
        Assert.Equal(KnownUserId, Assert.Single(library.UsersSeen)!.Id);
    }

    [Fact]
    public void IsUnder_DirectParentAndDeeperAncestor_ButNotASibling()
    {
        // library L > folder F > movie M; another folder G under L
        var (access, library, _) = Create();
        var lib = new Folder { Id = Guid.NewGuid() };
        var folder = new Folder { Id = Guid.NewGuid(), ParentId = lib.Id };
        var other = new Folder { Id = Guid.NewGuid(), ParentId = lib.Id };
        var movie = new Movie { Id = Guid.NewGuid(), ParentId = folder.Id };
        library.Items[lib.Id] = lib;
        library.Items[folder.Id] = folder;
        library.Items[other.Id] = other;
        BaseItem.LibraryManager = (MediaBrowser.Controller.Library.ILibraryManager)(object)library;

        Assert.True(access.IsUnder(movie, folder.Id));
        Assert.True(access.IsUnder(movie, lib.Id));
        Assert.False(access.IsUnder(movie, other.Id));
    }

    private static (LibrarySearchItemAccess Access, SearchLibraryManager Library, SearchUserManager Users) Create()
    {
        var (libraryManager, library) = SearchLibraryManager.Create();
        var (userManager, users) = SearchUserManager.Create();
        return (new LibrarySearchItemAccess(libraryManager, userManager), library, users);
    }
}
