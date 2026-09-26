using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Application.Services.Sound;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Platform.Windows.Sound;

namespace TarkovCompanion.App.Services.Sound;

/// <summary>[#712 0-10] Registers sound cues and speech: one call from AppComposition.</summary>
/// <remarks>
/// The triggers are started by the Setup card's factory, which the shell resolves at start-up
/// with the rest of Setup: one object owns their lifetime and a composition without the card has
/// no sound at all, which is the safe way round.
/// </remarks>
internal static class SoundComposition
{
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(provider => new SoundSettingsStore(provider.GetService<IWorkspaceLayoutStore>()));
        services.AddSingleton(_ => new WindowsSoundOutput());
        services.AddSingleton<SilentSoundOutput>();
        services.AddSingleton<ICueOutput>(provider => Output(provider));
        services.AddSingleton<ISpeechOutput>(provider => (ISpeechOutput)Output(provider));
        services.AddSingleton<IAudioDeviceCatalog>(provider => (IAudioDeviceCatalog)Output(provider));
        services.AddSingleton<ISoundLines, LocalizedSoundLines>();
        services.AddSingleton(provider => new SoundCueService(
            provider.GetRequiredService<SoundSettingsStore>(),
            provider.GetRequiredService<ICueOutput>(),
            provider.GetRequiredService<ISpeechOutput>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<SoundCueService>>()));
        services.AddSingleton<ISoundCues>(provider => provider.GetRequiredService<SoundCueService>());
        services.AddSingleton(provider =>
        {
            var groupSettings = provider.GetService<IGroupSettingsStore>();
            var self = new GroupSelfName(groupSettings);
            var items = provider.GetService<IItemRepository>();
            return new SoundCueTriggers(
                provider.GetRequiredService<ISoundCues>(),
                provider.GetRequiredService<ISoundLines>(),
                provider.GetService<TimeProvider>(),
                provider.GetService<SituationService>(),
                provider.GetService<IRuntimeStateStore>(),
                // Registered only where recognition is (Windows); elsewhere there are no scans to speak.
                provider.GetService<LatestScanResultPublisher>(),
                () => self.Current,
                items is null ? null : async (id, token) => (await items.GetAsync(id, token).ConfigureAwait(false))?.ShortName);
        });
        services.AddSingleton(provider =>
        {
            _ = provider.GetRequiredService<SoundCueTriggers>();
            var output = provider.GetRequiredService<ICueOutput>();
            return new SetupSoundViewModel(
                provider.GetRequiredService<SoundSettingsStore>(),
                provider.GetRequiredService<ISoundCues>(),
                provider.GetRequiredService<ISoundLines>(),
                provider.GetRequiredService<IAudioDeviceCatalog>(),
                output.IsAvailable,
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        });
    }

    private static ICueOutput Output(IServiceProvider provider) =>
        OperatingSystem.IsWindows() ? provider.GetRequiredService<WindowsSoundOutput>() : provider.GetRequiredService<SilentSoundOutput>();
}

/// <summary>This player's own display name in the group, so their own marks make no sound.</summary>
internal sealed class GroupSelfName
{
    private readonly IGroupSettingsStore? _store;
    private volatile string? _current;

    public GroupSelfName(IGroupSettingsStore? store)
    {
        _store = store;
        if (store is null)
        {
            return;
        }

        store.Changed += (_, _) => _ = LoadAsync();
        _ = LoadAsync();
    }

    public string? Current => _current;

    private async Task LoadAsync()
    {
        try
        {
            _current = (await _store!.GetAsync(CancellationToken.None).ConfigureAwait(false)).DisplayName;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Unknown: every mark then counts as a squadmate's, which errs toward a tone.
        }
    }
}
