using System.Diagnostics;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Infrastructure.Processes;

namespace TarkovCompanion.Infrastructure.Wiki;

/// <summary>Opens an allowed wiki link with the OS's default handler for an https URL.</summary>
public sealed class SystemBrowserWikiLinkOpener : IWikiLinkOpener
{
    public bool TryOpen(string? wikiUrl)
    {
        if (!WikiLinkPolicy.IsAllowed(wikiUrl))
        {
            return false;
        }

        try
        {
            // #599: never with the install folder as the browser's working directory.
            using var process = Process.Start(OutsideInstallFolder.ShellOpen(wikiUrl!));
            return process is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
