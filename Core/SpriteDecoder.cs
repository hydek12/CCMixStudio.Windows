using System.Buffers.Binary;

namespace CCMixStudio.Windows.Core;

public sealed record RasterImage(int Width, int Height, byte[] Rgba);

public sealed record DecodedSprite(int Width, int Height, IReadOnlyList<byte[]> Frames)
{
    public int FrameCount => Frames.Count;

    public RasterImage RenderFrame(int frameIndex, byte[] palette)
    {
        if (frameIndex < 0 || frameIndex >= Frames.Count || palette.Length < 768) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return Colorize(Width, Height, Frames[frameIndex], palette);
    }

    public RasterImage RenderSpriteSheet(byte[] palette, int? columns = null)
    {
        var cols = Math.Max(1, columns ?? (int)Math.Ceiling(Math.Sqrt(FrameCount)));
        var rows = (FrameCount + cols - 1) / cols;
        var width = checked(cols * Width);
        var height = checked(rows * Height);
        var output = new byte[checked(width * height * 4)];
        for (var frame = 0; frame < FrameCount; frame++)
        {
            var pixels = Frames[frame];
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var index = pixels[y * Width + x];
                if (index == 0) continue;
                var src = index * 3;
                var dst = ((frame / cols * Height + y) * width + frame % cols * Width + x) * 4;
                output[dst] = Scale(palette[src]);
                output[dst + 1] = Scale(palette[src + 1]);
                output[dst + 2] = Scale(palette[src + 2]);
                output[dst + 3] = 255;
            }
        }
        return new RasterImage(width, height, output);
    }

    private RasterImage Colorize(int width, int height, byte[] indices, byte[] palette)
    {
        var rgba = new byte[checked(width * height * 4)];
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            if (index == 0) continue;
            var source = index * 3;
            var destination = i * 4;
            rgba[destination] = Scale(palette[source]);
            rgba[destination + 1] = Scale(palette[source + 1]);
            rgba[destination + 2] = Scale(palette[source + 2]);
            rgba[destination + 3] = 255;
        }
        return new RasterImage(width, height, rgba);
    }

    private static byte Scale(byte channel) => (byte)Math.Min(255, channel * 255 / 63);
}

public static class SpriteDecoder
{
    public static byte[] BuiltInTemperatePalette { get; } = CreatePalette(false);
    public static byte[] BuiltInSnowPalette { get; } = CreatePalette(true);

    public static bool IsSHP(ReadOnlySpan<byte> data)
    {
        if (data.Length < 22) return false;
        var count = Read16(data, 0);
        var width = Read16(data, 6);
        var height = Read16(data, 8);
        if (count is < 1 or > 2048 || width is < 1 or > 1024 || height is < 1 or > 1024) return false;
        var tableEnd = 14 + (count + 2) * 8;
        if (data.Length < tableEnd) return false;
        var raw = Read32(data, 14);
        var offset = (int)(raw & 0x00FFFFFF);
        var format = (byte)(raw >> 24);
        return offset == tableEnd && format is 0x80 or 0x40 or 0x20;
    }

