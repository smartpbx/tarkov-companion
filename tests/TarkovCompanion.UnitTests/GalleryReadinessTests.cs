using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#279] The Windows page gallery asks a --gallery-scene launch "ready" on the diagnostic
/// channel instead of sleeping, so the answer has to be the scene's own and never a guess.
/// </summary>
public sealed class GalleryReadinessTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Answers_ready_with_what_the_scene_reported()
    {
        var readiness = new GalleryReadiness();
        var processor = new DiagnosticCommandProcessor(Token, new NoScan(), readiness, TimeSpan.FromSeconds(5));
        var answer = processor.ProcessAsync(new("r1", DiagnosticCommandKind.Ready, Token), DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.False(answer.IsCompleted);

        readiness.Ready("route on customs: 3 quests");
        var response = await answer;

        Assert.True(response.Accepted);
        Assert.Equal("ready", response.Event);
        Assert.Equal("route on customs: 3 quests", response.Detail);
    }

    [Fact]
    public async Task Answers_not_ready_with_the_scene_failure()
    {
        var readiness = new GalleryReadiness();
        readiness.Failed("no Plan's objective route after 45 s");
        var processor = new DiagnosticCommandProcessor(Token, new NoScan(), readiness, TimeSpan.FromSeconds(5));

        var response = await processor.ProcessAsync(new("r2", DiagnosticCommandKind.Ready, Token), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal("not-ready", response.Event);
        Assert.Equal("no Plan's objective route after 45 s", response.Error);
    }

    [Fact]
    public async Task Answers_not_ready_when_the_scene_never_finishes()
    {
        var processor = new DiagnosticCommandProcessor(Token, new NoScan(), new GalleryReadiness(), TimeSpan.FromMilliseconds(50));

        var response = await processor.ProcessAsync(new("r3", DiagnosticCommandKind.Ready, Token), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal("not-ready", response.Event);
        Assert.StartsWith("not ready after", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_without_a_scene_is_never_ready()
    {
        var processor = new DiagnosticCommandProcessor(Token, new NoScan());

        var response = await processor.ProcessAsync(new("r4", DiagnosticCommandKind.Ready, Token), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal("not-ready", response.Event);
    }

    [Fact]
    public async Task Readiness_still_needs_the_token()
    {
        var readiness = new GalleryReadiness();
        readiness.Ready("map");
        var processor = new DiagnosticCommandProcessor(Token, new NoScan(), readiness);

        var response = await processor.ProcessAsync(new("r5", DiagnosticCommandKind.Ready, "wrong"), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.False(response.Accepted);
        Assert.Equal("unauthorized", response.Error);
    }

    [Fact]
    public async Task A_named_condition_waits_for_the_scene_and_then_for_the_step()
    {
        var readiness = new GalleryReadiness();
        var step = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? asked = null;
        readiness.AfterStep = (condition, _) =>
        {
            asked = condition;
            return step.Task;
        };
        var processor = new DiagnosticCommandProcessor(Token, new NoScan(), readiness, TimeSpan.FromSeconds(5));

        var answer = processor.ProcessAsync(new("r6", DiagnosticCommandKind.Ready, Token, "loot"), DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.False(answer.IsCompleted);
        Assert.Null(asked);

        readiness.Ready("map on customs: 12 extracts");
        Assert.False(answer.IsCompleted);
        step.SetResult("loot: 40 pins");
        var response = await answer;

        Assert.Equal("loot", asked);
        Assert.Equal("ready", response.Event);
        Assert.Equal("loot: 40 pins", response.Detail);
    }

    [Fact]
    public async Task A_condition_the_scene_cannot_meet_is_not_ready()
    {
        var readiness = new GalleryReadiness();
        readiness.Ready("map");
        var processor = new DiagnosticCommandProcessor(Token, new NoScan(), readiness, TimeSpan.FromMilliseconds(200));

        var unknown = await processor.ProcessAsync(new("r7", DiagnosticCommandKind.Ready, Token, "settled"), DateTimeOffset.UnixEpoch, CancellationToken.None);
        readiness.AfterStep = (condition, _) => throw new InvalidOperationException($"no gallery condition named '{condition}'");
        var failed = await processor.ProcessAsync(new("r8", DiagnosticCommandKind.Ready, Token, "everything"), DateTimeOffset.UnixEpoch, CancellationToken.None);
        readiness.AfterStep = (_, _) => new TaskCompletionSource<string>().Task;
        var slow = await processor.ProcessAsync(new("r9", DiagnosticCommandKind.Ready, Token, "settled"), DateTimeOffset.UnixEpoch, CancellationToken.None);
        var unsafeName = await processor.ProcessAsync(new("r10", DiagnosticCommandKind.Ready, Token, "../x"), DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal("not-ready", unknown.Event);
        Assert.Equal("the scene cannot wait for 'settled'", unknown.Error);
        Assert.Equal("not-ready", failed.Event);
        Assert.Equal("no gallery condition named 'everything'", failed.Error);
        Assert.Equal("not-ready", slow.Event);
        Assert.StartsWith("not ready after", slow.Error, StringComparison.Ordinal);
        Assert.False(unsafeName.Accepted);
        Assert.Equal("invalid-scenario", unsafeName.Error);
    }

    [Fact]
    public void Parses_the_scene_and_refuses_one_it_does_not_know()
    {
        var options = AppCommandLine.Parse(["--developer-mode", "--gallery-scene", "Marks", "--map", "customs"]);

        Assert.Equal(GallerySceneKind.Marks, options.GalleryScene);
        Assert.Empty(options.UnknownOptions);
        Assert.Equal(GallerySceneKind.InRaid, AppCommandLine.Parse(["--developer-mode", "--gallery-scene", "inraid"]).GalleryScene);
        Assert.Equal(GallerySceneKind.Draw, AppCommandLine.Parse(["--developer-mode", "--gallery-scene", "draw"]).GalleryScene);
        Assert.Throws<ArgumentException>(() => AppCommandLine.Parse(["--gallery-scene", "everything"]));
        Assert.Throws<ArgumentException>(() => AppCommandLine.Parse(["--gallery-scene", "7"]));
    }

    private sealed class NoScan : IRuntimeScanUseCase
    {
        public Task<ScanExecutionResult> ExecuteAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A readiness question must not scan.");
    }
}
