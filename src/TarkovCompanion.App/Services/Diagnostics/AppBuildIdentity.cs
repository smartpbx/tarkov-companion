using System.Reflection;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Which build is running: its version, and the commit it was built from when that is known.
/// </summary>
/// <remarks>
/// Read from the assembly, because that is the one place the number cannot drift from the code
/// it describes. The version is decided once per build by <c>scripts/build-version.sh</c> from
/// the <c>PRODUCT_VERSION</c> file: the first part is the product generation (2 is the V2
/// workspace), the last is the CI run that built it, and a build nobody stamped says
/// <c>-dev</c>. An installed V2 build spent a week introducing itself as 1.0.1121 because that
/// first part was typed out in eight places and never once revisited.
///
/// The self-test reports this, and Windows verification compares it with the version the
/// package, the feed and the installer were given, so the four cannot disagree unnoticed.
/// </remarks>
/// <param name="Version">For example <c>2.0.1140</c>, or <c>2.0.0-dev</c> for a local build.</param>
/// <param name="Commit">The full commit hash, or null when the build did not record one.</param>
public sealed record AppBuildIdentity(string Version, string? Commit)
{
    private const int LongestBelievableValue = 200;

    /// <summary>The running application.</summary>
    public static AppBuildIdentity Current { get; } = FromAssembly(typeof(AppBuildIdentity).Assembly);

    /// <summary>The product generation: 2 for the V2 workspace. Null when the version has no number in front.</summary>
    public int? Generation
    {
        get
        {
            var end = Version.IndexOf('.', StringComparison.Ordinal);
            return end > 0 && int.TryParse(Version.AsSpan(0, end), out var major) ? major : null;
        }
    }

    public static AppBuildIdentity FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Parse(informational ?? assembly.GetName().Version?.ToString());
    }

    /// <summary>
    /// Splits <c>VERSION+COMMIT</c>.
    /// </summary>
    /// <remarks>
    /// The commit is the first dot-separated piece after the plus and no more. The workflow
    /// passes VERSION+SHA and the SDK then appends the source revision to that, so the raw
    /// value is the same forty characters twice with a dot between them.
    /// </remarks>
    public static AppBuildIdentity Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion) || informationalVersion.Length > LongestBelievableValue)
        {
            return new("unknown", null);
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return new(informationalVersion, null);
        }

        var metadata = informationalVersion[(plus + 1)..];
        var dot = metadata.IndexOf('.', StringComparison.Ordinal);
        var commit = dot < 0 ? metadata : metadata[..dot];
        return new(informationalVersion[..plus], commit.Length > 0 ? commit : null);
    }
}
