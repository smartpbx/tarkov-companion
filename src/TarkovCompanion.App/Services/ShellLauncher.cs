using System.ComponentModel;
using System.Diagnostics;
using TarkovCompanion.Infrastructure.Processes;

namespace TarkovCompanion.App.Services;

/// <summary>
/// Opens a web address or a folder in the system's own program for it.
/// </summary>
/// <remarks>
/// The one way the interface starts anything, in place of the window's launcher. The launcher
/// cannot be told a working directory, so what it starts inherits this process's; when that was
/// the install folder, a browser opened from here kept the folder pinned until the browser
/// closed and no update could be applied (#599, <see cref="OutsideInstallFolder"/>).
/// </remarks>
public static class ShellLauncher
{
    /// <summary>Opens an http or https address in the default browser.</summary>
    public static bool TryOpen(Uri? address)
    {
        if (address is not { IsAbsoluteUri: true }
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        return Start(address.AbsoluteUri);
    }

    /// <summary>Opens a folder that exists in the file manager.</summary>
    public static bool TryOpenFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && Start(Path.GetFullPath(folder));

    private static bool Start(string target)
    {
        try
        {
            using var process = Process.Start(OutsideInstallFolder.ShellOpen(target));
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or Win32Exception
                                          or PlatformNotSupportedException
                                          or FileNotFoundException)
        {
            // Every caller shows the address or the path as text beside the button.
            return false;
        }
    }
}
