using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OriginalTitles.Configuration;

/// <summary>
/// Plugin configuration. Serialised with XmlSerializer: never add Dictionary properties, use lists of entries.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the per-user enabled original-language lists. Empty (the default) means the plugin does nothing.
    /// </summary>
    public List<UserLanguageEntry> Users { get; set; } = new();
}
