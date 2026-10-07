using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// DI registrations the romanised search provider needs. Call once from <c>PluginServiceRegistrator.RegisterServices</c>:
/// <c>serviceCollection.AddOriginalTitlesSearch();</c>. Idempotent (TryAdd), so other features may call it too.
/// </summary>
/// <remarks>
/// The provider itself is NOT registered: the server finds it by type scan and builds it with <c>ActivatorUtilities</c>. Without these
/// registrations that construction fails and the server's <c>CreateInstanceSafe</c> disables the WHOLE plugin (ApplicationHost v12.2
/// ~340), so the registrator line is mandatory.
/// </remarks>
public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared library-event source, the title index, the access seam and (if nobody did yet) the language settings.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddOriginalTitlesSearch(this IServiceCollection services)
    {
        services.TryAddSingleton<LibraryItemEvents>();
        services.TryAddSingleton<TitleIndex>();
        services.TryAddSingleton<ISearchItemAccess, LibrarySearchItemAccess>();

        // Reads the live configuration on every call (the settings page replaces it on save).
        services.TryAddSingleton<IUserLanguageSettings>(
            _ => new UserLanguageSettings(() => Plugin.Instance?.Configuration ?? new PluginConfiguration()));
        return services;
    }
}
