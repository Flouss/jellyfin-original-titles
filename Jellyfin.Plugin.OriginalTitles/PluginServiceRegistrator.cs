using System;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
using Jellyfin.Plugin.OriginalTitles.Search;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalTitles;

/// <summary>
/// Service registrator for Original Titles plugin services.
/// </summary>
/// <remarks>
/// Relies on core registering <c>AddSingleton&lt;IDtoService, DtoService&gt;()</c> before plugin registrators run
/// (v12.2 ApplicationHost.cs ~490/492/606). The concrete DtoService is in Emby.Server.Implementations, which the plugin does not
/// reference, so the decorator builds the original from the existing descriptor at resolve time. Must never throw:
/// PluginManager.RegisterServices marks a throwing plugin Malfunctioned.
/// The container resolves the LAST registration of a service, so a plugin that registers <c>IDtoService</c> after this one wins
/// and the decorator is bypassed (this plugin wraps whatever is registered when it runs, not what comes later).
/// The decorator is registered as a factory descriptor (it builds the wrapped service itself), so DI no longer checks that part of the
/// graph for constructor cycles or missing dependencies at build time; a wrong wiring shows up when <c>IDtoService</c> is first resolved.
/// <c>OriginalTitleDtoService.LogFailure</c> is called from inside its catch blocks and is not itself guarded: a logger that throws
/// would escape (safe under Serilog, which never throws from a log call).
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Plugin.Instance is created after DI registration; Plugin.CurrentConfiguration is evaluated per call and never null. It is the
        // ONE provider (also used by AddOriginalTitlesSearch's fallback); never inline a second lambda here.
        serviceCollection.AddSingleton<IUserLanguageSettings>(_ => new UserLanguageSettings(Plugin.CurrentConfiguration));
        serviceCollection.AddSingleton<DisplayNameResolver>();
        DecorateDtoService(serviceCollection);
        serviceCollection.AddSingleton<Api.SearchHintsFilter>();
        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<Api.SearchHintsFilter>());

        // Editor guard: keeps the stored Name/SeriesName when a metadata save posts back the displayed original title. One singleton
        // like the hints filter: it holds no per-request state and depends only on singletons (ILibraryManager: v12.2 ApplicationHost ~577).
        serviceCollection.AddSingleton<Guard.EditorGuardFilter>();
        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<Guard.EditorGuardFilter>());

        // Search provider dependencies (library event relay, title index, ISearchProvider). Mandatory: without them the provider
        // cannot be constructed.
        serviceCollection.AddOriginalTitlesSearch();
    }

    /// <summary>
    /// Replaces the effective (last, non-keyed) <see cref="IDtoService"/> registration with <see cref="OriginalTitleDtoService"/>
    /// wrapping it, keeping its lifetime. Leaves the collection unchanged when there is nothing (recognisable) to wrap.
    /// </summary>
    private static void DecorateDtoService(IServiceCollection services)
    {
        var index = -1;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IDtoService) && !services[i].IsKeyedService)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        var core = services[index];
        Func<IServiceProvider, IDtoService>? createInner = core switch
        {
            { ImplementationInstance: IDtoService instance } => _ => instance,
            { ImplementationFactory: { } factory } => provider => (IDtoService)factory(provider),
            { ImplementationType: { } type } => provider => (IDtoService)ActivatorUtilities.CreateInstance(provider, type),
            _ => null
        };

        if (createInner is null)
        {
            return;
        }

        services[index] = ServiceDescriptor.Describe(
            typeof(IDtoService),
            provider => new OriginalTitleDtoService(
                createInner(provider),
                provider.GetRequiredService<DisplayNameResolver>(),
                provider.GetRequiredService<ILogger<OriginalTitleDtoService>>()),
            core.Lifetime);
    }
}
