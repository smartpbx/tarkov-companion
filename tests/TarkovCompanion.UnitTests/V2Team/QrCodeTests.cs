using System.Diagnostics;
using System.Text;
using TarkovCompanion.App.Services.V2.Team;

namespace TarkovCompanion.UnitTests.V2Team;

/// <summary>
/// The QR encoder behind the pairing panel's code image (#289).
/// </summary>
/// <remarks>
/// An encoder cannot be proved correct by structure alone: a symbol can have perfect finders,
/// timing and format bits and still carry the wrong codewords. So the real check shells out to
/// <c>zbarimg</c> and reads the payload back with a decoder this repository did not write. It is
/// skipped where the tool is absent, which is why the golden matrices below exist as well: they
/// were pinned from runs that zbar had already read, so a change that breaks the encoder fails
/// here whether or not a decoder is installed.
/// </remarks>
public sealed class QrCodeTests
{
    /// <summary>A payload of the shape the pairing ceremony actually produces.</summary>
    private const string PairingPayload = "TARKOV-COMPANION-PAIR/2.0/H7K2QM/9f2c4a1b8e6d3057";

    [Theory]
    [InlineData("A", 1)]
    [InlineData(PairingPayload, 4)]
    public void TheSmallestSymbolThatFitsIsUsed(string text, int version) =>
        Assert.Equal(version, QrCode.Encode(text).Version);

    [Fact]
    public void CapacityMatchesTheStandardTableForLevelM()
    {
        int[] expected = [14, 26, 42, 62, 84, 106, 122, 152, 180, 213];

        Assert.Equal(expected, Enumerable.Range(1, 10).Select(QrCode.CapacityBytes));
    }

