using Avalonia;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Styling;
using TarkovCompanion.App.Services.V2.Appearance;
using TarkovCompanion.Core.Domain.Personalization;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.Personalization;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class V2AppearanceApplierTests
{
    [Fact]
    public async Task Platform_change_rereads_system_theme_and_contrast_while_the_app_is_running()
    {
        using var session = HeadlessSessions.StartNew(typeof(AppearanceApp));
        await session.Dispatch(
            () =>
            {
                var source = new FakeSystemAppearanceSource(PlatformThemeVariant.Dark, ColorContrastPreference.NoPreference);
                using var applier = new V2AppearanceApplier(Avalonia.Application.Current!, source);
                var preferences = new WorkspacePreferences(Theme: AppearanceTheme.System);

                applier.Apply(preferences);
                applier.FollowSystemChanges(() => preferences);
                Assert.Equal(ThemeVariant.Dark, applier.Applied);

                source.ChangeTo(PlatformThemeVariant.Light, ColorContrastPreference.NoPreference);
                Assert.Equal(ThemeVariant.Light, applier.Applied);

                // Contrast is an operating-system accessibility request, so it overrides even an
                // explicit in-app choice. The source is read again; the event payload is not used.
                preferences = preferences with { Theme = AppearanceTheme.Dark };
                source.ChangeTo(PlatformThemeVariant.Light, ColorContrastPreference.High);
                Assert.Equal(App.Themes.V2.V2Appearance.HighContrast, applier.Applied);
                Assert.Equal(3, source.ReadCount);
            },
            CancellationToken.None);
    }

    public sealed class AppearanceApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<AppearanceApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private sealed class FakeSystemAppearanceSource(
        PlatformThemeVariant theme,
        ColorContrastPreference contrast) : IV2SystemAppearanceSource
    {
        private PlatformColorValues _values = Values(theme, contrast);

        public event EventHandler? Changed;

        public int ReadCount { get; private set; }

        public PlatformColorValues Read()
        {
            ReadCount++;
            return _values;
        }

        public void ChangeTo(PlatformThemeVariant nextTheme, ColorContrastPreference nextContrast)
        {
            _values = Values(nextTheme, nextContrast);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }

        private static PlatformColorValues Values(PlatformThemeVariant wantedTheme, ColorContrastPreference wantedContrast) =>
            new()
            {
                ThemeVariant = wantedTheme,
                ContrastPreference = wantedContrast,
            };
    }
}
