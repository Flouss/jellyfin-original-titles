using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Plugin_IsAWebPagePlugin_WithEmbeddedConfigPage()
    {
        Assert.True(typeof(IHasWebPages).IsAssignableFrom(typeof(Plugin)));
        Assert.True(typeof(BasePlugin<Configuration.PluginConfiguration>).IsAssignableFrom(typeof(Plugin)));

        var resource = typeof(Plugin).Namespace + ".Configuration.configPage.html";
        Assert.Contains(resource, typeof(Plugin).Assembly.GetManifestResourceNames());
    }
}
