using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.OriginalTitles.Configuration;
using Xunit;

namespace Jellyfin.Plugin.OriginalTitles.Tests.UserSettings;

public class UserLanguageSettingsTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Stranger = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static UserLanguageSettings Build(params (Guid User, string[] Languages)[] users)
    {
        var config = new PluginConfiguration();
        foreach (var (user, languages) in users)
        {
            config.Users.Add(new UserLanguageEntry { UserId = user, Languages = new List<string>(languages) });
        }

        return new UserLanguageSettings(() => config);
    }

    [Fact]
    public void IsEnabled_NormalisesCaseAndWhitespace_AndRejectsUnlistedLanguage()
    {
        // Alice stores ["ar","fr"]. Lower-casing "AR" gives "ar" (listed -> true); trimming " fr " gives "fr" (listed -> true);
        // "de" is not in the list (-> false), which is the absence companion to the two presence assertions.
        var settings = Build((Alice, new[] { "ar", "fr" }));

        Assert.True(settings.IsEnabled(Alice, "AR"));
        Assert.True(settings.IsEnabled(Alice, " fr "));
        Assert.False(settings.IsEnabled(Alice, "de"));
    }

    [Fact]
    public void IsEnabled_NormalisesStoredCodes_TooAndPlainCodeMatches()
    {
        // Stored " AR " normalises to "ar"; query "ar" matches it (presence), "fr" does not (absence).
        var settings = Build((Alice, new[] { " AR " }));

        Assert.True(settings.IsEnabled(Alice, "ar"));
        Assert.False(settings.IsEnabled(Alice, "fr"));
    }

    [Fact]
    public void IsEnabled_UnknownUser_IsFalseForEveryLanguage_WhileKnownUserIsTrue()
    {
        // Only Alice has "ar"; Stranger is not in the list so every language is false.
        var settings = Build((Alice, new[] { "ar", "fr" }));

        Assert.True(settings.IsEnabled(Alice, "ar"));
        Assert.False(settings.IsEnabled(Stranger, "ar"));
        Assert.False(settings.IsEnabled(Stranger, "fr"));
        Assert.False(settings.IsEnabled(Stranger, "de"));
    }

    [Fact]
    public void IsEnabled_EmptyList_IsFalse_WhileSiblingUserWithListIsTrue()
    {
        // Alice has an empty list (nothing enabled); Bob stores ["ar"] so "ar" is true for Bob only.
        var settings = Build((Alice, Array.Empty<string>()), (Bob, new[] { "ar" }));

        Assert.False(settings.IsEnabled(Alice, "ar"));
        Assert.True(settings.IsEnabled(Bob, "ar"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsEnabled_NullEmptyOrWhitespaceLanguage_IsFalse_EvenWhenListContainsEmptyString(string? language)
    {
        // The stored list holds "" and "  " (must be ignored on load) next to "ar" (proves the user is otherwise enabled).
        var settings = Build((Alice, new[] { string.Empty, "  ", "ar" }));

        Assert.True(settings.IsEnabled(Alice, "ar"));
        Assert.False(settings.IsEnabled(Alice, language));
    }

    [Fact]
    public void IsEnabled_ReadsLiveConfiguration_NotASnapshot()
    {
        // Saving the config page replaces/changes the configuration; the service must see the new list.
        var config = new PluginConfiguration();
        var settings = new UserLanguageSettings(() => config);
        Assert.False(settings.IsEnabled(Alice, "ar"));

        config.Users.Add(new UserLanguageEntry { UserId = Alice, Languages = new List<string> { "ar" } });

        Assert.True(settings.IsEnabled(Alice, "ar"));
    }

    [Fact]
    public void IsEnabled_ThrowingProvider_IsFalse_NotAnException_WhileWorkingProviderIsTrue()
    {
        // The provider wraps Plugin.Instance.Configuration at runtime; if reading it throws, nothing is enabled.
        var throwing = new UserLanguageSettings(() => throw new InvalidOperationException("config unavailable"));
        Assert.False(throwing.IsEnabled(Alice, "ar"));

        Assert.True(Build((Alice, new[] { "ar" })).IsEnabled(Alice, "ar"));
    }

    [Fact]
    public void IsEnabled_NullConfigNullUsersOrNullEntry_IsFalse_WhileValidSiblingEntryIsTrue()
    {
        // A null configuration, a null Users list, or a null entry inside Users (possible after XML deserialisation of a
        // hand-edited file) must read as "nothing enabled" for that part, never throw. Bob's valid ["ar"] entry after
        // the null entry still answers true (presence companion).
        Assert.False(new UserLanguageSettings(() => null!).IsEnabled(Alice, "ar"));
        Assert.False(new UserLanguageSettings(() => new PluginConfiguration { Users = null! }).IsEnabled(Alice, "ar"));

        var config = new PluginConfiguration();
        config.Users.Add(null!);
        config.Users.Add(new UserLanguageEntry { UserId = Bob, Languages = new List<string> { "ar" } });
        var settings = new UserLanguageSettings(() => config);

        Assert.False(settings.IsEnabled(Alice, "ar"));
        Assert.True(settings.IsEnabled(Bob, "ar"));
    }

    [Fact]
    public void Configuration_XmlRoundTrip_PreservesTwoUsersWithDifferentLists()
    {
        var original = new PluginConfiguration();
        original.Users.Add(new UserLanguageEntry { UserId = Alice, Languages = new List<string> { "ar", "fr" } });
        original.Users.Add(new UserLanguageEntry { UserId = Bob, Languages = new List<string> { "ja" } });

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        using var reader = new StringReader(writer.ToString());
        var copy = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.Equal(2, copy.Users.Count);
        Assert.Equal(Alice, copy.Users[0].UserId);
        Assert.Equal(new[] { "ar", "fr" }, copy.Users[0].Languages);
        Assert.Equal(Bob, copy.Users[1].UserId);
        Assert.Equal(new[] { "ja" }, copy.Users[1].Languages);

        // And the restored configuration drives the service identically.
        var settings = new UserLanguageSettings(() => copy);
        Assert.True(settings.IsEnabled(Alice, "fr"));
        Assert.False(settings.IsEnabled(Bob, "fr"));
        Assert.True(settings.IsEnabled(Bob, "ja"));
    }
}
