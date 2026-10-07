using System;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
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
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Plugin.Instance is created after DI registration; the provider is evaluated per call and never null.
        serviceCollection.AddSingleton<IUserLanguageSettings>(
            _ => new UserLanguageSettings(() => Plugin.Instance?.Configuration ?? new PluginConfiguration()));
        serviceCollection.AddSingleton<DisplayNameResolver>();
        DecorateDtoService(serviceCollection);

        // Later tasks register the search provider here.
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
