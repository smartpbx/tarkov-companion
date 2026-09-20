namespace TarkovCompanion.App.Services.V2.Team;

/// <summary>
/// Reed-Solomon error-correction codewords over GF(256), as QR symbols use them.
/// </summary>
/// <remarks>
/// The field is GF(2^8) with the primitive polynomial 0x11D, which is the one ISO/IEC 18004
/// names. Written for that one purpose: a fixed field, a generator built by multiplying out
/// (x - a^i), and the remainder of the message polynomial divided by it.
/// </remarks>
internal static class ReedSolomon
{
    private static readonly byte[] Exponents = new byte[512];
    private static readonly byte[] Logarithms = new byte[256];

    static ReedSolomon()
    {
        var value = 1;
        for (var index = 0; index < 255; index++)
        {
            Exponents[index] = (byte)value;
            Logarithms[value] = (byte)index;
            value <<= 1;
            if (value >= 256)
            {
                value ^= 0x11D;
            }
        }

        // Doubled so a product's exponent never needs a modulo.
        for (var index = 255; index < 512; index++)
        {
            Exponents[index] = Exponents[index - 255];
        }
    }

    /// <summary>The error-correction codewords for one block.</summary>
    public static byte[] Remainder(byte[] data, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        var generator = Generator(count);
        var remainder = new byte[count];
        foreach (var codeword in data)
        {
            var factor = (byte)(codeword ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, count - 1);
            remainder[count - 1] = 0;
            if (factor == 0)
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                remainder[index] ^= Multiply(generator[index], factor);
            }
        }

        return remainder;
    }

    private static byte[] Generator(int count)
    {
        var generator = new byte[count];
        generator[count - 1] = 1;
        var root = 1;
        for (var degree = 0; degree < count; degree++)
        {
            for (var index = 0; index < count; index++)
            {
                generator[index] = Multiply(generator[index], (byte)root);
                if (index + 1 < count)
                {
                    generator[index] ^= generator[index + 1];
                }
            }

            root = Multiply((byte)root, 2);
        }

        return generator;
    }

    private static byte Multiply(byte left, byte right) =>
        left == 0 || right == 0 ? (byte)0 : Exponents[Logarithms[left] + Logarithms[right]];
}
