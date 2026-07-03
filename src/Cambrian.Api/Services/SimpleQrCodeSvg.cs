using System.Text;

namespace Cambrian.Api.Services;

/// <summary>
/// Minimal QR Code SVG encoder for Cambrian verification URLs.
/// Supports byte-mode QR version 5, error correction L, mask 0.
/// </summary>
internal static class SimpleQrCodeSvg
{
    private const int Version = 5;
    private const int Size = 21 + 4 * (Version - 1);
    private const int DataCodewords = 108;
    private const int EccCodewords = 26;
    private const int QuietZone = 4;

    public static string Create(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        if (payload.Length > 106)
            throw new InvalidOperationException("Verification URL is too long for the built-in QR encoder.");

        var modules = new bool[Size, Size];
        var isFunction = new bool[Size, Size];

        DrawFunctionPatterns(modules, isFunction);
        DrawFormatBits(modules, isFunction);

        var data = EncodeData(payload);
        var ecc = ReedSolomonRemainder(data, ReedSolomonDivisor(EccCodewords));
        var allCodewords = data.Concat(ecc).ToArray();
        DrawCodewords(allCodewords, modules, isFunction);

        return ToSvg(modules);
    }

    private static byte[] EncodeData(byte[] payload)
    {
        var bits = new List<bool>();
        AppendBits(bits, 0b0100, 4); // byte mode
        AppendBits(bits, payload.Length, 8);
        foreach (var b in payload)
            AppendBits(bits, b, 8);

        var dataCapacityBits = DataCodewords * 8;
        var terminator = Math.Min(4, dataCapacityBits - bits.Count);
        AppendBits(bits, 0, terminator);
        while (bits.Count % 8 != 0)
            bits.Add(false);

        var result = new List<byte>();
        for (var i = 0; i < bits.Count; i += 8)
        {
            var value = 0;
            for (var j = 0; j < 8; j++)
                value = (value << 1) | (bits[i + j] ? 1 : 0);
            result.Add((byte)value);
        }

        for (var pad = 0; result.Count < DataCodewords; pad++)
            result.Add((byte)(pad % 2 == 0 ? 0xEC : 0x11));

        return result.ToArray();
    }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction)
    {
        DrawFinder(modules, isFunction, 3, 3);
        DrawFinder(modules, isFunction, 3, Size - 4);
        DrawFinder(modules, isFunction, Size - 4, 3);

        for (var i = 8; i <= Size - 9; i++)
        {
            var dark = i % 2 == 0;
            SetFunction(modules, isFunction, 6, i, dark);
            SetFunction(modules, isFunction, i, 6, dark);
        }

        DrawAlignment(modules, isFunction, 30, 30);
        SetFunction(modules, isFunction, 4 * Version + 9, 8, true);
    }

    private static void DrawFinder(bool[,] modules, bool[,] isFunction, int centerRow, int centerCol)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var row = centerRow + dy;
                var col = centerCol + dx;
                if (row < 0 || row >= Size || col < 0 || col >= Size)
                    continue;

                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                var dark = distance is 0 or 1 or 3;
                SetFunction(modules, isFunction, row, col, dark);
            }
        }
    }

    private static void DrawAlignment(bool[,] modules, bool[,] isFunction, int centerRow, int centerCol)
    {
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(modules, isFunction, centerRow + dy, centerCol + dx, distance is 0 or 2);
            }
        }
    }

    private static void DrawFormatBits(bool[,] modules, bool[,] isFunction)
    {
        const int errorCorrectionLevelL = 0b01;
        const int mask = 0;
        var data = (errorCorrectionLevelL << 3) | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
            rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
        var bits = ((data << 10) | (rem & 0x3FF)) ^ 0x5412;

        for (var i = 0; i <= 5; i++)
            SetFunction(modules, isFunction, 8, i, GetBit(bits, i));
        SetFunction(modules, isFunction, 8, 7, GetBit(bits, 6));
        SetFunction(modules, isFunction, 8, 8, GetBit(bits, 7));
        SetFunction(modules, isFunction, 7, 8, GetBit(bits, 8));
        for (var i = 9; i < 15; i++)
            SetFunction(modules, isFunction, 14 - i, 8, GetBit(bits, i));

        for (var i = 0; i < 8; i++)
            SetFunction(modules, isFunction, Size - 1 - i, 8, GetBit(bits, i));
        for (var i = 8; i < 15; i++)
            SetFunction(modules, isFunction, 8, Size - 15 + i, GetBit(bits, i));
        SetFunction(modules, isFunction, Size - 8, 8, true);
    }

    private static void DrawCodewords(byte[] codewords, bool[,] modules, bool[,] isFunction)
    {
        var bits = new List<bool>(codewords.Length * 8);
        foreach (var codeword in codewords)
            AppendBits(bits, codeword, 8);

        var bitIndex = 0;
        var upward = true;
        for (var right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right--;

            for (var vertical = 0; vertical < Size; vertical++)
            {
                var row = upward ? Size - 1 - vertical : vertical;
                for (var j = 0; j < 2; j++)
                {
                    var col = right - j;
                    if (isFunction[row, col])
                        continue;

                    var dark = bitIndex < bits.Count && bits[bitIndex];
                    if ((row + col) % 2 == 0)
                        dark = !dark;

                    modules[row, col] = dark;
                    bitIndex++;
                }
            }

            upward = !upward;
        }
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        byte root = 1;

        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = GaloisMultiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }
            root = GaloisMultiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];

        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++)
                result[i] ^= GaloisMultiply(divisor[i], factor);
        }

        return result;
    }

    private static byte GaloisMultiply(byte x, byte y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ (((z >> 7) & 1) * 0x11D);
            if (((y >> i) & 1) != 0)
                z ^= x;
        }
        return (byte)z;
    }

    private static void SetFunction(bool[,] modules, bool[,] isFunction, int row, int col, bool dark)
    {
        modules[row, col] = dark;
        isFunction[row, col] = true;
    }

    private static void AppendBits(List<bool> bits, int value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
            bits.Add(((value >> i) & 1) != 0);
    }

    private static bool GetBit(int value, int index) => ((value >> index) & 1) != 0;

    private static string ToSvg(bool[,] modules)
    {
        var dimension = Size + QuietZone * 2;
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ");
        sb.Append(dimension);
        sb.Append(' ');
        sb.Append(dimension);
        sb.Append("\" shape-rendering=\"crispEdges\"><rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/><path d=\"");

        for (var row = 0; row < Size; row++)
        {
            for (var col = 0; col < Size; col++)
            {
                if (!modules[row, col])
                    continue;

                sb.Append('M');
                sb.Append(col + QuietZone);
                sb.Append(',');
                sb.Append(row + QuietZone);
                sb.Append("h1v1h-1z");
            }
        }

        sb.Append("\" fill=\"#111111\"/></svg>");
        return sb.ToString();
    }
}
