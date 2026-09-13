using System.Runtime.InteropServices;
using TarkovCompanion.Platform.Windows.Storage;

namespace TarkovCompanion.WindowsSmokeTests;

/// <summary>
/// Pins the layout of the struct the recycle bin hands to the shell.
/// </summary>
/// <remarks>
/// This was declared with <c>Pack = 1</c>, which put every field from the source path onward
/// four bytes early. The shell then read a path out of the back half of one pointer and the
/// front half of the next and dereferenced it, so the application died of an access violation
/// on a background timer thread, hourly, with nothing in its own log: a fault inside a
/// P/Invoke is not an exception and no <c>catch</c> ever sees it.
///
/// A wrong layout therefore cannot be caught at run time and has to be caught here. The
/// offsets are the ones shellapi.h produces for <c>SHFILEOPSTRUCTW</c> on x64, where its
/// <c>pshpack8.h</c> region is simply natural alignment.
/// </remarks>
public sealed class ShellInteropLayoutTests
{
    [Theory]
    [InlineData("WindowHandle", 0)]
    [InlineData("Function", 8)]
    [InlineData("From", 16)]
    [InlineData("To", 24)]
    [InlineData("Flags", 32)]
    [InlineData("AnyOperationsAborted", 36)]
    [InlineData("NameMappings", 40)]
    [InlineData("ProgressTitle", 48)]
    public void FieldsSitWhereTheShellExpectsThem(string field, int offset)
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(offset, Marshal.OffsetOf<WindowsRecycleBin.ShellFileOperationRequest>(field).ToInt32());
    }

    [Fact]
    public void TheStructIsTheSizeTheShellExpects() =>
        Assert.Equal(56, Marshal.SizeOf<WindowsRecycleBin.ShellFileOperationRequest>());
}
