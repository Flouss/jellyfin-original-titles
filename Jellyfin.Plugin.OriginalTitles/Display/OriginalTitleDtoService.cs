using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalTitles.Display;

/// <summary>
/// Decorates core's <see cref="IDtoService"/>: every call is delegated unchanged, then only <c>dto.Name</c> /
/// <c>dto.SeriesName</c> of the returned DTOs are replaced per <see cref="DisplayNameResolver"/> for the requesting user.
/// </summary>
/// <remarks>
/// Invariants: never writes to a <see cref="BaseItem"/> (only to the DTOs core just built for this call); a null user is never
/// rewritten; fail open: a failure while deciding one item leaves that DTO exactly as core built it, the others are still
/// rewritten, nothing escapes, and each exception type is logged once at Warning (afterwards at Debug).
/// Not covered by fail-open: exceptions thrown by the inner (core) service propagate unchanged, exactly as without the plugin.
/// Batch DTOs are matched to items by <c>Id</c> (core sets <c>dto.Id = item.Id</c>), never by position: core drops items the
/// user cannot see, so the output can be shorter than the input.
/// </remarks>
public class OriginalTitleDtoService : IDtoService
{
    private readonly IDtoService _inner;
    private readonly DisplayNameResolver _resolver;
    private readonly ILogger<OriginalTitleDtoService> _logger;
    private readonly ConcurrentDictionary<Type, byte> _loggedFailureKinds = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OriginalTitleDtoService"/> class.
    /// </summary>
    /// <param name="inner">The core service being decorated.</param>
    /// <param name="resolver">Decides the displayed names.</param>
    /// <param name="logger">The logger.</param>
    public OriginalTitleDtoService(IDtoService inner, DisplayNameResolver resolver, ILogger<OriginalTitleDtoService> logger)
    {
        _inner = inner;
        _resolver = resolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public double? GetPrimaryImageAspectRatio(BaseItem item) => _inner.GetPrimaryImageAspectRatio(item);

    /// <inheritdoc />
    public BaseItemDto GetBaseItemDto(BaseItem item, DtoOptions options, User? user = null, BaseItem? owner = null)
    {
        var dto = _inner.GetBaseItemDto(item, options, user, owner);
        if (user is not null)
        {
            Rewrite(item, dto, user.Id);
        }

        return dto;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItemDto> GetBaseItemDtos(IReadOnlyList<BaseItem> items, DtoOptions options, User? user = null, BaseItem? owner = null, bool skipVisibilityCheck = false)
    {
        var dtos = _inner.GetBaseItemDtos(items, options, user, owner, skipVisibilityCheck);
        if (user is null || dtos is null || dtos.Count == 0)
        {
            return dtos!;
        }

        Dictionary<Guid, BaseItem> itemsById;
        try
        {
            itemsById = new Dictionary<Guid, BaseItem>(items.Count);
            foreach (var item in items)
            {
                if (item is not null)
                {
                    itemsById.TryAdd(item.Id, item);
                }
            }
        }
#pragma warning disable CA1031 // Fail open: never break browsing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailure(ex, Guid.Empty);
            return dtos;
        }

        // The DTOs are rewritten in place; the list itself (order, count) is core's, returned as is.
        foreach (var dto in dtos)
        {
            if (dto is not null && itemsById.TryGetValue(dto.Id, out var item))
            {
                Rewrite(item, dto, user.Id);
            }
        }

        return dtos;
    }

    /// <inheritdoc />
    public BaseItemDto GetItemByNameDto(BaseItem item, DtoOptions options, List<BaseItem>? taggedItems, User? user = null)
        => _inner.GetItemByNameDto(item, options, taggedItems, user);

    private void Rewrite(BaseItem item, BaseItemDto? dto, Guid userId)
    {
        if (dto is null)
        {
            return;
        }

        try
        {
            var (name, seriesName) = _resolver.Resolve(item, userId);
            if (name is not null)
            {
                dto.Name = name;
            }

            if (seriesName is not null)
            {
                dto.SeriesName = seriesName;
            }
        }
#pragma warning disable CA1031 // Fail open: never break browsing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailure(ex, dto.Id);
        }
    }

    private void LogFailure(Exception ex, Guid itemId)
    {
        if (_loggedFailureKinds.TryAdd(ex.GetType(), 0))
        {
            _logger.LogWarning(
                ex,
                "Original Titles: could not decide the display name for item {ItemId}; it is shown unchanged. Further {ExceptionType} failures are logged at Debug level only",
                itemId,
                ex.GetType().Name);
        }
        else
        {
            _logger.LogDebug(ex, "Original Titles: display name decision failed for item {ItemId}; shown unchanged", itemId);
        }
    }
}
