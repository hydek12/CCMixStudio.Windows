using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CCMixStudio.Windows.Core;

public static class AudioDecoder
{
    private static readonly int[] ImaSteps = [7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767];
    private static readonly int[] ImaIndex = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];
    private static readonly int[] Ws2Bit = [-2, -1, 0, 1];
    private static readonly int[] Ws4Bit = [-9, -8, -6, -5, -4, -3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 8];

    public static bool IsAUD(ReadOnlySpan<byte> data) => data.Length >= 20 &&
        BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) == 0x0000DEAF &&
        BinaryPrimitives.ReadUInt16LittleEndian(data) is >= 8000 and <= 48000 && data[11] is 1 or 99;

    public static byte[]? DecodeAUD(byte[] data)
    {
        if (!IsAUD(data)) return null;
        var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var channels = (ushort)((data[10] & 1) != 0 ? 2 : 1);
        var compression = data[11];
        var samples = new List<short>(data.Length * 2);
        var pos = 12;
        var predictor = 0;
        var stepIndex = 0;
        while (pos + 8 <= data.Length)
        {
            var compressed = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            var uncompressed = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            pos += 8;
            if (magic != 0x0000DEAF || pos + compressed > data.Length) break;
            var chunk = data.AsSpan(pos, compressed);
            pos += compressed;
            if (compression == 99)
            {
                foreach (var value in chunk)
                foreach (var shift in new[] { 0, 4 })
                {
                    var nibble = (value >> shift) & 15;
                    var step = ImaSteps[stepIndex];
                    var diff = step >> 3;
                    if ((nibble & 1) != 0) diff += step >> 2;
                    if ((nibble & 2) != 0) diff += step >> 1;
                    if ((nibble & 4) != 0) diff += step;
                    predictor = Math.Clamp(predictor + (((nibble & 8) != 0) ? -diff : diff), -32768, 32767);
                    stepIndex = Math.Clamp(stepIndex + ImaIndex[nibble], 0, 88);
                    samples.Add((short)predictor);
                }
            }
            else
            {
                var sample8 = 128;
                var cursor = 0;
                var produced = 0;
                while (cursor < chunk.Length && produced < uncompressed)
                {
                    var code = chunk[cursor++];
                    var mode = code >> 6;
                    var count = code & 63;
                    if (mode == 0)
                    {
                        for (var i = 0; i <= count && cursor < chunk.Length; i++)
                        {
                            var packed = chunk[cursor++];
                            for (var shift = 0; shift <= 6; shift += 2)
                            {
                                sample8 = Math.Clamp(sample8 + Ws2Bit[(packed >> shift) & 3], 0, 255);
                                samples.Add((short)((sample8 - 128) << 8)); produced++;
                            }
                        }
                    }
                    else if (mode == 1)
                    {
                        for (var i = 0; i <= count && cursor < chunk.Length; i++)
                        {
                            var packed = chunk[cursor++];
                            sample8 = Math.Clamp(sample8 + Ws4Bit[packed & 15], 0, 255); samples.Add((short)((sample8 - 128) << 8)); produced++;
                            sample8 = Math.Clamp(sample8 + Ws4Bit[packed >> 4], 0, 255); samples.Add((short)((sample8 - 128) << 8)); produced++;
                        }
                    }
                    else if (mode == 2 && (count & 0x20) != 0)
                    {
                        var delta = (sbyte)(code << 2) >> 2;
                        sample8 = Math.Clamp(sample8 + delta, 0, 255); samples.Add((short)((sample8 - 128) << 8)); produced++;
                    }
                    else if (mode == 2)
                    {
                        for (var i = 0; i <= count && cursor < chunk.Length; i++)
                        {
                            sample8 = chunk[cursor++]; samples.Add((short)((sample8 - 128) << 8)); produced++;
                        }
                    }
                    else
                    {
                        for (var i = 0; i <= count; i++) { samples.Add((short)((sample8 - 128) << 8)); produced++; }
                    }
                }
            }
        }
        return samples.Count == 0 ? null : BuildWav(samples, sampleRate, channels);
    }

    public static bool IsVQA(ReadOnlySpan<byte> data) => data.Length >= 20 && data[..4].SequenceEqual("FORM"u8) && data.Slice(8, 4).SequenceEqual("WVQA"u8);

    public static DecodedVqa? DecodeVQA(byte[] data)
    {
        if (!IsVQA(data)) return null;
        var width = 320; var height = 200; var fps = 15; var sampleRate = 22050; ushort channels = 1;
        var frames = new List<int>(); var sndChunks = new List<byte[]>();
        var palettes = new Dictionary<int, byte[]>(); var codebooks = new Dictionary<int, byte[]>();
        var palette = SpriteDecoder.BuiltInTemperatePalette; var codebook = Array.Empty<byte>();
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data, pos, 4);
            var size = ReadBE32(data, pos + 4);
            var payloadAt = pos + 8;
            if (size < 0 || (long)payloadAt + size > data.Length) break;
            var payload = data.AsSpan(payloadAt, size).ToArray();
            if (tag == "VQHD" && payload.Length >= 20)
            {
                width = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6)));
                height = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8)));
                fps = Math.Max(1, (int)payload[12]);
                var sr = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(16));
                if (sr is >= 8000 and <= 48000) sampleRate = sr;
                if (payload[18] is 1 or 2) channels = payload[18];
            }
            else if (tag == "CPAL" && payload.Length >= 768) palette = payload[..768];
            else if (tag == "CPLZ") palette = SpriteDecoder.DecompressLCW(payload, 768)[..768];
            else if (tag == "CBFZ") codebook = SpriteDecoder.DecompressLCW(payload, 262144);
            else if (tag is "CBF " or "CBFG") codebook = payload;
            else if (tag == "SND2") sndChunks.Add(payload);
            else if (tag == "VQFR")
            {
                frames.Add(pos);
                ReadVqfr(payload, ref palette, ref codebook);
                palettes[frames.Count - 1] = (byte[])palette.Clone();
                codebooks[frames.Count - 1] = (byte[])codebook.Clone();
            }
            pos = payloadAt + size + (size & 1);
        }
        if (frames.Count == 0) return null;
        var wav = sndChunks.Count == 0 ? null : DecodeImaChunks(sndChunks, sampleRate, channels);
        return new DecodedVqa(width, height, frames.Count, fps, frames.ToArray(), palettes, codebooks, wav);
    }

    private static void ReadVqfr(byte[] payload, ref byte[] palette, ref byte[] codebook)
    {
        var p = 0;
        while (p + 8 <= payload.Length)
        {
            var tag = Encoding.ASCII.GetString(payload, p, 4);
            var size = ReadBE32(payload, p + 4);
            var start = p + 8;
            if (size < 0 || (long)start + size > payload.Length) break;
            var sub = payload.AsSpan(start, size).ToArray();
            if (tag == "CPAL" && sub.Length >= 768) palette = sub[..768];
            else if (tag == "CPLZ") palette = SpriteDecoder.DecompressLCW(sub, 768);
            else if (tag == "CBFZ") codebook = SpriteDecoder.DecompressLCW(sub, 262144);
            else if (tag is "CBF " or "CBFG") codebook = sub;
            p = start + size + (size & 1);
        }
    }

    private static byte[]? DecodeImaChunks(IEnumerable<byte[]> chunks, int sampleRate, ushort channels)
    {
        var samples = new List<short>(); var predictor = 0; var stepIndex = 0;
        foreach (var chunk in chunks)
        foreach (var value in chunk)
        foreach (var shift in new[] { 0, 4 })
        {
            var nibble = (value >> shift) & 15; var step = ImaSteps[stepIndex]; var diff = step >> 3;
            if ((nibble & 1) != 0) diff += step >> 2;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 4) != 0) diff += step;
            predictor = Math.Clamp(predictor + (((nibble & 8) != 0) ? -diff : diff), -32768, 32767);
            stepIndex = Math.Clamp(stepIndex + ImaIndex[nibble], 0, 88); samples.Add((short)predictor);
        }
        return samples.Count == 0 ? null : BuildWav(samples, sampleRate, channels);
    }

    private static byte[] BuildWav(List<short> samples, int sampleRate, ushort channels)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8); writer.Write((uint)(36 + samples.Count * 2)); writer.Write("WAVE"u8); writer.Write("fmt "u8); writer.Write(16u);
        writer.Write((ushort)1); writer.Write(channels); writer.Write((uint)sampleRate); writer.Write((uint)(sampleRate * channels * 2)); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write((uint)(samples.Count * 2)); foreach (var sample in samples) writer.Write(sample);
        return output.ToArray();
    }

    private static int ReadBE32(byte[] data, int offset) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)));
}