    public static DecodedSprite? Decode(byte[] data)
    {
        if (!IsSHP(data)) return null;
        var frameCount = Read16(data, 0);
        var width = Read16(data, 6);
        var height = Read16(data, 8);
        var pixelsPerFrame = checked(width * height);
        var entries = new FrameIndex[frameCount + 1];
        for (var i = 0; i <= frameCount; i++)
        {
            var offset = 14 + i * 8;
            var raw = Read32(data, offset);
            var reference = Read32(data, offset + 4);
            entries[i] = new FrameIndex((int)(raw & 0x00FFFFFF), (byte)(raw >> 24), (int)(reference & 0x00FFFFFF));
        }

        var frames = Enumerable.Range(0, frameCount).Select(_ => new byte[pixelsPerFrame]).ToArray();
        var byOffset = entries.Take(frameCount).Select((entry, index) => (entry.Offset, index)).ToDictionary(item => item.Offset, item => item.index);
        for (var i = 0; i < frameCount; i++)
        {
            var current = entries[i];
            var nextOffset = entries[i + 1].Offset;
            if (current.Offset < 0 || nextOffset < current.Offset || nextOffset > data.Length) continue;
            var source = data.AsSpan(current.Offset, nextOffset - current.Offset).ToArray();
            if (current.Format == 0x80)
            {
                frames[i] = DecompressLCW(source, pixelsPerFrame);
            }
            else if (current.Format is 0x40 or 0x20)
            {
                var baseFrame = i > 0 ? frames[i - 1] : new byte[pixelsPerFrame];
                if (current.Format == 0x40 && byOffset.TryGetValue(current.ReferenceOffset, out var referenceIndex) && referenceIndex < i)
                    baseFrame = frames[referenceIndex];
                frames[i] = ApplyXorDelta(source, baseFrame);
            }
        }
        return new DecodedSprite(width, height, frames);
    }

    public static byte[] DecompressLCW(byte[] source, int expectedSize)
    {
        var destination = new byte[expectedSize];
        var sp = 0;
        var dp = 0;
        while (sp < source.Length && dp < expectedSize)
        {
            var command = source[sp++];
            if ((command & 0x80) == 0)
            {
                if (sp >= source.Length) break;
                var relative = ((command & 0x0F) << 8) | source[sp++];
                CopyRelative(destination, ref dp, expectedSize, dp - relative, (command >> 4 & 7) + 3);
            }
            else if ((command & 0x40) == 0)
            {
                var count = command & 0x3F;
                if (count == 0) break;
                count = Math.Min(count, Math.Min(source.Length - sp, expectedSize - dp));
                Array.Copy(source, sp, destination, dp, count);
                sp += count;
                dp += count;
            }
            else if (command == 0xFE)
            {
                if (source.Length - sp < 3) break;
                var count = source[sp] | source[sp + 1] << 8;
                var value = source[sp + 2]; sp += 3;
                count = Math.Min(count, expectedSize - dp);
                Array.Fill(destination, value, dp, count); dp += count;
            }
            else if (command == 0xFF)
            {
                if (source.Length - sp < 4) break;
                var count = source[sp] | source[sp + 1] << 8;
                var position = source[sp + 2] | source[sp + 3] << 8; sp += 4;
                CopyAbsolute(destination, ref dp, expectedSize, position, count);
            }
            else
            {
                if (source.Length - sp < 2) break;
                var position = source[sp] | source[sp + 1] << 8; sp += 2;
                CopyAbsolute(destination, ref dp, expectedSize, position, (command & 0x3F) + 3);
            }
        }
        return destination;
    }

    private static byte[] ApplyXorDelta(byte[] source, byte[] baseFrame)
    {
        var destination = (byte[])baseFrame.Clone();
        var sp = 0;
        var dp = 0;
        while (sp < source.Length && dp < destination.Length)
        {
            var command = source[sp++];
            if ((command & 0x80) == 0)
            {
                if (command == 0)
                {
                    if (source.Length - sp < 2) break;
                    var count = source[sp++]; var value = source[sp++];
                    for (var i = 0; i < count && dp < destination.Length; i++) destination[dp++] ^= value;
                }
                else
                {
                    for (var i = 0; i < command && sp < source.Length && dp < destination.Length; i++) destination[dp++] ^= source[sp++];
                }
            }
            else
            {
                var low = command & 0x7F;
                if (low != 0) { dp = Math.Min(destination.Length, dp + low); continue; }
                if (source.Length - sp < 2) break;
                var word = source[sp] | source[sp + 1] << 8; sp += 2;
                if (word == 0) break;
                if ((word & 0x8000) == 0) dp = Math.Min(destination.Length, dp + word);
                else if ((word & 0x4000) == 0)
                {
                    var count = word & 0x3FFF;
                    for (var i = 0; i < count && sp < source.Length && dp < destination.Length; i++) destination[dp++] ^= source[sp++];
                }
                else
                {
                    if (sp >= source.Length) break;
                    var count = word & 0x3FFF; var value = source[sp++];
                    for (var i = 0; i < count && dp < destination.Length; i++) destination[dp++] ^= value;
                }
            }
        }
        return destination;
    }

