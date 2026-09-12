using System.Runtime.InteropServices;
using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.Platform.Windows.Storage;

/// <summary>
/// Moves a file to the Windows recycle bin.
/// </summary>
/// <remarks>
/// The companion tidies away screenshots it did not create, so the operation has to be one the
/// player can undo. The shell's own delete is that operation: the file appears in the recycle
/// bin exactly as it would had they deleted it in Explorer, with the same restore command and
/// the same quota governing when it really goes.
///
/// <c>SHFileOperationW</c> is the older of the two shell APIs for this and is used in
/// preference to <c>IFileOperation</c> because it is a single call with no COM apartment
/// requirements, and this runs on a background timer thread. The flags suppress every piece of
/// interface it would otherwise show; a companion quietly tidying a folder must never put a
/// dialog in front of somebody who is in a raid.
/// </remarks>
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint DeleteOperation = 0x0003;

    /// <summary>
    /// Recycle rather than delete, and show nothing at all while doing it.
    /// </summary>
    /// <remarks>
    /// FOF_ALLOWUNDO is the flag the whole feature rests on: without it the shell deletes
    /// outright. The other four suppress the confirmation prompt, the error dialog, the
    /// progress window and the directory-creation prompt, none of which belong in front of
    /// somebody who is in a raid.
    /// </remarks>
    private const ushort RecycleSilently = 0x0040 | 0x0010 | 0x0400 | 0x0004 | 0x0200;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public bool Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!IsAvailable)
        {
            return false;
        }

        // The shell takes a list of paths terminated by an empty one, so the string carries a
        // second null beyond the one the marshaller appends.
        var from = Marshal.StringToHGlobalUni(path + '\0');
        try
        {
            var request = new ShellFileOperationRequest
            {
                WindowHandle = IntPtr.Zero,
                Function = DeleteOperation,
                From = from,
                To = IntPtr.Zero,
                Flags = RecycleSilently,
                AnyOperationsAborted = 0,
                NameMappings = IntPtr.Zero,
                ProgressTitle = IntPtr.Zero,
            };

            // A non-zero result means the shell refused, which is not an error worth reporting:
            // the file stays where it is and the next sweep will try again.
            return ShellFileOperation(ref request) == 0 && request.AnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int ShellFileOperation(ref ShellFileOperationRequest request);

    /// <summary>
    /// <c>SHFILEOPSTRUCTW</c>, which shellapi.h declares inside a byte-packed region.
    /// </summary>
    /// <remarks>
    /// <see cref="StructLayout"/> with <c>Pack = 1</c> is load-bearing. Without it the compiler
    /// pads after <see cref="Flags"/> and every field beyond it lands at the wrong offset, so
    /// the shell reads rubbish where the abort flag should be.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ShellFileOperationRequest
    {
        public IntPtr WindowHandle;
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }
}
