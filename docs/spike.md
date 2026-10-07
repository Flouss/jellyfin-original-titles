# Task 0 spike notes (2026-10-07)

Environment: dotnet SDK 10.0.103; packages `Jellyfin.Controller` and `Jellyfin.Model` **12.2.0**.
Real Jellyfin source = git tag `v12.2` (the tag `v12.2.0` does not exist; repo tags are `v12.2`, `v12.1`, ...), read from
`raw.githubusercontent.com/jellyfin/jellyfin/v12.2/...`.

Probes were run in a throwaway console project outside this repo (same two NuGet references; reflection plus tiny constructions).
Nothing from it is kept. Only `ScaffoldTests` lives in the test project.

## NuGet version

`https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json` and `.../jellyfin.model/index.json` both list
`12.0.0-rc1..rc7`, `12.0.0`, `12.1.0`, `12.2.0`. **12.2.0 exists for both** and is used for both.
Reflection reports the `Jellyfin.Controller` assembly version as 12.2.0.0.

## (a) Constructing entities without a server: YES

`new Movie { Name = "Hello", OriginalTitle = "Bonjour", OriginalLanguage = "fr" }`, `new Series { ... }`, `new Season { Name = ... }`,
`new Episode { Name = ..., SeriesName = ... }` all construct and read values back with no library manager / server.
Setter visibility (reflection; all public get + public set unless noted):

| Member | Declared in | Setter |
|---|---|---|
| `Name`, `OriginalTitle`, `OriginalLanguage` | `BaseItem` | public |
| `Episode.SeriesName`, `Episode.SeriesId`, `Episode.SeasonId` | `Episode` | public |
| `Season.SeriesName`, `Season.SeriesId` | `Season` | public |
| `Episode.Series`, `Episode.Season`, `Season.Series` | | **get-only** (resolved through `BaseItem.LibraryManager`) |

`OriginalLanguage` setter (v12.2 `BaseItem.cs` 228-232): `LocalizationManager?.FindLanguageInfo(value)?.TwoLetterISOLanguageName ?? value`.
With no `LocalizationManager` the **raw value is stored unnormalised**: `OriginalLanguage = " AR "` reads back `" AR "` (measured).
Settings/wrapper code must trim + lowercase on its own side (as the plan requires); tests must not assume the setter normalises.

## (b) DTO setters: YES

`BaseItemDto.Name`, `.OriginalTitle`, `.SeriesName`: `string`, public get + public set (reflection on 12.2.0).

## (c) `GetInheritedOriginalLanguage()` without a library manager

Real source (v12.2): `Episode.cs` 157-160 and `Season.cs` 132-135 are both
`return OriginalLanguage ?? Series?.GetInheritedOriginalLanguage();`; `BaseItem.cs` 1841 returns `OriginalLanguage`.
The `Series` getter takes `SeriesId` (or `FindSeriesId()` via the parent chain) and does `LibraryManager.GetItemById(seriesId) as Series`.
`BaseItem.LibraryManager` is `public static ILibraryManager { get; set; }` (`BaseItem.cs` 496), settable from tests.

Measured:

| Setup | Result |
|---|---|
| Episode/Season, own language `null`, `SeriesId` empty, no library manager | `null` (no throw) |
| Episode, own `"fr"`, `SeriesId` set, no library manager | `"fr"` (short-circuits before touching the library manager) |
| Episode, own `null`, `SeriesId` set, no library manager | **`NullReferenceException`** |
| fake `ILibraryManager` whose `GetItemById(Guid)` returns the series (series language `"ar"`), own `null` | Episode `"ar"`, Season `"ar"` |
| same fake, Episode own `"fr"` | `"fr"` |
| same fake, Episode own `""` (empty string) | **`""`, no fallback** |
| same fake, series language also `null` | `null` |

**How to exercise it:** set the static `BaseItem.LibraryManager` to a fake `ILibraryManager` that implements only `GetItemById(Guid)`,
and set `episode.SeriesId = series.Id` (`season.SeriesId` likewise). I built the fake with `System.Reflection.DispatchProxy`
(`DispatchProxy.Create<ILibraryManager, Fake>()`), which needs no extra package; every other member can throw `NotSupportedException`
(only `GetItemById` was invoked in the probe). Because the member is static, tests that set it must restore it to `null` afterwards and must
not run in parallel with other tests that read it (xunit parallelises across test classes: put them in one `[Collection]` or disable parallelisation).

**Plan/spec discrepancy (real code wins):** the brief and spec say the language falls back to the series when the own language is
"empty". The code uses `??`, so it falls back only when the own language is **`null`**; an empty string is returned as-is and does NOT
fall back. The wrapper must treat `""` as "no language / not enabled" itself, and fallback tests must use `null`.
Also note: `Episode` falls back **directly to the series** (not via its season), and for an episode in an enabled-language series
whose own `OriginalLanguage` is `""`, the series language is not consulted.

## (d) XmlSerializer: proven

- `List<Entry>` with `Entry { Guid UserId; List<string> Languages }` serialises and deserialises equal (round-trip true). Output shape:
  `<Users><Entry><UserId>...</UserId><Languages><string>ar</string><string>fr</string></Languages></Entry></Users>`.
- A `Dictionary<Guid, List<string>>` property: `new XmlSerializer(typeof(...))` throws `InvalidOperationException`
  ("Cannot serialize member ... because it implements IDictionary"), both on a plain class and on a `BasePluginConfiguration` subclass.
  So the "no `Dictionary<,>` in the plugin configuration" constraint is proven, not assumed.

