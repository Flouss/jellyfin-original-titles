using System;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.OriginalTitles.Display;

/// <summary>
/// Decides which name a user should see for an item. Pure: reads the item, never writes it.
/// </summary>
/// <remarks>
/// A replacement is produced only when the (series') OriginalTitle is non-blank, differs from its Name, and its original language is
/// enabled for the user. Movie/Series: the replacement is for <c>Name</c>. Episode/Season: their own Name is never touched; the
/// replacement is for <c>SeriesName</c>, decided entirely from the series (title AND language; the episode's own language is
/// ignored). Every other item kind is untouched.
/// Tripwire: <c>Episode.Series</c> / <c>Season.Series</c> resolve through the static <c>BaseItem.LibraryManager</c> and throw
/// (NullReferenceException) when it is unset but a SeriesId exists. This class does not catch that; the DTO wrapper's fail-open
/// handler (Task 3) is the safety net. Not covered: BoxSet/Folder/Audio and every other kind are deliberately never rewritten.
/// </remarks>
public class DisplayNameResolver
{
    private readonly IUserLanguageSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="DisplayNameResolver"/> class.
    /// </summary>
    /// <param name="settings">Per-user enabled languages.</param>
    public DisplayNameResolver(IUserLanguageSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Decides the replacement names for <paramref name="item"/> as seen by <paramref name="userId"/>.
    /// </summary>
    /// <param name="item">The item about to be shown.</param>
    /// <param name="userId">The requesting user.</param>
    /// <returns>
    /// <c>Name</c>: non-null only for a <see cref="Movie"/>/<see cref="Series"/> that qualifies.
    /// <c>SeriesName</c>: non-null only for an <see cref="Episode"/>/<see cref="Season"/> whose series qualifies.
    /// A null member means "leave the real value".
    /// </returns>
    public (string? Name, string? SeriesName) Resolve(BaseItem item, Guid userId)
    {
        switch (item)
        {
            case Movie or Series:
                return (Qualifying(item, userId), null);
            case Episode episode:
                return (null, Qualifying(episode.Series, userId));
            case Season season:
                return (null, Qualifying(season.Series, userId));
            default:
                return (null, null);
        }
    }

    private string? Qualifying(BaseItem? titled, Guid userId)
    {
        if (titled is null)
        {
            return null;
        }

        var originalTitle = titled.OriginalTitle;
        if (string.IsNullOrWhiteSpace(originalTitle))
        {
            return null;
        }

        if (string.Equals(originalTitle, titled.Name, StringComparison.Ordinal))
        {
            return null;
        }

        return _settings.IsEnabled(userId, titled.GetInheritedOriginalLanguage()) ? originalTitle : null;
    }
}
