using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Display;

/// <summary>
/// The registrator replaces core's IDtoService registration with the decorator wrapping it. The real DtoService lives in
/// Emby.Server.Implementations (not referenced), so a stand-in with its own constructor dependency plays "core" here,
/// registered the way v12.2 ApplicationHost.cs ~606 does: AddSingleton&lt;IDtoService, DtoService&gt;().
/// </summary>
public class PluginServiceRegistratorTests
{
    // Sentinel the stand-in returns from GetPrimaryImageAspectRatio, proving calls reach it through the decorator.
    private const double StandInAspectRatio = 1.25;

    // Sentinel of a later-registered stand-in (the last IDtoService registration wins in the container).
    private const double LaterStandInAspectRatio = 0.5;

    private static ServiceCollection CoreServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<CoreDependency>();
        services.AddSingleton<IDtoService, StandInCoreDtoService>();
        services.AddSingleton<DtoConsumer>();
        return services;
    }

    private static void Register(IServiceCollection services) => new PluginServiceRegistrator().RegisterServices(services, null!);

    [Fact]
    public void ResolvingIDtoService_YieldsTheDecorator_WrappingTheCoreRegistration()
    {
        var services = CoreServices();
        Register(services);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IDtoService>();

        Assert.IsType<OriginalTitleDtoService>(resolved);
        Assert.Equal(StandInAspectRatio, resolved.GetPrimaryImageAspectRatio(new Folder()));

        // The stand-in's constructor dependency came from the container (built once, shared with the container's singleton).
        var core = provider.GetRequiredService<CoreDependency>();
        Assert.Equal(1, core.StandInsBuilt);
    }

    [Fact]
    public void Decorator_IsASingleton_AndConsumersReceiveIt()
    {
        var services = CoreServices();
        Register(services);
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IDtoService>();
        var second = provider.GetRequiredService<IDtoService>();
        var consumer = provider.GetRequiredService<DtoConsumer>();

        Assert.Same(first, second);
        Assert.Same(first, consumer.DtoService);
        Assert.Equal(1, provider.GetRequiredService<CoreDependency>().StandInsBuilt);
        Assert.Single(services, d => d.ServiceType == typeof(IDtoService));
        Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(IDtoService)).Lifetime);
    }

    [Fact]
    public void Decorator_ResolvesWithNoPluginInstance_AndEnablesNothing()
    {
        // Plugin.Instance is null in unit tests, so nobody has a language enabled and the stand-in's DTO comes back untouched
        // ("Mouch Fi Thniti"); resolution must not throw. Two layers guard this (the registrator's null-safe provider and
        // UserLanguageSettings' catch): this test pins their combined behaviour -- removing only one layer does not fail it.
        var services = CoreServices();
        Register(services);
        using var provider = services.BuildServiceProvider();
        Assert.Null(Plugin.Instance);

        var settings = provider.GetRequiredService<IUserLanguageSettings>();
        Assert.False(settings.IsEnabled(Guid.NewGuid(), "ar"));

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Mouch Fi Thniti", OriginalTitle = "موش في ثنيتي", OriginalLanguage = "ar" };
        var user = new User("someone", "Default", "Default");
        Assert.Equal("Mouch Fi Thniti", provider.GetRequiredService<IDtoService>().GetBaseItemDto(movie, new DtoOptions(), user).Name);
        Assert.NotNull(provider.GetRequiredService<DisplayNameResolver>());
    }

    [Fact]
    public void FactoryAndInstanceCoreRegistrations_AreWrappedToo()
    {
        var byFactory = new ServiceCollection();
        byFactory.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        byFactory.AddSingleton<IDtoService>(_ => new StandInCoreDtoService(new CoreDependency()));
        Register(byFactory);
        using (var provider = byFactory.BuildServiceProvider())
        {
            var resolved = provider.GetRequiredService<IDtoService>();
            Assert.IsType<OriginalTitleDtoService>(resolved);
            Assert.Equal(StandInAspectRatio, resolved.GetPrimaryImageAspectRatio(new Folder()));
        }

        var byInstance = new ServiceCollection();
        byInstance.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        byInstance.AddSingleton<IDtoService>(new StandInCoreDtoService(new CoreDependency()));
        Register(byInstance);
        using (var provider = byInstance.BuildServiceProvider())
        {
            var resolved = provider.GetRequiredService<IDtoService>();
            Assert.IsType<OriginalTitleDtoService>(resolved);
            Assert.Equal(StandInAspectRatio, resolved.GetPrimaryImageAspectRatio(new Folder()));
        }
    }

    [Fact]
    public void WithTwoCoreRegistrations_TheLastEffectiveOneIsWrapped()
    {
        // The container resolves the LAST IDtoService registration, so that is the one to decorate. Two stand-ins answer different
        // aspect ratios: 1.25 (first, StandInCoreDtoService) and 0.5 (last, LaterStandInDtoService). Wrapping the first would
        // answer 1.25; wrapping the last answers 0.5. Presence companion: the first stand-in really is registered (and unused).
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<CoreDependency>();
        services.AddSingleton<IDtoService, StandInCoreDtoService>();
        services.AddSingleton<IDtoService, LaterStandInDtoService>();
        Register(services);
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IDtoService>();

        Assert.IsType<OriginalTitleDtoService>(resolved);
        Assert.Equal(LaterStandInAspectRatio, resolved.GetPrimaryImageAspectRatio(new Folder()));
        Assert.NotEqual(StandInAspectRatio, LaterStandInAspectRatio);
        Assert.Equal(0, provider.GetRequiredService<CoreDependency>().StandInsBuilt);
    }

    [Fact]
    public void NoCoreRegistration_AddsNoDtoService_AndDoesNotThrow()
    {
        // Fail open at registration time: nothing to wrap -> leave the collection without an IDtoService (core's job).
        // Presence companion: the plugin's own services are still registered.
        var services = new ServiceCollection();

        Register(services);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDtoService));
        Assert.Contains(services, d => d.ServiceType == typeof(IUserLanguageSettings));
    }

    /// <summary>A core-side dependency of the stand-in; counts how many stand-ins were built with it.</summary>
    public sealed class CoreDependency
    {
        public int StandInsBuilt { get; set; }
    }

    /// <summary>Plays core's DtoService: has a constructor dependency, answers a sentinel aspect ratio.</summary>
    public sealed class StandInCoreDtoService : IDtoService
    {
        public StandInCoreDtoService(CoreDependency dependency)
        {
            dependency.StandInsBuilt++;
        }

        public double? GetPrimaryImageAspectRatio(BaseItem item) => StandInAspectRatio;

        public BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User? user = null, BaseItem? owner = null)
            => new() { Id = item.Id, Name = item.Name };

        public IReadOnlyList<BaseItemDto> GetBaseItemDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User? user = null, BaseItem? owner = null, bool skipVisibilityCheck = false)
            => items.Select(i => new BaseItemDto { Id = i.Id, Name = i.Name }).ToArray();

        public BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem>? taggedItems, User? user = null)
            => new() { Id = item.Id, Name = item.Name };
    }

    /// <summary>A second core-like registration that sits after <see cref="StandInCoreDtoService"/> and answers a different ratio.</summary>
    public sealed class LaterStandInDtoService : IDtoService
    {
        public double? GetPrimaryImageAspectRatio(BaseItem item) => LaterStandInAspectRatio;

        public BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User? user = null, BaseItem? owner = null)
            => new() { Id = item.Id, Name = item.Name };

        public IReadOnlyList<BaseItemDto> GetBaseItemDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User? user = null, BaseItem? owner = null, bool skipVisibilityCheck = false)
            => items.Select(i => new BaseItemDto { Id = i.Id, Name = i.Name }).ToArray();

        public BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem>? taggedItems, User? user = null)
            => new() { Id = item.Id, Name = item.Name };
    }

    /// <summary>Plays SessionManager / a controller: takes IDtoService by constructor injection.</summary>
    public sealed class DtoConsumer
    {
        public DtoConsumer(IDtoService dtoService)
        {
            DtoService = dtoService;
        }

        public IDtoService DtoService { get; }
    }
}
