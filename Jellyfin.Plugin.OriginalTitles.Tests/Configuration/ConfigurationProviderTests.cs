using System;
using System.Collections.Generic;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Search;
using Jellyfin.Plugin.OriginalTitles.Tests.Display;
using Jellyfin.Plugin.OriginalTitles.Tests.Search;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.UserSettings;

/// <summary>
/// Both <see cref="IUserLanguageSettings"/> registrations (the registrator's and the TryAdd fallback in AddOriginalTitlesSearch) must
/// read the plugin's single provider, <c>Plugin.CurrentConfiguration</c>. A registration with its own inline
/// <c>() =&gt; new PluginConfiguration()</c> passes every other test and silently disables the plugin for every user in production.
/// Static state (<c>Plugin.ConfigurationOverride</c>): runs in the static-state collection and resets in finally.
/// </summary>
[Collection(BaseItemLibraryManagerCollection.Name)]
public class ConfigurationProviderTests
{
    // Hand-derived: user U has "ar" enabled in the config the override returns; V has no entry; "fr" is not in U's list.
    private static readonly Guid U = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid V = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PluginConfiguration ConfigWithUserAr() => new()
    {
        Users = new List<UserLanguageEntry> { new() { UserId = U, Languages = new List<string> { "ar" } } }
    };

    private static ServiceCollection CoreStandIns()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(SearchLibraryManager.Create().Manager);
        services.AddSingleton(SearchUserManager.Create().Manager);
        services.AddSingleton<ISearchManager>(new FakeSearchManager());
        return services;
    }

    private static void AssertReadsTheOverride(IUserLanguageSettings settings)
    {
        // Presence: the override's entry is visible (U/ar enabled, U configured). Absence companions: another language and another
        // user are not, so "everything enabled" or "everything disabled" cannot satisfy the test.
        Assert.True(settings.IsEnabled(U, "ar"));
        Assert.False(settings.IsEnabled(U, "fr"));
        Assert.False(settings.IsEnabled(V, "ar"));
        Assert.Equal(new[] { U }, settings.GetConfiguredUserIds());
    }

    [Fact]
    public void RealRegistrator_RegistersSettingsThatReadThePluginsSingleProvider()
    {
        var services = CoreStandIns();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();

        Plugin.ConfigurationOverride = ConfigWithUserAr;
        try
        {
            AssertReadsTheOverride(provider.GetRequiredService<IUserLanguageSettings>());
        }
        finally
        {
            Plugin.ConfigurationOverride = null;
        }

        // Override removed again: back to the no-instance behaviour (nothing configured), proving the seam does not leak.
        Assert.Empty(provider.GetRequiredService<IUserLanguageSettings>().GetConfiguredUserIds());
    }

    [Fact]
    public void ExtensionAlone_RegistersSettingsThatReadThePluginsSingleProvider()
    {
        var services = CoreStandIns();
        services.AddOriginalTitlesSearch();
        using var provider = services.BuildServiceProvider();

        Plugin.ConfigurationOverride = ConfigWithUserAr;
        try
        {
            AssertReadsTheOverride(provider.GetRequiredService<IUserLanguageSettings>());
        }
        finally
        {
            Plugin.ConfigurationOverride = null;
        }

        Assert.Empty(provider.GetRequiredService<IUserLanguageSettings>().GetConfiguredUserIds());
    }

    [Fact]
    public void CurrentConfiguration_PrefersTheOverride_AndIsNeverNull()
    {
        Assert.NotNull(Plugin.CurrentConfiguration());
        Assert.Empty(Plugin.CurrentConfiguration().Users);

        var config = ConfigWithUserAr();
        Plugin.ConfigurationOverride = () => config;
        try
        {
            Assert.Same(config, Plugin.CurrentConfiguration());
        }
        finally
        {
            Plugin.ConfigurationOverride = null;
        }
    }
}