    private static void CopyRelative(byte[] destination, ref int dp, int expectedSize, int source, int count)
    {
        for (var i = 0; i < count && dp < expectedSize; i++)
        {
            var index = source + i;
            destination[dp] = index >= 0 && index < dp ? destination[index] : (byte)0;
            dp++;
        }
    }

    private static void CopyAbsolute(byte[] destination, ref int dp, int expectedSize, int source, int count)
    {
        for (var i = 0; i < count && dp < expectedSize; i++)
        {
            var index = source + i;
            destination[dp++] = index >= 0 && index < expectedSize ? destination[index] : (byte)0;
        }
    }

    private static byte[] CreatePalette(bool snow)
    {
        var result = new byte[768];
        for (var i = 1; i < 256; i++)
        {
            var b = i * 3;
            double t;
            if (!snow)
            {
                if (i < 16) { t = i / 15.0; result[b] = (byte)(12 + t * 51); result[b + 1] = (byte)(10 + t * 44); result[b + 2] = (byte)(2 + t * 10); }
                else if (i < 32) { var v = (byte)(6 + (i - 16) * 3); result[b] = result[b + 1] = result[b + 2] = v; }
                else if (i < 48) { t = (i - 32) / 15.0; result[b] = (byte)(14 + t * 49); result[b + 1] = (byte)(2 + t * 10); result[b + 2] = (byte)(2 + t * 10); }
                else if (i < 80) { t = (i - 48) / 31.0; result[b] = (byte)(6 + t * 20); result[b + 1] = (byte)(12 + t * 34); result[b + 2] = (byte)(4 + t * 14); }
                else if (i < 112) { t = (i - 80) / 31.0; result[b] = (byte)(14 + t * 28); result[b + 1] = (byte)(10 + t * 20); result[b + 2] = (byte)(5 + t * 11); }
                else if (i < 144) { t = (i - 112) / 31.0; result[b] = (byte)(4 + t * 16); result[b + 1] = (byte)(10 + t * 28); result[b + 2] = (byte)(20 + t * 43); }
                else { t = (i - 144) / 111.0; result[b] = (byte)(10 + t * 50); result[b + 1] = (byte)(10 + t * 48); result[b + 2] = (byte)(8 + t * 42); }
            }
            else if (i < 16) { t = i / 15.0; result[b] = (byte)(12 + t * 51); result[b + 1] = (byte)(10 + t * 44); result[b + 2] = (byte)(2 + t * 10); }
            else if (i < 48) { t = (i - 16) / 31.0; result[b] = (byte)(24 + t * 39); result[b + 1] = (byte)(28 + t * 35); result[b + 2] = (byte)(34 + t * 29); }
            else if (i < 80) { t = (i - 48) / 31.0; result[b] = (byte)(5 + t * 16); result[b + 1] = (byte)(14 + t * 28); result[b + 2] = (byte)(10 + t * 20); }
            else if (i < 112) { t = (i - 80) / 31.0; result[b] = (byte)(12 + t * 22); result[b + 1] = (byte)(10 + t * 18); result[b + 2] = (byte)(8 + t * 16); }
            else { t = (i - 112) / 143.0; result[b] = (byte)(18 + t * 45); result[b + 1] = (byte)(20 + t * 43); result[b + 2] = (byte)(24 + t * 39); }
        }
        return result;
    }

    private sealed record FrameIndex(int Offset, byte Format, int ReferenceOffset);
    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