    [Fact]
    public void SomethingTooLongForVersionTenIsRefused() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('A', 214)));

    [Theory]
    [InlineData("A")]
    [InlineData(PairingPayload)]
    public void EveryCornerCarriesAFinderPattern(string text)
    {
        var code = QrCode.Encode(text);
        var last = code.Size - 7;

        foreach (var (row, column) in new[] { (0, 0), (0, last), (last, 0) })
        {
            // The 7x7 finder is a dark ring, a light ring, then a 3x3 dark core.
            for (var y = 0; y < 7; y++)
            {
                for (var x = 0; x < 7; x++)
                {
                    var ring = Math.Max(Math.Abs(y - 3), Math.Abs(x - 3));
                    Assert.Equal(ring != 2, code[row + y, column + x]);
                }
            }
        }

        // And the fourth corner does not, which is how a reader knows the orientation.
        Assert.False(code[last, last] && code[last + 6, last + 6]);
    }

    [Fact]
    public void TheTimingPatternsAlternate()
    {
        var code = QrCode.Encode(PairingPayload);

        for (var index = 8; index < code.Size - 8; index++)
        {
            Assert.Equal(index % 2 == 0, code[6, index]);
            Assert.Equal(index % 2 == 0, code[index, 6]);
        }
    }

    [Fact]
    public void TheSameTextAlwaysGivesTheSameSymbol() =>
        Assert.Equal(Render(QrCode.Encode(PairingPayload)), Render(QrCode.Encode(PairingPayload)));

    /// <summary>
    /// Pinned module matrices, as one line of <c>#</c> and <c>.</c> per row.
    /// </summary>
    /// <remarks>
    /// Each was produced by this encoder and then read back by zbar before being written down, so
    /// these are not "whatever it did last time": they are three symbols a decoder has decoded.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Golden))]
    public void APinnedSymbolIsUnchanged(string text, string expected) =>
        Assert.Equal(expected, Render(QrCode.Encode(text)));

    public static TheoryData<string, string> Golden()
    {
        var data = new TheoryData<string, string>();
        foreach (var (text, rows) in GoldenSymbols)
        {
            data.Add(text, rows);
        }

        return data;
    }

    /// <summary>
    /// Reads the symbol back with a decoder this repository did not write.
    /// </summary>
    /// <remarks>
    /// Where zbar is not installed this cannot run, and it returns rather than passing a
    /// weaker assertion off as this one. The pinned matrices above are the ratchet on such a
    /// machine: they were produced by this encoder and read by zbar before being written down,
    /// so a change that breaks the encoder still fails the suite.
    /// </remarks>
    [Theory]
    [InlineData("A")]
    [InlineData(PairingPayload)]
    [InlineData("TARKOV-COMPANION-PAIR/2.0/ZZZZZZ/0123456789abcdef0123456789abcdef")]
    public void ADecoderReadsTheTextBack(string text)
    {
        if (!HasZbar)
        {
            return;
        }

        var code = QrCode.Encode(text);
        var path = Path.Combine(Path.GetTempPath(), $"tc-qr-{Guid.NewGuid():N}.pgm");
        try
        {
            File.WriteAllBytes(path, Pgm(code, scale: 8, quiet: 4));
            var read = Run("zbarimg", $"--quiet --raw \"{path}\"");

            Assert.Equal(text, read.Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Writes the golden file and its images, when TC_QR_DUMP names a directory.
    /// </summary>
    /// <remarks>
    /// How the pinned matrices above are regenerated, and how the images that zbar reads are
    /// produced. A test rather than a separate tool because it needs the same encoder and the
    /// same rendering the assertions use; it does nothing at all unless the variable is set.
    /// </remarks>
    [Fact]
    public void WritesTheGoldenFileWhenAsked()
    {
        var target = Environment.GetEnvironmentVariable("TC_QR_DUMP");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        Directory.CreateDirectory(target);
        var texts = new[]
        {
            "A",
            PairingPayload,
            "TARKOV-COMPANION-PAIR/2.0/ZZZZZZ/0123456789abcdef0123456789abcdef",
        };
        var golden = new StringBuilder();
        for (var index = 0; index < texts.Length; index++)
        {
            var code = QrCode.Encode(texts[index]);
            File.WriteAllBytes(Path.Combine(target, $"qr-{index}.pgm"), Pgm(code, 8, 4));
            golden.Append(texts[index]).Append('\n').Append(Render(code));
            if (index < texts.Length - 1)
            {
                golden.Append("---\n");
            }
        }

        File.WriteAllText(Path.Combine(target, "qr-golden.txt"), golden.ToString());
    }

    private static bool HasZbar =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator)
        .Any(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, "zbarimg")));

    private static string Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"{file} did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        return output;
    }

    /// <summary>The symbol as a binary PGM, which is the simplest image a decoder will read.</summary>
    private static byte[] Pgm(QrCode code, int scale, int quiet)
    {
        var width = (code.Size + (quiet * 2)) * scale;
        var pixels = new byte[width * width];
        Array.Fill(pixels, (byte)255);
        for (var row = 0; row < code.Size; row++)
        {
            for (var column = 0; column < code.Size; column++)
            {
                if (!code[row, column])
                {
                    continue;
                }

                for (var y = 0; y < scale; y++)
                {
                    var offset = (((row + quiet) * scale) + y) * width;
                    Array.Fill(pixels, (byte)0, offset + ((column + quiet) * scale), scale);
                }
            }
        }

        var header = Encoding.ASCII.GetBytes($"P5\n{width} {width}\n255\n");
        return [.. header, .. pixels];
    }

    private static string Render(QrCode code)
    {
        var builder = new StringBuilder();
        for (var row = 0; row < code.Size; row++)
        {
            for (var column = 0; column < code.Size; column++)
            {
                builder.Append(code[row, column] ? '#' : '.');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static IReadOnlyList<(string Text, string Rows)> GoldenSymbols { get; } = LoadGolden();

    private static IReadOnlyList<(string, string)> LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "V2Team", "qr-golden.txt");
        if (!File.Exists(path))
        {
            return [];
        }

        var symbols = new List<(string, string)>();
        // Normalised, because a Windows checkout hands this file over with CRLF (it is a .txt, which
        // .gitattributes did not pin) and every pinned row then carried a '\r' the encoder never
        // writes: `windows-build` was red on main and on every open pull request for this alone.
        foreach (var block in File.ReadAllText(path).Replace("\r\n", "\n").Split("\n---\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Trim('\n').Split('\n');
            symbols.Add((lines[0], string.Join('\n', lines[1..]) + "\n"));
        }

        return symbols;
    }
}
