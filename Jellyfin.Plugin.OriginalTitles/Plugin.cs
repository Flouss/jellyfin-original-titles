using System;
using System.Collections.Generic;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OriginalTitles;

/// <summary>
/// The main plugin class for Original Titles.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Original Titles";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7c1e5a3d-92b4-4f6e-8d0a-3b5f1c2e9a47");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets or sets a replacement for <see cref="Instance"/>'s configuration. Test seam only: production code never sets it, and a
    /// test that does must reset it in a <c>finally</c> (it is process-wide static state) and run inside the static-state xUnit collection.
    /// </summary>
    internal static Func<PluginConfiguration?>? ConfigurationOverride { get; set; }

    /// <summary>
    /// The ONE configuration provider of the plugin: every <see cref="IUserLanguageSettings"/> registration (the registrator's and the
    /// <c>AddOriginalTitlesSearch</c> TryAdd fallback) must use this method group and nothing else. Evaluated per call, never null:
    /// <see cref="Instance"/> is created after DI registration, and the settings page replaces the configuration on save.
    /// </summary>
    /// <remarks>
    /// Tripwire: a second, inline provider lambda that returns <c>new PluginConfiguration()</c> turns the plugin into a silent no-op for
    /// every user. Pinned by <c>ConfigurationProviderTests</c> (resolves <see cref="IUserLanguageSettings"/> through the real
    /// registrator and through the extension alone and checks it reads this method's result). Does NOT cover: a registration that
    /// bypasses both of those entry points, and the <c>Instance?.Configuration</c> production branch itself: <see cref="Instance"/> cannot
    /// be set in tests, so mutating that branch survives; the harness wrapper check is its only pin.
    /// </remarks>
    /// <returns>The live configuration, or an empty one when the plugin instance does not exist yet.</returns>
    internal static PluginConfiguration CurrentConfiguration()
        => ConfigurationOverride?.Invoke() ?? Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
