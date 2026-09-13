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
    /// <c>SHFILEOPSTRUCTW</c>, laid out the way shellapi.h actually declares it.
    /// </summary>
    /// <remarks>
    /// This was written with <c>Pack = 1</c> and that took the whole application down. The
    /// header wraps this struct in <c>pshpack8.h</c>, not <c>pshpack1.h</c>, and on x64 eight
    /// byte packing is just natural alignment: the shell expects four bytes of padding between
    /// <see cref="Function"/> and <see cref="From"/>, two after <see cref="Flags"/>, and four
    /// after <see cref="AnyOperationsAborted"/>. Packed to one byte, every field from
    /// <see cref="From"/> onward sat four bytes early, so the shell read its source path out of
    /// the back half of one pointer and the front half of the next, then dereferenced it.
    ///
    /// That is an access violation inside the shell, which no managed <c>catch</c> can see, so
    /// the process was killed outright on a background timer thread with nothing in the log.
    /// The layout is pinned by a test for that reason.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellFileOperationRequest
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
