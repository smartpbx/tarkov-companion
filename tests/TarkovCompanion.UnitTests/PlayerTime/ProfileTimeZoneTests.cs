using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.UnitTests.Profiles;

namespace TarkovCompanion.UnitTests.PlayerTime;

/// <summary>[#269] Times shown in the profile's time zone, "system" by default.</summary>
public sealed class ProfileTimeZoneTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("system")]
    [InlineData("System")]
    // #458 wrote this into every profile before anybody could choose; it must not move them to UTC.
    [InlineData("Etc/UTC")]
    [InlineData("Not/AZone")]
    public void SystemThePlaceholderAndUnknownZonesMeanTheMachinesZone(string? id) =>
        Assert.Null(ProfileTimeZone.Resolve(id));

    [Fact]
    public void AnUnknownZoneCannotBeStored()
    {
        Assert.False(ProfileTimeZone.IsValid("Not/AZone"));
        Assert.True(ProfileTimeZone.IsValid("system"));
        Assert.True(ProfileTimeZone.IsValid("UTC"));
        Assert.True(ProfileTimeZone.IsValid("Asia/Tokyo"));
    }

    [Fact]
    public void TimesAreShownInTheChosenZoneWithItsDaylightSaving()
    {
        var summer = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        using (LocalTime.UseZone(ProfileTimeZone.Resolve("America/New_York")!))
        {
            Assert.Equal("2026-07-01 08:00", LocalTime.Sortable(summer));
            Assert.Equal("2026-01-15 07:00", LocalTime.Sortable(winter));
            Assert.Equal("UTC-04:00", LocalTime.Offset(summer));
        }

        using (LocalTime.UseZone(ProfileTimeZone.Resolve("Asia/Tokyo")!))
        {
            Assert.Equal("2026-07-01 21:00", LocalTime.Sortable(summer));
            Assert.Equal("2026-07-01", LocalTime.SortableDate(summer.AddHours(-12)));
        }

        using (LocalTime.UseZone(ProfileTimeZone.Resolve("UTC")!))
        {
            Assert.Equal("2026-07-01 12:00", LocalTime.Sortable(summer));
        }
    }

    [Fact]
    public async Task AProfilesZoneIsStoredAndSystemIsNormalised()
    {
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(ProfileV2Fixtures.Now));
        var profile = ProfileV2Fixtures.Profile(1, "g1", ProfileGameMode.Pvp, "gpu");
        await service.CreateAsync(ProfileV2Fixtures.Request(profile), CancellationToken.None);

        await service.UpdateTimeZoneAsync(profile.Context.Identity.ProfileId, "Asia/Tokyo", CancellationToken.None);
        Assert.Equal("Asia/Tokyo", store.Current.Profiles.Single().Context.Locale.TimeZone);
        Assert.Equal("en-US", store.Current.Profiles.Single().Context.Locale.Language);

        await service.UpdateTimeZoneAsync(profile.Context.Identity.ProfileId, "System", CancellationToken.None);
        Assert.Equal(ProfileTimeZone.System, store.Current.Profiles.Single().Context.Locale.TimeZone);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateTimeZoneAsync(profile.Context.Identity.ProfileId, "Not/AZone", CancellationToken.None));
    }
}
