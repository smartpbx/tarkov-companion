using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

public sealed class SupportBundleTests
{
    /// <summary>
    /// The shape of a screenshot name survives; the position in it does not.
    /// </summary>
    /// <remarks>
    /// A name whose shape this build does not recognise yields no position, and that is
    /// invisible from every other angle — the game confirms the screenshot, the folder is
    /// right, the file is there, and the player never appears on anybody's map. The shape is
    /// the whole diagnosis. Where they were standing is nobody's business.
    /// </remarks>
    [Fact]
    public void MaskingKeepsEverySeparatorAndNoCoordinate()
    {
        var masked = SupportBundle.MaskDigits("2026-09-13[20-15]_123.4, 5.6, -78.9_0.0, 0.0, 0.0, 1.0_12.00.png");

        Assert.Equal("0000-00-00[00-00]_000.0, 0.0, -00.0_0.0, 0.0, 0.0, 0.0_00.00.png", masked);
        Assert.DoesNotContain("123", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("78.9", masked, StringComparison.Ordinal);
    }

    /// <summary>
    /// A locale that writes decimals with a comma stays visible after masking.
    /// </summary>
    /// <remarks>
    /// This is the shape most likely to be failing on somebody else's machine, and it would be
    /// worthless if masking flattened it into the shape that works.
    /// </remarks>
    [Fact]
    public void AnUnexpectedShapeIsStillRecognisableAfterMasking()
    {
        var masked = SupportBundle.MaskDigits("2026-09-13[20-15-33]_123,4, 5,6, -78,9_1.2E-05.png");

        Assert.Contains("[00-00-00]", masked, StringComparison.Ordinal);
        Assert.Contains("000,0", masked, StringComparison.Ordinal);
        Assert.Contains("E-00", masked, StringComparison.Ordinal);
    }

    /// <summary>A user's folder name is a real name often enough to matter.</summary>
    [Theory]
    [InlineData(@"C:\Users\Geoffrey\AppData\Local\TarkovCompanion", @"C:\Users\<user>\AppData\Local\TarkovCompanion")]
    [InlineData(@"d:\users\clay\Documents", @"d:\users\<user>\Documents")]
    public void AUserFolderIsReplaced(string line, string expected) =>
        Assert.Equal(expected, SupportBundle.Redact(line));

    /// <summary>
    /// The group key is the only thing protecting a room, and it must never travel in a report.
    /// </summary>
    [Theory]
    [InlineData("X-Group-Key: hunter2hunter2")]
    [InlineData("""sending {"key":"hunter2hunter2"} to the relay""")]
    public void AGroupKeyIsRedacted(string line)
    {
        var redacted = SupportBundle.Redact(line);

        // The invariant is that the secret is gone and the line still reads, not that the
        // punctuation lands in any particular place.
        Assert.DoesNotContain("hunter2hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted>", redacted, StringComparison.Ordinal);
    }

    /// <summary>A line that carries neither is left exactly as it was.</summary>
    [Fact]
    public void AnOrdinaryLineIsUntouched()
    {
        const string line = "2026-09-13T21:04:11Z [group] the relay did not answer";

        Assert.Equal(line, SupportBundle.Redact(line));
    }
}
