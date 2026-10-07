using Jellyfin.Plugin.OriginalTitles.Library;
using Jellyfin.Plugin.OriginalTitles.Search;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Search;

/// <summary>
/// The server creates the provider by type scan with <c>ActivatorUtilities.CreateInstance(ServiceProvider, type)</c>
/// (ApplicationHost.cs v12.2 ~335, ~748); any unresolvable constructor parameter makes <c>CreateInstanceSafe</c> fail the WHOLE plugin.
/// This proves core services plus <see cref="SearchServiceCollectionExtensions.AddOriginalTitlesSearch"/> are enough.
/// </summary>
public class SearchRegistrationTests
{
    [Fact]
    public void CoreServicesPlusTheExtension_CanCreateTheProviderTheWayTheTypeScanDoes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(SearchLibraryManager.Create().Manager);
        services.AddSingleton(SearchUserManager.Create().Manager);
        services.AddSingleton<ISearchManager>(new FakeSearchManager());

        services.AddOriginalTitlesSearch();
        services.AddOriginalTitlesSearch(); // a second call (e.g. from Task 5) must not duplicate the shared event source

        using var provider = services.BuildServiceProvider();
        var created = ActivatorUtilities.CreateInstance<OriginalTitlesSearchProvider>(provider);

        Assert.NotNull(created);
        Assert.Single(services, d => d.ServiceType == typeof(LibraryItemEvents));
        Assert.Same(provider.GetRequiredService<LibraryItemEvents>(), provider.GetRequiredService<LibraryItemEvents>());
    }

    [Fact]
    public void WithoutTheExtension_TheTypeScanWouldFail()
    {
        // presence companion for the test above: the extension is what makes construction possible
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(SearchLibraryManager.Create().Manager);
        services.AddSingleton(SearchUserManager.Create().Manager);
        services.AddSingleton<ISearchManager>(new FakeSearchManager());

        using var provider = services.BuildServiceProvider();
        Assert.ThrowsAny<System.InvalidOperationException>(() => ActivatorUtilities.CreateInstance<OriginalTitlesSearchProvider>(provider));
    }
}
