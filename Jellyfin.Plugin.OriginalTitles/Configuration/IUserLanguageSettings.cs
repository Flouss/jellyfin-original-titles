using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalTitles.Configuration;

/// <summary>
/// Answers whether a user has enabled original titles for a language.
/// </summary>
public interface IUserLanguageSettings
{
    /// <summary>
    /// Gets whether <paramref name="languageCode"/> (trimmed, case-insensitive) is enabled for <paramref name="userId"/>.
    /// A null, empty or whitespace code is never enabled. Never throws: an unreadable or malformed configuration enables nothing.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="languageCode">The item's original language code, e.g. "ar".</param>
    /// <returns>True when the code is in the user's enabled list.</returns>
    bool IsEnabled(Guid userId, string? languageCode);

    /// <summary>
    /// Gets the distinct ids of every user that has a settings entry (whatever its languages). Never throws: an unreadable or
    /// malformed configuration gives an empty list.
    /// </summary>
    /// <returns>The configured user ids, in configuration order.</returns>
    IReadOnlyList<Guid> GetConfiguredUserIds();
}
