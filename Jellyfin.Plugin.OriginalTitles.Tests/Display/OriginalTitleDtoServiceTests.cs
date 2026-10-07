using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Jellyfin.Plugin.OriginalTitles.Display;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.Display;

/// <summary>
/// The IDtoService decorator: delegates to the core service, then rewrites only dto.Name / dto.SeriesName per
/// <see cref="DisplayNameResolver"/>. Every fixture value below is a literal; expected values are derived from them in comments.
/// </summary>
[Collection(BaseItemLibraryManagerCollection.Name)]
public sealed class OriginalTitleDtoServiceTests : IDisposable
{
    // Movie: translated Name / original title / language "ar".
    private const string MovieName = "Mouch Fi Thniti";
    private const string MovieOriginal = "موش في ثنيتي";

    // Series: translated Name / original title / language "ar".
    private const string SeriesName = "Khottab Al Bab";
    private const string SeriesOriginal = "الخطاب على الباب";

    // French movie: language "fr", which ArabicUser does not enable.
    private const string FrenchName = "Amelie";
    private const string FrenchOriginal = "Le Fabuleux Destin d'Amelie Poulain";

    private const string EpisodeName = "Episode One";

    private static readonly Guid ArabicUser = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FrenchUser = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public OriginalTitleDtoServiceTests()
    {
        BaseItem.LibraryManager = null!;
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = null!;
    }

    private static DisplayNameResolver Resolver()
    {
        var config = new PluginConfiguration();
        config.Users.Add(new UserLanguageEntry { UserId = ArabicUser, Languages = new List<string> { "ar" } });
        config.Users.Add(new UserLanguageEntry { UserId = FrenchUser, Languages = new List<string> { "fr" } });

        // Guid.Empty also enables "ar": a wrapper that maps a null user to Guid.Empty instead of skipping it would rewrite.
        config.Users.Add(new UserLanguageEntry { UserId = Guid.Empty, Languages = new List<string> { "ar" } });
        return new DisplayNameResolver(new UserLanguageSettings(() => config));
    }

    private static (OriginalTitleDtoService Service, FakeCoreDtoService Inner, CapturingLogger Log) Build()
    {
        var inner = new FakeCoreDtoService();
        var log = new CapturingLogger();
        return (new OriginalTitleDtoService(inner, Resolver(), log), inner, log);
    }

    private static User UserWithId(Guid id) => new("user-" + id.ToString("N")[..4], "Default", "Default") { Id = id };

    private static Movie ArabicMovie() => new() { Id = Guid.NewGuid(), Name = MovieName, OriginalTitle = MovieOriginal, OriginalLanguage = "ar" };

    private static Movie FrenchMovie() => new() { Id = Guid.NewGuid(), Name = FrenchName, OriginalTitle = FrenchOriginal, OriginalLanguage = "fr" };

    private static Series ArabicSeries() => new() { Id = Guid.NewGuid(), Name = SeriesName, OriginalTitle = SeriesOriginal, OriginalLanguage = "ar" };

    // Own language null + SeriesId set + BaseItem.LibraryManager null -> Episode.Series throws NullReferenceException (docs/spike.md (c)).
    private static Episode ThrowingEpisode() => new() { Id = Guid.NewGuid(), Name = EpisodeName, SeriesName = SeriesName, SeriesId = Guid.NewGuid(), OriginalLanguage = null };

