using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// The real relay, <c>Program.cs</c> and all, as a separate process listening on a free loopback port.
/// </summary>
/// <remarks>
/// A child process because <c>Program.cs</c> is top-level statements that end in <c>app.Run()</c>:
/// there is no entry point to call and stop. It also gives a test an environment of its own, which
/// the operator key needs: <c>RelayAdmin</c> reads a process-wide variable, and no test in this
/// process may set one.
/// </remarks>
internal sealed partial class RunningRelay : IAsyncDisposable
{
    private static readonly string[] ClearedVariables =
    [
        "TARKOV_GROUP_STATE", "STATE_DIRECTORY", "TARKOV_RELAY_ADMIN_KEY", "TARKOV_RELAY_OWNER_RECOVERY_SECRET",
        "TARKOV_RELAY_TRUSTED_FORWARDERS", "TARKOV_RELAY_UPDATE_STATUS", "TARKOV_UPDATE_FEED_ROOT",
        "ASPNETCORE_URLS", "DOTNET_URLS",
    ];

    private readonly Process _process;
    private readonly List<string> _log;

    private RunningRelay(Process process, HttpClient client, List<string> log)
    {
        _process = process;
        _log = log;
        Client = client;
    }

    public HttpClient Client { get; }

    public Task<HttpResponseMessage> GetWithKeyAsync(string path, string operatorKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Admin-Key", operatorKey);
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendWithKeyAsync(HttpMethod method, string path, string operatorKey)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", operatorKey);
        return Client.SendAsync(request);
    }

    public string Log()
    {
        lock (_log)
        {
            return string.Join('\n', _log);
        }
    }

    public static async Task<RunningRelay> StartAsync(params (string Name, string Value)[] environment)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "TarkovCompanion.GroupServer.dll");
        Assert.True(File.Exists(assembly), $"the relay was not built beside the tests: {assembly}");
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Run the way the unit runs it: `dotnet TarkovCompanion.GroupServer.dll`, against the
        // runtimeconfig the build copies beside it because the tests reference the relay project.
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        foreach (var name in ClearedVariables)
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        var log = new List<string>();
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start };
        void Observe(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (log)
            {
                log.Add(line);
            }

            if (Listening().Match(line) is { Success: true } match)
            {
                listening.TrySetResult(match.Groups["address"].Value);
            }
        }

        process.OutputDataReceived += (_, e) => Observe(e.Data);
        process.ErrorDataReceived += (_, e) => Observe(e.Data);
        process.Exited += (_, _) => listening.TrySetException(
            new InvalidOperationException("the relay exited before it listened:\n" + string.Join('\n', log)));
        process.EnableRaisingEvents = true;
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var address = await listening.Task.WaitAsync(TimeSpan.FromSeconds(60));
            return new RunningRelay(
                process,
                new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") },
                log);
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        Stop(_process);
        return ValueTask.CompletedTask;
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }

        return Environment.ProcessPath is { } current &&
            string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? current
                : "dotnet";
    }

    [GeneratedRegex(@"Now listening on:\s*(?<address>http://\S+)")]
    private static partial Regex Listening();
}
