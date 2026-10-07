using System;
using System.Collections.Generic;
using Jellyfin.Plugin.OriginalTitles.Library;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

public class LibraryItemEventsTests
{
    [Fact]
    public void SubscribesOnce_RelaysAllThreeEvents_AndUnsubscribesOnDispose()
    {
        var (manager, library) = SearchLibraryManager.Create();
        var events = new LibraryItemEvents(manager, NullLogger<LibraryItemEvents>.Instance);
        Assert.Equal(3, library.HandlerCount); // one handler per core event, however many consumers attach

        var seen = new List<string>();
        events.ItemAdded += (_, _) => seen.Add("added");
        events.ItemUpdated += (_, _) => seen.Add("updated");
        events.ItemRemoved += (_, _) => seen.Add("removed");
        Assert.Equal(3, library.HandlerCount);

        var movie = new Movie { Id = Guid.NewGuid() };
        library.RaiseAdded(movie);
        library.RaiseUpdated(movie);
        library.RaiseRemoved(movie);
        Assert.Equal(new[] { "added", "updated", "removed" }, seen);

        events.Dispose();
        Assert.Equal(0, library.HandlerCount);
    }

    [Fact]
    public void AThrowingConsumer_DoesNotStopTheNextOne()
    {
        var (manager, library) = SearchLibraryManager.Create();
        var events = new LibraryItemEvents(manager, NullLogger<LibraryItemEvents>.Instance);
        var reached = false;
        events.ItemUpdated += (_, _) => throw new InvalidOperationException("consumer bug");
        events.ItemUpdated += (_, _) => reached = true;

        library.RaiseUpdated(new Movie { Id = Guid.NewGuid() }); // must not throw into core either

        Assert.True(reached);
    }
}