    [Fact]
    public void GetBaseItemDto_EnabledUser_GetsOriginalTitle_OtherUserAndNullUserGetInnersDto()
    {
        // Movie "Mouch Fi Thniti" / "موش في ثنيتي" / "ar". ArabicUser enables "ar" -> dto.Name = "موش في ثنيتي".
        // FrenchUser enables only "fr" -> dto.Name stays the inner's "Mouch Fi Thniti". user == null -> untouched.
        var (service, inner, log) = Build();
        var movie = ArabicMovie();

        var enabled = service.GetBaseItemDto(movie, new DtoOptions(), UserWithId(ArabicUser));
        Assert.Equal(MovieOriginal, enabled.Name);

        var other = service.GetBaseItemDto(movie, new DtoOptions(), UserWithId(FrenchUser));
        Assert.Same(inner.LastDto, other);
        Assert.Equal(MovieName, other.Name);

        var anonymous = service.GetBaseItemDto(movie, new DtoOptions(), null);
        Assert.Same(inner.LastDto, anonymous);
        Assert.Equal(MovieName, anonymous.Name);
        Assert.Null(anonymous.SeriesName);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void GetBaseItemDto_NullUser_IsNotAFailure_AndIsNotTreatedAsGuidEmpty()
    {
        // Guid.Empty has "ar" enabled in the fixture, so mapping null -> Guid.Empty would rewrite to "موش في ثنيتي";
        // dereferencing a null user would be caught and logged as a failure. Correct: untouched AND no warning.
        // Presence companion: the same movie for ArabicUser is rewritten.
        var (service, _, log) = Build();
        var movie = ArabicMovie();

        Assert.Equal(MovieName, service.GetBaseItemDto(movie, new DtoOptions(), null).Name);
        Assert.Equal(MovieName, service.GetBaseItemDtos(new BaseItem[] { movie }, new DtoOptions(), null).Single().Name);
        Assert.Empty(log.Warnings);
        Assert.Empty(log.Debugs);

        Assert.Equal(MovieOriginal, service.GetBaseItemDto(movie, new DtoOptions(), UserWithId(ArabicUser)).Name);
    }

    [Fact]
    public void GetBaseItemDto_ForwardsEveryArgumentToTheInnerService()
    {
        var (service, inner, _) = Build();
        var movie = ArabicMovie();
        var owner = ArabicSeries();
        var options = new DtoOptions();
        var user = UserWithId(FrenchUser);

        service.GetBaseItemDto(movie, options, user, owner);

        Assert.Same(movie, inner.LastItem);
        Assert.Same(options, inner.LastOptions);
        Assert.Same(user, inner.LastUser);
        Assert.Same(owner, inner.LastOwner);
    }

    [Fact]
    public void GetBaseItemDto_Episode_GetsSeriesOriginalTitleAsSeriesName_OwnNameUntouched()
    {
        // Episode -> series "Khottab Al Bab" / "الخطاب على الباب" / "ar". ArabicUser -> dto.SeriesName = "الخطاب على الباب",
        // dto.Name stays "Episode One". FrenchUser -> dto.SeriesName stays "Khottab Al Bab".
        var series = ArabicSeries();
        BaseItem.LibraryManager = FakeLibraryManager.Create(series);
        var episode = new Episode { Id = Guid.NewGuid(), Name = EpisodeName, SeriesName = SeriesName, SeriesId = series.Id };
        var (service, _, _) = Build();

        var enabled = service.GetBaseItemDto(episode, new DtoOptions(), UserWithId(ArabicUser));
        Assert.Equal(SeriesOriginal, enabled.SeriesName);
        Assert.Equal(EpisodeName, enabled.Name);

        var other = service.GetBaseItemDto(episode, new DtoOptions(), UserWithId(FrenchUser));
        Assert.Equal(SeriesName, other.SeriesName);
        Assert.Equal(EpisodeName, other.Name);

        // Stored data unchanged.
        Assert.Equal(SeriesName, episode.SeriesName);
        Assert.Equal(EpisodeName, episode.Name);
    }

    [Fact]
    public void Wrapper_NeverWritesToTheBaseItem_WhileTheDtoDoesChange()
    {
        // Presence companion first: the DTOs really were rewritten ("موش في ثنيتي", "الخطاب على الباب").
        // Invariant: the items keep Name / OriginalTitle / OriginalLanguage exactly as constructed.
        var (service, _, _) = Build();
        var movie = ArabicMovie();
        var series = ArabicSeries();
        var user = UserWithId(ArabicUser);

        Assert.Equal(MovieOriginal, service.GetBaseItemDto(movie, new DtoOptions(), user).Name);
        Assert.Equal(SeriesOriginal, service.GetBaseItemDtos(new BaseItem[] { series }, new DtoOptions(), user).Single().Name);

        Assert.Equal(MovieName, movie.Name);
        Assert.Equal(MovieOriginal, movie.OriginalTitle);
        Assert.Equal("ar", movie.OriginalLanguage);
        Assert.Equal(SeriesName, series.Name);
        Assert.Equal(SeriesOriginal, series.OriginalTitle);
        Assert.Equal("ar", series.OriginalLanguage);
    }

    [Fact]
    public void GetBaseItemDtos_KeepsOrderAndCount_AndRewritesOnlyMatchingItems()
    {
        // Items: [Arabic movie, French movie, Arabic series], user ArabicUser (["ar"]).
        //   0: "ar" enabled            -> "موش في ثنيتي"
        //   1: "fr" not enabled        -> stays "Amelie"
        //   2: "ar" enabled (series)   -> "الخطاب على الباب"
        var (service, inner, _) = Build();
        var items = new BaseItem[] { ArabicMovie(), FrenchMovie(), ArabicSeries() };

        var dtos = service.GetBaseItemDtos(items, new DtoOptions(), UserWithId(ArabicUser));

        Assert.Equal(3, dtos.Count);
        Assert.Equal(new[] { MovieOriginal, FrenchName, SeriesOriginal }, dtos.Select(d => d.Name));
        Assert.Equal(items.Select(i => i.Id), dtos.Select(d => d.Id));
        Assert.Same(inner.LastDtos, dtos);
    }

    [Fact]
    public void GetBaseItemDtos_InnerDropsAnItem_RemainingDtosAreMatchedById_NotByPosition()
    {
        // Core DtoService filters with items.Where(x => x.IsVisible(user)) (v12.2 DtoService.cs ~166), so the output can be
        // shorter than the input. Items: [Arabic movie (hidden by the fake), French movie, Arabic series]; inner returns 2 DTOs.
        //   out[0] is the French movie -> must stay "Amelie" (index pairing would apply item 0's "موش في ثنيتي")
        //   out[1] is the Arabic series -> "الخطاب على الباب" (index pairing would take item 1, "fr", and leave "Khottab Al Bab")
        var (service, inner, _) = Build();
        var hidden = ArabicMovie();
        inner.Hidden.Add(hidden.Id);
        var items = new BaseItem[] { hidden, FrenchMovie(), ArabicSeries() };

        var dtos = service.GetBaseItemDtos(items, new DtoOptions(), UserWithId(ArabicUser));

        Assert.Equal(2, dtos.Count);
        Assert.Equal(new[] { FrenchName, SeriesOriginal }, dtos.Select(d => d.Name));
    }

    [Fact]
    public void GetBaseItemDtos_ForwardsEveryArgumentToTheInnerService()
    {
        var (service, inner, _) = Build();
        var items = new BaseItem[] { ArabicMovie() };
        var owner = ArabicSeries();
        var options = new DtoOptions();
        var user = UserWithId(FrenchUser);

        service.GetBaseItemDtos(items, options, user, owner, skipVisibilityCheck: true);

        Assert.Same(items, inner.LastItems);
        Assert.Same(options, inner.LastOptions);
        Assert.Same(user, inner.LastUser);
        Assert.Same(owner, inner.LastOwner);
        Assert.True(inner.LastSkipVisibilityCheck);
    }

    [Fact]
    public void FailOpen_ThrowingItem_IsReturnedUntouched_OthersStillRewritten_NothingEscapes()
    {
        // Precondition (not vacuous): the resolver really throws for the episode.
        var throwing = ThrowingEpisode();
        Assert.Throws<NullReferenceException>(() => Resolver().Resolve(throwing, ArabicUser));

        // Batch [Arabic movie, throwing episode, Arabic series] for ArabicUser:
        //   0 -> "موش في ثنيتي"; 1 -> inner's DTO untouched (Name "Episode One", SeriesName "Khottab Al Bab"); 2 -> "الخطاب على الباب".
        var (service, _, log) = Build();
        var items = new BaseItem[] { ArabicMovie(), throwing, ArabicSeries() };

        var dtos = service.GetBaseItemDtos(items, new DtoOptions(), UserWithId(ArabicUser));

        Assert.Equal(3, dtos.Count);
        Assert.Equal(new[] { MovieOriginal, EpisodeName, SeriesOriginal }, dtos.Select(d => d.Name));
        Assert.Equal(SeriesName, dtos[1].SeriesName);

        // Single call: no exception, inner's DTO returned unchanged.
        var single = service.GetBaseItemDto(throwing, new DtoOptions(), UserWithId(ArabicUser));
        Assert.Equal(EpisodeName, single.Name);
        Assert.Equal(SeriesName, single.SeriesName);

        // The failure was logged (as a warning once; see the log-once test).
        Assert.Single(log.Warnings);
    }

    [Fact]
    public void FailOpen_LogsOnceAsWarningPerFailureKind_ThenAtDebug()
    {
        // Three NullReferenceException failures (two in one batch, one single call) -> 1 warning + 2 debug entries.
        var (service, _, log) = Build();
        var user = UserWithId(ArabicUser);

        service.GetBaseItemDtos(new BaseItem[] { ThrowingEpisode(), ThrowingEpisode() }, new DtoOptions(), user);
        service.GetBaseItemDto(ThrowingEpisode(), new DtoOptions(), user);

        Assert.Single(log.Warnings);
        Assert.IsType<NullReferenceException>(log.Warnings[0]);
        Assert.Equal(2, log.Debugs.Count);
    }

    [Fact]
    public void GetItemByNameDto_IsPurePassThrough()
    {
        // Even an item that would qualify (Arabic movie, ArabicUser) is not rewritten here: same instance, inner's name.
        var (service, inner, _) = Build();
        var movie = ArabicMovie();
        var options = new DtoOptions();
        var tagged = new List<BaseItem> { FrenchMovie() };
        var user = UserWithId(ArabicUser);

        var dto = service.GetItemByNameDto(movie, options, tagged, user);

        Assert.Same(inner.LastDto, dto);
        Assert.Equal(MovieName, dto.Name);
        Assert.Same(movie, inner.LastItem);
        Assert.Same(options, inner.LastOptions);
        Assert.Same(tagged, inner.LastTaggedItems);
        Assert.Same(user, inner.LastUser);
    }

    [Fact]
    public void GetPrimaryImageAspectRatio_IsPurePassThrough()
    {
        // The fake answers 1.5 (and 0.75 for a second value) -> the wrapper returns exactly those.
        var (service, inner, _) = Build();
        var movie = ArabicMovie();

        inner.AspectRatio = 1.5;
        Assert.Equal(1.5, service.GetPrimaryImageAspectRatio(movie));
        Assert.Same(movie, inner.LastItem);

        inner.AspectRatio = 0.75;
        Assert.Equal(0.75, service.GetPrimaryImageAspectRatio(movie));
    }

    [Fact]
    public void InnerServiceExceptions_PropagateUnchanged_ForBothDtoMethods()
    {
        // Documented invariant: fail-open covers only the plugin's own decision, never core's service. A sentinel thrown by the
        // inner service must reach the caller as the very same instance (not wrapped, swallowed, or replaced by a made-up DTO).
        // Presence companion: with a healthy inner service the same call returns a DTO and throws nothing.
        var sentinel = new InvalidOperationException("core failed");
        var service = new OriginalTitleDtoService(new ThrowingCoreDtoService(sentinel), Resolver(), new CapturingLogger());
        var user = UserWithId(ArabicUser);

        var single = Assert.Throws<InvalidOperationException>(() => service.GetBaseItemDto(ArabicMovie(), new DtoOptions(), user));
        Assert.Same(sentinel, single);

        var batch = Assert.Throws<InvalidOperationException>(() => service.GetBaseItemDtos(new BaseItem[] { ArabicMovie() }, new DtoOptions(), user));
        Assert.Same(sentinel, batch);

        var (healthy, _, _) = Build();
        Assert.Equal(MovieOriginal, healthy.GetBaseItemDto(ArabicMovie(), new DtoOptions(), user).Name);
    }

    /// <summary>Stand-in for a core service that fails: every DTO method throws the given sentinel.</summary>
    private sealed class ThrowingCoreDtoService : IDtoService
    {
        private readonly Exception _sentinel;

        public ThrowingCoreDtoService(Exception sentinel)
        {
            _sentinel = sentinel;
        }

        public double? GetPrimaryImageAspectRatio(BaseItem item) => throw _sentinel;

        public BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User? user = null, BaseItem? owner = null) => throw _sentinel;

        public IReadOnlyList<BaseItemDto> GetBaseItemDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User? user = null, BaseItem? owner = null, bool skipVisibilityCheck = false) => throw _sentinel;