## IDtoService (NuGet assembly `Jellyfin.Controller` 12.2.0, namespace `MediaBrowser.Controller.Dto`)

Exactly four members (reflection, full type names):

```
double? GetPrimaryImageAspectRatio(BaseItem item)
BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User user = default, BaseItem owner = default)
IReadOnlyList<BaseItemDto> GetBaseItemDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User user = default, BaseItem owner = default, bool skipVisibilityCheck = default)
BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem> taggedItems, User user = default)
```

Namespaces: `BaseItem` = `MediaBrowser.Controller.Entities`, `BaseItemDto` = `MediaBrowser.Model.Dto`, `DtoOptions` = `MediaBrowser.Controller.Dto`,
`User` = `Jellyfin.Database.Implementations.Entities.User` (note: not `Jellyfin.Data.Entities`). The probe printed `=default` for the
optional parameters (`HasDefaultValue`), i.e. they default to null/false.

The NuGet exposes **no implementation** of `IDtoService` (scan of the `Jellyfin.Controller` assembly for implementers: none; no
`Emby.Server.Implementations.Dto.DtoService`). In the real source (v12.2) it is `public class DtoService : IDtoService` in
`Emby.Server.Implementations/Dto/DtoService.cs` (line 40), registered by `ApplicationHost.cs` line 606:
`serviceCollection.AddSingleton<IDtoService, DtoService>();`. It is a public class, but it lives in `Emby.Server.Implementations`,
which is not a referenced package, so the plugin cannot compile against the concrete type. A wrapper has to get the inner instance at runtime
(for example from the existing `ServiceDescriptor`'s implementation type, instantiated with `ActivatorUtilities`). Not verified here: that such
decoration from an `IPluginServiceRegistrator` works at runtime (design spec lists it as open).

## Scaffold choices

Mirrors another Jellyfin plugin scaffold: same csproj properties (`net10.0`, nullable, `GenerateDocumentationFile`,
`TreatWarningsAsErrors=false`, `CodeAnalysisRuleSet=../jellyfin.ruleset`), StyleCop.Analyzers `1.2.0-beta.556`, embedded
`Configuration/configPage.html` served through `GetPages()`. Plugin GUID is new: `7c1e5a3d-92b4-4f6e-8d0a-3b5f1c2e9a47`.
Differences from that scaffold: the `Plugin` ctor takes only `IApplicationPaths` + `IXmlSerializer` (no `IServerApplicationHost`);
the solution is a classic `.sln` (SDK 10 defaults to `.slnx`). Test project: xunit 2.x on `net10.0`.
`dotnet build`: 0 warnings, 0 errors. `dotnet test`: 1 passed (mutation-proofed: removing the `EmbeddedResource` item fails it).

## Task 6 spike: what the server's transliterator really emits (2026-10-07)

Source read (v12.2): `BaseItem.GetSortName(string, bool, ServerConfiguration)` (`BaseItem.cs` 992) lower-cases, applies the
`ServerConfiguration` remove-word/remove-char/replace-char lists (defaults: words `the|a|an`, remove chars `, & - { } '`, replace chars `. + %`),
zero-pads every digit run to 10 digits (`ModifySortChunks`), runs `RemoveDiacritics`, and only if the result is not pure ASCII calls
`string.Transliterated()` (`src/Jellyfin.Extensions/StringExtensions.cs`: ICU `Any-Latin; Latin-Ascii; Lower; NFD; [:Nonspacing Mark:] Remove; [:Punctuation:] Remove;`,
overridable via `JELLYFIN_TRANSLITERATOR_ID`).

**ICU runs outside the server.** A throwaway console project (outside the repo, same `Jellyfin.Controller`/`Jellyfin.Model` 12.2.0 references; ICU
arrives transitively through `Jellyfin.Extensions`) called the real `BaseItem.GetSortName(name, true, new ServerConfiguration())` and `name.Transliterated()`.
Observed, verbatim (input -> output):

| Input | `GetSortName(name, true, new ServerConfiguration())` | `name.Transliterated()` |
|---|---|---|
| `الخطاب على الباب` | `alkhtab ʿly albab` | identical |
| `موش في ثنيتي` | `mwsh fy thnyty` (matches a SortName seen on a real server) | identical |
| `الشيخ غانم` | `alshykh ghanm` | identical |
| `الخطاب-على` (hyphen) | `alkhtabʿly` (hyphen removed, words fused) | identical |
| `ء أ عين` | `ʾ a ʿyn` | identical |
| `عمر 7` | `ʿmr 0000000007` (digit run zero-padded) | `ʿmr 7` |
| `Al-Khattab, the 'Door'!` | not run | `alkhattab the door` (punctuation removed) |

Non-ASCII characters that survive Latin-Ascii: ayn is **U+02BF MODIFIER LETTER LEFT HALF RING** (`ʿ`), hamza is **U+02BE MODIFIER LETTER RIGHT HALF RING** (`ʾ`).
Neither is removed by `[:Punctuation:] Remove` (they are modifier letters), so they stay inside the word (`ʿly`). They look like apostrophes; use the escapes.

Consequences used by Task 6: (1) the definite article arrives glued to the word (`alkhtab`, `albab`), never as `al-`, so `al`/`el` must be stripped without a hyphen;
(2) the transliterated title contains ʿ/ʾ, the letters Arabizi writes `3`/`2`, which the rules already drop; (3) `GetSortName` pads digits and removes hyphens, `Transliterated()` does neither,
so the plugin's index uses `Transliterated()` directly (identical output on all Arabic samples above, no zero-padding that would break the `7`->`h` mapping).
