using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalTitles.Configuration;

/// <summary>
/// The original-language codes enabled for one Jellyfin user.
/// </summary>
public class UserLanguageEntry
{
    /// <summary>
    /// Gets or sets the Jellyfin user id.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the enabled language codes (stored as typed; compared after trim + lowercase).
    /// </summary>
    public List<string> Languages { get; set; } = new();
}
