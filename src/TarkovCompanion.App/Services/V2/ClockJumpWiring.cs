using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.V2;

/// <summary>
/// [#799] What happens when the PC's clock is set while the companion runs.
/// </summary>
/// <remarks>
/// The raid's held times (start, positions, trail) and the marks' creation and expiry are moved
/// by the jump, then the group publishes at once with the moved ages. The raid goes first: the
/// publish that follows reads the position age from it.
///
/// The relay noticing that the PC now agrees with it is the same event seen from outside, so it
/// asks for a look straight away rather than at the next tick.
/// </remarks>
internal static class ClockJumpWiring
{
    public static WallClockJumpDetector Attach(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var detector = new WallClockJumpDetector(
            services.GetService<TimeProvider>(),
            services.GetService<ILogger<WallClockJumpDetector>>());
        var raid = services.GetService<RaidActivityCoordinator>();
        var marks = services.GetService<IRaidMarkStore>();
        var group = services.GetService<GroupSessionService>();
        var logger = services.GetService<ILogger<WallClockJumpDetector>>();
        detector.Jumped += jump => _ = RebaseAsync(jump);
        if (services.GetService<RelayClockOffsetTracker>() is { } relayClock)
        {
            relayClock.ClockCorrected += () => detector.Check();
        }

        detector.Start();
        return detector;

        async Task RebaseAsync(TimeSpan jump)
        {
            try
            {
                var marksMoved = marks?.RebaseClockAsync(jump, CancellationToken.None) ?? Task.CompletedTask;
                if (raid is not null)
                {
                    await raid.RebaseClockAsync(jump, CancellationToken.None).ConfigureAwait(false);
                }

                group?.ClockJumped();
                await marksMoved.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogWarning(exception, "Could not move the held times after the PC clock was set.");
            }
        }
    }
}
