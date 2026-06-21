using System.Buffers.Binary;

namespace Cambrian.Api.Infrastructure;

public static class ImageUploadValidator
{
    public const int MaxDimension = 8192;
    public const long MaxPixels = 40_000_000;

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp",
        };

    public static bool IsSupported(string extension, string contentType) =>
        ContentTypes.TryGetValue(extension, out var expected)
        && string.Equals(expected, contentType, StringComparison.OrdinalIgnoreCase);

    public static async Task<byte[]> ReadAndValidateAsync(
        Stream input,
        string extension,
        string contentType,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported(extension, contentType))
            throw new ArgumentException("Image extension and MIME type do not match.");

        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new ArgumentException("Image must be under 10 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
            throw new ArgumentException("Image file is required.");

        var dimensions = extension.ToLowerInvariant() switch
        {
            ".png" => ReadPngDimensions(bytes),
            ".jpg" or ".jpeg" => ReadJpegDimensions(bytes),
            ".webp" => ReadWebpDimensions(bytes),
            _ => null,
        };

        if (dimensions is null)
            throw new ArgumentException("Image content does not match the declared file type.");

        var (width, height) = dimensions.Value;
        if (width <= 0 || height <= 0
            || width > MaxDimension || height > MaxDimension
            || (long)width * height > MaxPixels)
        {
            throw new ArgumentException(
                $"Image dimensions exceed the {MaxDimension}x{MaxDimension} and {MaxPixels:N0}-pixel limits.");
        }

        return bytes;
    }

    private static (int Width, int Height)? ReadPngDimensions(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature))
            return null;
        return (
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4)));
    }

    private static (int Width, int Height)? ReadJpegDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            return null;

        var offset = 2;
        while (offset + 8 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) return null;
            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9) continue;
            if (offset + 2 > bytes.Length) return null;

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset + length > bytes.Length) return null;

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3
                or 0xC5 or 0xC6 or 0xC7
                or 0xC9 or 0xCA or 0xCB
                or 0xCD or 0xCE or 0xCF)
            {
                if (length < 7) return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                return (width, height);
            }

            offset += length;
        }

        return null;
    }

    private static (int Width, int Height)? ReadWebpDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return null;

        var chunk = bytes.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            var width = 1 + ReadUInt24LittleEndian(bytes.Slice(24, 3));
            var height = 1 + ReadUInt24LittleEndian(bytes.Slice(27, 3));
            return (width, height);
        }

        if (chunk.SequenceEqual("VP8 "u8)
            && bytes.Length >= 30
            && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(28, 2)) & 0x3FFF;
            return (width, height);
        }

        if (chunk.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            var b0 = bytes[21];
            var b1 = bytes[22];
            var b2 = bytes[23];
            var b3 = bytes[24];
            var width = 1 + (((b2 & 0x3F) << 8) | b1);
            var height = 1 + ((b3 << 6) | (b2 >> 2));
            return (width, height);
        }

        return null;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}
