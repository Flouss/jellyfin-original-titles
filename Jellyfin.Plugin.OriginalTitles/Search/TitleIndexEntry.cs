using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.OriginalTitles.Search;

/// <summary>
/// One indexed Movie/Series: the romanisation keys of its transliterated OriginalTitle and its raw original-language code.
/// </summary>
/// <param name="ItemId">The item id.</param>
/// <param name="Kind">Movie or Series.</param>
/// <param name="Language">The item's <c>OriginalLanguage</c> as stored (compared later through <c>IUserLanguageSettings</c>).</param>
/// <param name="Keys">Keys from <see cref="RomanisationKey.TransliteratedKeysOf"/>; never empty.</param>
public sealed record TitleIndexEntry(Guid ItemId, BaseItemKind Kind, string Language, IReadOnlyList<string> Keys);