        public BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem>? taggedItems, User? user = null) => throw _sentinel;
    }

    /// <summary>
    /// Stand-in for core DtoService: builds DTOs the way v12.2 DtoService.cs does for the fields under test
    /// (dto.Id = item.Id ~1177, dto.Name = item.Name ~1222, Episode/Season dto.SeriesName = SeriesName ~1521/~1593),
    /// and records the arguments it received.
    /// </summary>
    public sealed class FakeCoreDtoService : IDtoService
    {
        public HashSet<Guid> Hidden { get; } = new();

        public double? AspectRatio { get; set; }

        public BaseItem? LastItem { get; private set; }

        public IReadOnlyList<BaseItem>? LastItems { get; private set; }

        public DtoOptions? LastOptions { get; private set; }

        public User? LastUser { get; private set; }

        public BaseItem? LastOwner { get; private set; }

        public bool LastSkipVisibilityCheck { get; private set; }

        public List<BaseItem>? LastTaggedItems { get; private set; }

        public BaseItemDto? LastDto { get; private set; }

        public IReadOnlyList<BaseItemDto>? LastDtos { get; private set; }

        public double? GetPrimaryImageAspectRatio(BaseItem item)
        {
            LastItem = item;
            return AspectRatio;
        }

        public BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User? user = null, BaseItem? owner = null)
        {
            (LastItem, LastOptions, LastUser, LastOwner) = (item, options, user, owner);
            return LastDto = ToDto(item);
        }

        public IReadOnlyList<BaseItemDto> GetBaseItemDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User? user = null, BaseItem? owner = null, bool skipVisibilityCheck = false)
        {
            (LastItems, LastOptions, LastUser, LastOwner, LastSkipVisibilityCheck) = (items, options, user, owner, skipVisibilityCheck);
            return LastDtos = items.Where(i => !Hidden.Contains(i.Id)).Select(ToDto).ToArray();
        }

        public BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem>? taggedItems, User? user = null)
        {
            (LastItem, LastOptions, LastTaggedItems, LastUser) = (item, options, taggedItems, user);
            return LastDto = ToDto(item);
        }

        private static BaseItemDto ToDto(BaseItem item) => new()
        {
            Id = item.Id,
            Name = item.Name,
            OriginalTitle = item.OriginalTitle,
            SeriesName = item switch
            {
                Episode e => e.SeriesName,
                Season s => s.SeriesName,
                _ => null
            }
        };
    }

    /// <summary>Records the exceptions logged at Warning and at Debug.</summary>
    public sealed class CapturingLogger : ILogger<OriginalTitleDtoService>
    {
        public List<Exception?> Warnings { get; } = new();

        public List<Exception?> Debugs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(exception);
            }
            else if (logLevel == LogLevel.Debug)
            {
                Debugs.Add(exception);
            }
        }
    }
}
