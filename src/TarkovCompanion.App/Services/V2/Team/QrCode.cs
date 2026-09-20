namespace TarkovCompanion.App.Services.V2.Team;

/// <summary>
/// A QR symbol: which modules are dark, and how wide the square is.
/// </summary>
/// <remarks>
/// The quiet zone is not included. A renderer adds it, because how much margin a code needs
/// depends on what it is drawn on and the specification's four-module minimum is a floor.
/// </remarks>
public sealed class QrCode
{
    private readonly bool[,] _modules;

    private QrCode(bool[,] modules, int version)
    {
        _modules = modules;
        Version = version;
        Size = modules.GetLength(0);
    }

    /// <summary>The symbol version, 1 to 10.</summary>
    public int Version { get; }

    /// <summary>The width and height of the symbol in modules, quiet zone excluded.</summary>
    public int Size { get; }

    /// <summary>Whether the module at this row and column is dark.</summary>
    public bool this[int row, int column] => _modules[row, column];

    /// <summary>
    /// Encodes text as a byte-mode, error-correction-level-M symbol.
    /// </summary>
    /// <remarks>
    /// Written from ISO/IEC 18004, using the standard forms for the field arithmetic, the
    /// generator polynomial and the placement order, and deliberately narrow: byte mode, level M,
    /// versions 1 to 10. That covers 213 characters, and the pairing
    /// payload this exists for is capped at 128 by the protocol. Level M because a code read off
    /// a monitor by a phone a foot away has no dirt, no crease and no print error to recover
    /// from, and a higher level would only make the modules smaller.
    ///
    /// Correctness of a QR encoder is not something structural tests can fully establish, so the
    /// tests beside this file pin the finished module matrix of three payloads, and those three
    /// were read back by zbar before being pinned.
    /// </remarks>
    /// <exception cref="ArgumentException">The text is empty or too long for version 10.</exception>
    public static QrCode Encode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var data = System.Text.Encoding.UTF8.GetBytes(text);
        var version = SmallestVersionFor(data.Length);
        var codewords = Codewords(data, version);
        var modules = new bool[Widths(version), Widths(version)];
        var reserved = new bool[Widths(version), Widths(version)];
        DrawFunctionPatterns(modules, reserved, version);
        DrawCodewords(modules, reserved, codewords);
        var mask = ChooseMask(modules, reserved, version);
        ApplyMask(modules, reserved, mask);
        DrawFormat(modules, mask);
        return new(modules, version);
    }

    /// <summary>How many bytes a level-M symbol of this version carries in byte mode.</summary>
    public static int CapacityBytes(int version) =>
        DataCodewords(version) - (version >= 10 ? 3 : 2);

    private static int Widths(int version) => (version * 4) + 17;

    private static int SmallestVersionFor(int length)
    {
        for (var version = 1; version <= 10; version++)
        {
            if (CapacityBytes(version) >= length)
            {
                return version;
            }
        }

        throw new ArgumentException(
            $"{length} bytes does not fit a version 10 symbol at error-correction level M.",
            nameof(length));
    }

    // ISO/IEC 18004 table 9, error-correction level M: total codewords, error-correction
    // codewords per block, and the block split. Two groups where the blocks differ in length by
    // one codeword; the second group is empty where they do not.
    private static (int Total, int EccPerBlock, int Group1Blocks, int Group1Data, int Group2Blocks, int Group2Data)
        Layout(int version) => version switch
        {
            1 => (26, 10, 1, 16, 0, 0),
            2 => (44, 16, 1, 28, 0, 0),
            3 => (70, 26, 1, 44, 0, 0),
            4 => (100, 18, 2, 32, 0, 0),
            5 => (134, 24, 2, 43, 0, 0),
            6 => (172, 16, 4, 27, 0, 0),
            7 => (196, 18, 4, 31, 0, 0),
            8 => (242, 22, 2, 38, 2, 39),
            9 => (292, 22, 3, 36, 2, 37),
            10 => (346, 26, 4, 43, 1, 44),
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };

    private static int DataCodewords(int version)
    {
        var layout = Layout(version);
        return (layout.Group1Blocks * layout.Group1Data) + (layout.Group2Blocks * layout.Group2Data);
    }

    /// <summary>Bits left over after the codeword stream, which are written as zero.</summary>
    private static int RemainderBits(int version) => version switch
    {
        1 => 0,
        >= 2 and <= 6 => 7,
        _ => 0,
    };

    private static byte[] Codewords(byte[] data, int version)
    {
        var layout = Layout(version);
        var dataCodewords = DataCodewords(version);
        var bits = new BitBuffer(dataCodewords * 8);
        bits.Append(0b0100, 4);
        bits.Append((uint)data.Length, version >= 10 ? 16 : 8);
        foreach (var value in data)
        {
            bits.Append(value, 8);
        }

        // Terminator, then zeros to the byte boundary, then the specification's alternating pad.
        bits.Append(0, Math.Min(4, (dataCodewords * 8) - bits.Length));
        bits.Append(0, (8 - (bits.Length % 8)) % 8);
        for (var pad = 0xEC; bits.Length < dataCodewords * 8; pad ^= 0xEC ^ 0x11)
        {
            bits.Append((uint)pad, 8);
        }

        var blocks = new List<byte[]>();
        var eccBlocks = new List<byte[]>();
        var offset = 0;
        var source = bits.ToBytes();
        for (var block = 0; block < layout.Group1Blocks + layout.Group2Blocks; block++)
        {
            var length = block < layout.Group1Blocks ? layout.Group1Data : layout.Group2Data;
            var slice = source[offset..(offset + length)];
            offset += length;
            blocks.Add(slice);
            eccBlocks.Add(ReedSolomon.Remainder(slice, layout.EccPerBlock));
        }

        // Interleaved: one codeword from each block in turn, the data stream then the error
        // correction stream. A block shorter than its neighbours simply has nothing to give on
        // the last pass.
        var interleaved = new List<byte>(layout.Total);
        var longest = blocks.Max(block => block.Length);
        for (var index = 0; index < longest; index++)
        {
            foreach (var block in blocks.Where(block => index < block.Length))
            {
                interleaved.Add(block[index]);
            }
        }

        for (var index = 0; index < layout.EccPerBlock; index++)
        {
            foreach (var block in eccBlocks)
            {
                interleaved.Add(block[index]);
            }
        }

        return [.. interleaved];
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] reserved, int version)
    {
        var size = modules.GetLength(0);
        foreach (var (row, column) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
        {
            DrawFinder(modules, reserved, row, column, size);
        }

        // Timing patterns run between the finders on row and column 6.
        for (var index = 8; index < size - 8; index++)
        {
            Set(modules, reserved, 6, index, index % 2 == 0);
            Set(modules, reserved, index, 6, index % 2 == 0);
        }

        foreach (var row in AlignmentCentres(version))
        {
            foreach (var column in AlignmentCentres(version))
            {
                // The three corners already hold finder patterns.
                if ((row <= 8 && column <= 8)
                    || (row <= 8 && column >= size - 9)
                    || (row >= size - 9 && column <= 8))
                {
                    continue;
                }

                DrawAlignment(modules, reserved, row, column);
            }
        }

        // The format areas are reserved now and written after masking; the module below the
        // top-left finder's format strip is always dark.
        for (var index = 0; index < 9; index++)
        {
            reserved[8, index] = true;
            reserved[index, 8] = true;
        }

        for (var index = 0; index < 8; index++)
        {
            reserved[8, size - 1 - index] = true;
            reserved[size - 1 - index, 8] = true;
        }

        Set(modules, reserved, size - 8, 8, true);

        if (version >= 7)
        {
            DrawVersion(modules, reserved, version, size);
        }
    }

    private static void DrawFinder(bool[,] modules, bool[,] reserved, int top, int left, int size)
    {
        for (var row = -1; row <= 7; row++)
        {
            for (var column = -1; column <= 7; column++)
            {
                var y = top + row;
                var x = left + column;
                if (y < 0 || y >= size || x < 0 || x >= size)
                {
                    continue;
                }

                var distance = Math.Max(Math.Abs(row - 3), Math.Abs(column - 3));
                Set(modules, reserved, y, x, distance != 2 && distance <= 3);
            }
        }
    }

    private static void DrawAlignment(bool[,] modules, bool[,] reserved, int centreRow, int centreColumn)
    {
        for (var row = -2; row <= 2; row++)
        {
            for (var column = -2; column <= 2; column++)
            {
                Set(
                    modules,
                    reserved,
                    centreRow + row,
                    centreColumn + column,
                    Math.Max(Math.Abs(row), Math.Abs(column)) != 1);
            }
        }
    }

    /// <summary>ISO/IEC 18004 annex E, the alignment-pattern centres for versions 1 to 10.</summary>
    private static int[] AlignmentCentres(int version) => version switch
    {
        1 => [],
        2 => [6, 18],
        3 => [6, 22],
        4 => [6, 26],
        5 => [6, 30],
        6 => [6, 34],
        7 => [6, 22, 38],
        8 => [6, 24, 42],
        9 => [6, 26, 46],
        10 => [6, 28, 50],
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static void DrawVersion(bool[,] modules, bool[,] reserved, int version, int size)
    {
        var remainder = (uint)version;
        for (var index = 0; index < 12; index++)
        {
            remainder = (remainder << 1) ^ (((remainder >> 11) & 1) * 0x1F25);
        }

        var bits = ((uint)version << 12) | remainder;
        for (var index = 0; index < 18; index++)
        {
            var bit = ((bits >> index) & 1) == 1;
            var row = index / 3;
            var column = size - 11 + (index % 3);
            Set(modules, reserved, row, column, bit);
            Set(modules, reserved, column, row, bit);
        }
    }

    private static void DrawFormat(bool[,] modules, int mask)
    {
        var size = modules.GetLength(0);
        // Level M is 0b00; the five bits are the level then the mask.
        var value = (uint)mask;
        var remainder = value;
        for (var index = 0; index < 10; index++)
        {
            remainder = (remainder << 1) ^ (((remainder >> 9) & 1) * 0x537);
        }

        var bits = ((value << 10) | remainder) ^ 0x5412;

        // The first copy runs down column 8 and then left along row 8; the second runs left
        // along row 8 from the right edge and then down column 8 from the bottom. Writing these
        // transposed produces a symbol with perfect finders and timing that no reader will
        // decode, which is exactly what the first version of this did.
        for (var index = 0; index <= 5; index++)
        {
            modules[index, 8] = Bit(bits, index);
        }

        modules[7, 8] = Bit(bits, 6);
        modules[8, 8] = Bit(bits, 7);
        modules[8, 7] = Bit(bits, 8);
        for (var index = 9; index < 15; index++)
        {
            modules[8, 14 - index] = Bit(bits, index);
        }

        for (var index = 0; index < 8; index++)
        {
            modules[8, size - 1 - index] = Bit(bits, index);
        }

        for (var index = 8; index < 15; index++)
        {
            modules[size - 15 + index, 8] = Bit(bits, index);
        }

        modules[size - 8, 8] = true;
    }

    private static bool Bit(uint value, int index) => ((value >> index) & 1) == 1;

    private static void DrawCodewords(bool[,] modules, bool[,] reserved, byte[] codewords)
    {
        var size = modules.GetLength(0);
        var bit = 0;
        var total = codewords.Length * 8;
        // Two module columns at a time, right to left, alternating upward and downward, skipping
        // the timing column.
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (var step = 0; step < size; step++)
            {
                var upward = ((right + 1) & 2) == 0;
                var row = upward ? size - 1 - step : step;
                for (var offset = 0; offset < 2; offset++)
                {
                    var column = right - offset;
                    if (reserved[row, column])
                    {
                        continue;
                    }

                    modules[row, column] = bit < total && ((codewords[bit / 8] >> (7 - (bit % 8))) & 1) == 1;
                    bit++;
                }
            }
        }
    }

    private static int ChooseMask(bool[,] modules, bool[,] reserved, int version)
    {
        var best = 0;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, reserved, mask);
            DrawFormat(modules, mask);
            var penalty = Penalty(modules);
            ApplyMask(modules, reserved, mask);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = mask;
            }
        }

        _ = version;
        return best;
    }

    private static void ApplyMask(bool[,] modules, bool[,] reserved, int mask)
    {
        var size = modules.GetLength(0);
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (reserved[row, column] || !Masked(mask, row, column))
                {
                    continue;
                }

                modules[row, column] = !modules[row, column];
            }
        }
    }

    private static bool Masked(int mask, int row, int column) => mask switch
    {
        0 => (row + column) % 2 == 0,
        1 => row % 2 == 0,
        2 => column % 3 == 0,
        3 => (row + column) % 3 == 0,
        4 => (((row / 2) + (column / 3)) % 2) == 0,
        5 => ((row * column) % 2) + ((row * column) % 3) == 0,
        6 => ((((row * column) % 2) + ((row * column) % 3)) % 2) == 0,
        7 => ((((row + column) % 2) + ((row * column) % 3)) % 2) == 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };

    /// <summary>The four penalty rules of ISO/IEC 18004 clause 8.8.2, summed.</summary>
    private static int Penalty(bool[,] modules)
    {
        var size = modules.GetLength(0);
        var penalty = 0;
        var dark = 0;

        for (var line = 0; line < size; line++)
        {
            penalty += RunPenalty(modules, size, line, horizontal: true);
            penalty += RunPenalty(modules, size, line, horizontal: false);
        }

        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (modules[row, column])
                {
                    dark++;
                }

                if (row + 1 < size
                    && column + 1 < size
                    && modules[row, column] == modules[row, column + 1]
                    && modules[row, column] == modules[row + 1, column]
                    && modules[row, column] == modules[row + 1, column + 1])
                {
                    penalty += 3;
                }
            }
        }

        var percent = dark * 100 / (size * size);
        penalty += Math.Abs(percent - 50) / 5 * 10;
        return penalty;
    }

    private static int RunPenalty(bool[,] modules, int size, int line, bool horizontal)
    {
        var penalty = 0;
        var run = 1;
        var window = new bool[11];
        for (var index = 0; index < size; index++)
        {
            var value = horizontal ? modules[line, index] : modules[index, line];
            if (index > 0 && value == (horizontal ? modules[line, index - 1] : modules[index - 1, line]))
            {
                run++;
                if (run == 5)
                {
                    penalty += 3;
                }
                else if (run > 5)
                {
                    penalty++;
                }
            }
            else
            {
                run = 1;
            }

            Array.Copy(window, 1, window, 0, 10);
            window[10] = value;
            if (index >= 10 && (Matches(window, Finderish) || Matches(window, FinderishReversed)))
            {
                penalty += 40;
            }
        }

        return penalty;
    }

    // The 1:1:3:1:1 finder proportion appearing inside the data, which a decoder can mistake for
    // a real finder pattern.
    private static readonly bool[] Finderish =
        [true, false, true, true, true, false, true, false, false, false, false];

    private static readonly bool[] FinderishReversed =
        [false, false, false, false, true, false, true, true, true, false, true];

    private static bool Matches(bool[] window, bool[] pattern)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if (window[index] != pattern[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void Set(bool[,] modules, bool[,] reserved, int row, int column, bool dark)
    {
        modules[row, column] = dark;
        reserved[row, column] = true;
    }

    /// <summary>A bit stream written most significant bit first, as the specification reads it.</summary>
    private sealed class BitBuffer(int capacityBits)
    {
        private readonly byte[] _bytes = new byte[(capacityBits + 7) / 8];

        public int Length { get; private set; }

        public void Append(uint value, int bits)
        {
            for (var index = bits - 1; index >= 0; index--)
            {
                if (((value >> index) & 1) == 1)
                {
                    _bytes[Length / 8] |= (byte)(1 << (7 - (Length % 8)));
                }

                Length++;
            }
        }

        public byte[] ToBytes() => _bytes;
    }
}