public sealed record DecodedVqa(int Width, int Height, int FrameCount, int Fps, int[] FrameOffsets,
    IReadOnlyDictionary<int, byte[]> PaletteAtFrame, IReadOnlyDictionary<int, byte[]> CodebookAtFrame, byte[]? WavAudioData)
{
    public RasterImage RenderFrame(int index, byte[] data)
    {
        if (index < 0 || index >= FrameOffsets.Length) throw new ArgumentOutOfRangeException(nameof(index));
        const int tileWidth = 4, tileHeight = 2, tilePixels = 8;
        var columns = Math.Max(1, (Width + tileWidth - 1) / tileWidth);
        var rows = Math.Max(1, (Height + tileHeight - 1) / tileHeight);
        var tileCount = columns * rows;
        var rgba = new byte[checked(Width * Height * 4)];
        for (var frame = 0; frame <= index; frame++)
        {
            var start = FrameOffsets[frame];
            if (start + 8 > data.Length) continue;
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + 4)));
            var end = Math.Min(data.Length, start + 8 + size);
            var chunk = data.AsSpan(start, end - start).ToArray();
            var palette = Nearest(PaletteAtFrame, frame) ?? SpriteDecoder.BuiltInTemperatePalette;
            var codebook = Nearest(CodebookAtFrame, frame) ?? Array.Empty<byte>();
            var entries = Math.Max(1, codebook.Length / tilePixels);
            var vpt = FindPayload(chunk, ["VPTZ", "VPTG", "VPT "]);
            var indices = Array.Empty<int>();
            if (vpt is { } range)
            {
                var raw = chunk.AsSpan(range.Start, range.Length).ToArray();
                var tag = Encoding.ASCII.GetString(chunk, range.Start - 8, 4);
                var decoded = tag == "VPTZ" ? DecompressVqaChunk(raw, tileCount * 2) : raw;
                if (decoded.Length >= tileCount * 2)
                {
                    indices = new int[tileCount];
                    for (var i = 0; i < tileCount; i++) indices[i] = decoded[i * 2] | decoded[i * 2 + 1] << 8;
                }
                else indices = decoded.Select(value => (int)value).Take(tileCount).ToArray();
            }
            for (var tile = 0; tile < tileCount; tile++)
            {
                if (tile < indices.Length && indices[tile] == 0xFFFF) continue;
                var tileIndex = tile < indices.Length ? indices[tile] : tile;
                var codeOffset = tileIndex % entries * tilePixels;
                for (var py = 0; py < tileHeight; py++)
                for (var px = 0; px < tileWidth; px++)
                {
                    var x = tile % columns * tileWidth + px; var y = tile / columns * tileHeight + py;
                    if (x >= Width || y >= Height) continue;
                    var pos = codeOffset + py * tileWidth + px;
                    var paletteIndex = pos < codebook.Length ? codebook[pos] : chunk.Length == 0 ? 0 : chunk[(tile * 8 + py * tileWidth + px) % chunk.Length];
                    var p = Math.Min(255, paletteIndex) * 3; var dst = (y * Width + x) * 4;
                    rgba[dst] = (byte)Math.Min(255, palette[p] * 255 / 63);
                    rgba[dst + 1] = (byte)Math.Min(255, palette[p + 1] * 255 / 63);
                    rgba[dst + 2] = (byte)Math.Min(255, palette[p + 2] * 255 / 63);
                    rgba[dst + 3] = 255;
                }
            }
        }
        return new RasterImage(Width, Height, rgba);
    }

    private static byte[]? Nearest(IReadOnlyDictionary<int, byte[]> values, int index)
    {
        for (var frame = index; frame >= 0; frame--) if (values.TryGetValue(frame, out var value) && value.Length > 0) return value;
        return null;
    }

    private static (int Start, int Length)? FindPayload(byte[] data, string[] tags)
    {
        var pos = 8;
        while (pos + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data, pos, 4);
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4)));
            var payload = pos + 8;
            if ((long)payload + size > data.Length) break;
            if (tags.Contains(tag)) return (payload, size);
            pos = payload + size + (size & 1);
        }
        return null;
    }

    private static byte[] DecompressVqaChunk(byte[] source, int expected)
    {
        var direct = SpriteDecoder.DecompressLCW(source, expected);
        var directNonZero = direct.Count(value => value != 0);
        if (directNonZero > direct.Length / 16 || source.Length <= 4) return direct;
        var shifted = SpriteDecoder.DecompressLCW(source[4..], expected);
        return shifted.Count(value => value != 0) > directNonZero ? shifted : direct;
    }
}
