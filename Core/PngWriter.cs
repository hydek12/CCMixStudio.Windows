using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace CCMixStudio.Windows.Core;

public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] EncodeRgba(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(image), "Image dimensions must be positive.");
        if (image.Rgba.Length < image.Width * image.Height * 4)
            throw new ArgumentException("RGBA buffer is smaller than expected dimensions.", nameof(image));

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], image.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter: adaptive
        ihdr[12] = 0; // interlace: none
        WriteChunk(output, "IHDR"u8, ihdr);

        var rowStride = image.Width * 4;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                var scanline = new byte[rowStride + 1];
                for (var y = 0; y < image.Height; y++)
                {
                    scanline[0] = 0; // Filter type 0 (None)
                    Buffer.BlockCopy(image.Rgba, y * rowStride, scanline, 1, rowStride);
                    zlib.Write(scanline, 0, scanline.Length);
                }
            }
            WriteChunk(output, "IDAT"u8, compressed.ToArray());
        }

        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        stream.Write(lenBytes);
        stream.Write(type);
        if (!data.IsEmpty)
            stream.Write(data);

        Span<byte> crcBytes = stackalloc byte[4];
        var crc = ComputeCrc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc;
    }
}
