using System.Diagnostics;
using TarkovCompanion.Application.Services.Wiki;

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
            using var process = Process.Start(new ProcessStartInfo(wikiUrl!) { UseShellExecute = true });
            return process is not null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
