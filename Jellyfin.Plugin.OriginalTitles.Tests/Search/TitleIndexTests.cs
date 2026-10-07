using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalTitles.Library;
using Jellyfin.Plugin.OriginalTitles.Search;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

/// <summary>
/// Hand-derived keys (RomanisationKeyTests pipeline; ICU output from docs/spike.md):
/// <c>الخطاب على الباب</c> -> <c>alkhtab ʿly albab</c> -> [xtb, l, b]; query <c>khottab al beb</c> -> [xtb, b].
/// <c>موش في ثنيتي</c> -> <c>mwsh fy thnyty</c> -> mwsh: sh->c, w (not first) dropped -> mc; fy -> f; thnyty: th->t, y dropped -> tnt
/// -> [mc, f, tnt]; query <c>mouch fi thniti</c> -> mouch: ch->c, drop o/u -> mc; fi -> f; thniti -> tniti -> tnt -> [mc, f, tnt].
/// </summary>
public class TitleIndexTests
{
    private const string Khattab = "الخطاب على الباب";
    private const string Mouch = "موش في ثنيتي";

    private static readonly string[] KhattabQuery = { "xtb", "b" };
    private static readonly string[] MouchQuery = { "mc", "f", "tnt" };

    private static readonly Guid IdA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid IdB = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid IdS = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid IdE = new("eeeeeeee-0000-0000-0000-000000000001");

    [Fact]
    public void Build_IndexesMoviesAndSeriesWithTitleAndLanguage_SkipsTheRest()
    {
        var (index, library) = Create();
        library.Snapshot.Add(Movie(IdA, Khattab, "ar"));
        library.Snapshot.Add(new Series { Id = IdS, Name = "Suitors", OriginalTitle = Khattab, OriginalLanguage = "ar" });
        library.Snapshot.Add(new Episode { Id = IdE, Name = "Ep", OriginalTitle = Khattab, OriginalLanguage = "ar" }); // wrong kind
        library.Snapshot.Add(Movie(Guid.NewGuid(), "   ", "ar")); // blank original title
        library.Snapshot.Add(Movie(Guid.NewGuid(), Khattab, " ")); // blank language: can never be enabled for anyone

        index.EnsureBuilt();

        Assert.Equal(2, index.Count); // A and S only
        var hits = index.Match(KhattabQuery, _ => true);
        Assert.Equal(new[] { (IdA, BaseItemKind.Movie), (IdS, BaseItemKind.Series) }, hits.Select(h => (h.ItemId, h.Kind)).OrderBy(h => h.ItemId));
        Assert.Equal(new[] { "xtb", "l", "b" }, hits.Single(h => h.ItemId == IdA).Keys);
    }

    [Fact]
    public void Match_AppliesTheLanguageGate_WithPresenceCompanion()
    {
        var (index, library) = Create();
        library.Snapshot.Add(Movie(IdA, Khattab, "ar"));
        index.EnsureBuilt();

        Assert.Empty(index.Match(KhattabQuery, lang => lang == "fr"));
        Assert.Equal(IdA, Assert.Single(index.Match(KhattabQuery, lang => lang == "ar")).ItemId);
    }

    [Fact]
    public void EnsureBuilt_ReadsTheLibraryOnce()
    {
        var (index, library) = Create();
        library.Snapshot.Add(Movie(IdA, Khattab, "ar"));

        index.EnsureBuilt();
        index.EnsureBuilt();

        Assert.Equal(1, library.ListCalls);
    }

    [Fact]
    public void Events_AddUpdateRemove_KeepTheIndexCurrent()
    {
        var (index, library) = Create();
        index.EnsureBuilt();
        Assert.Equal(0, index.Count);

        library.RaiseAdded(Movie(IdA, Khattab, "ar"));
        Assert.Equal(IdA, Assert.Single(index.Match(KhattabQuery, _ => true)).ItemId);

        // updated to another original title: the old keys no longer match, the new ones do
        library.RaiseUpdated(Movie(IdA, Mouch, "ar"));
        Assert.Empty(index.Match(KhattabQuery, _ => true));
        Assert.Equal(IdA, Assert.Single(index.Match(MouchQuery, _ => true)).ItemId);

        // updated to a blank original title: dropped
        library.RaiseUpdated(Movie(IdA, string.Empty, "ar"));
        Assert.Equal(0, index.Count);

        library.RaiseAdded(Movie(IdB, Mouch, "ar"));
        library.RaiseRemoved(Movie(IdB, Mouch, "ar"));
        Assert.Empty(index.Match(MouchQuery, _ => true));
    }

    [Fact]
    public void EventsDuringOrBeforeTheBuild_WinOverTheOlderSnapshot()
    {
        var (index, library) = Create();

        // snapshot still holds A (khattab) and B, but events already said: A now has the mouch title, B was removed
        library.Snapshot.Add(Movie(IdA, Khattab, "ar"));
        library.Snapshot.Add(Movie(IdB, Khattab, "ar"));
        library.RaiseUpdated(Movie(IdA, Mouch, "ar"));
        library.RaiseRemoved(Movie(IdB, Khattab, "ar"));

        index.EnsureBuilt();

        Assert.Empty(index.Match(KhattabQuery, _ => true)); // neither stale A nor removed B resurrected
        Assert.Equal(IdA, Assert.Single(index.Match(MouchQuery, _ => true)).ItemId);
    }

    [Fact]
    public void FailedBuild_IsRetriedOnTheNextCall()
    {
        var (index, library) = Create();
        library.Snapshot.Add(Movie(IdA, Khattab, "ar"));
        library.ThrowOnList = true;

        index.EnsureBuilt(); // logs, does not throw
        Assert.Equal(0, index.Count);

        library.ThrowOnList = false;
        index.EnsureBuilt();
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var (index, library) = Create();
        index.EnsureBuilt();
        index.Dispose();

        library.RaiseAdded(Movie(IdA, Khattab, "ar"));
        Assert.Equal(0, index.Count);
    }

    private static Movie Movie(Guid id, string originalTitle, string language) =>
        new() { Id = id, Name = "Translated", OriginalTitle = originalTitle, OriginalLanguage = language };

    private static (TitleIndex Index, SearchLibraryManager Library) Create()
    {
        var (manager, fake) = SearchLibraryManager.Create();
        var events = new LibraryItemEvents(manager, NullLogger<LibraryItemEvents>.Instance);
        return (new TitleIndex(manager, events, NullLogger<TitleIndex>.Instance), fake);
    }
}
