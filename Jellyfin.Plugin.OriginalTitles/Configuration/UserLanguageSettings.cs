using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalTitles.Configuration;

/// <summary>
/// Reads per-user enabled languages from the live plugin configuration.
/// </summary>
public class UserLanguageSettings : IUserLanguageSettings
{
    private readonly Func<PluginConfiguration> _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLanguageSettings"/> class.
    /// </summary>
    /// <param name="configProvider">Returns the current configuration on every call (it is replaced when the page saves).</param>
    public UserLanguageSettings(Func<PluginConfiguration> configProvider)
    {
        _configProvider = configProvider;
    }

    /// <inheritdoc />
    public bool IsEnabled(Guid userId, string? languageCode)
    {
        var wanted = Normalise(languageCode);
        if (wanted.Length == 0)
        {
            return false;
        }

        PluginConfiguration? config;
        try
        {
            config = _configProvider();
        }
#pragma warning disable CA1031 // An unreadable configuration means nothing is enabled; never break the caller.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }

        var users = config?.Users;
        if (users is null)
        {
            return false;
        }

        foreach (var entry in users)
        {
            if (entry is null || entry.UserId != userId || entry.Languages is null)
            {
                continue;
            }

            foreach (var stored in entry.Languages)
            {
                // A stored empty string normalises to "" and can never equal a non-empty wanted code.
                if (Normalise(stored) == wanted)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetConfiguredUserIds()
    {
        PluginConfiguration? config;
        try
        {
            config = _configProvider();
        }
#pragma warning disable CA1031 // An unreadable configuration means nobody is configured; never break the caller.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Array.Empty<Guid>();
        }

        var ids = new List<Guid>();
        foreach (var entry in config?.Users ?? new List<UserLanguageEntry>())
        {
            if (entry is not null && !ids.Contains(entry.UserId))
            {
                ids.Add(entry.UserId);
            }
        }

        return ids;
    }

    // Settings keep whatever the admin typed; BaseItem.OriginalLanguage keeps raw values when no LocalizationManager exists.
    private static string Normalise(string? code) => code is null ? string.Empty : code.Trim().ToLowerInvariant();
}
