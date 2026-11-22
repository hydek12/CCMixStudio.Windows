using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace CCMixStudio.Windows.Core;

public static class MixArchiveEngine
{
    private static readonly byte[] PublicModulus = Convert.FromBase64String("AihRvNoIbTn85FZRYNZRcT+i6KpU+maCsEqr3Q5q+LDB5tH7Tz2qQ38V")[2..];

    public static bool LooksLikeMixArchive(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18) return false;
        var count16 = U16(data, 0);
        if (count16 != 0)
        {
            var headerEnd = 6L + count16 * 12L;
            if (data.Length < headerEnd) return false;
            var bodySize = U32(data, 2);
            return headerEnd + bodySize == data.Length || headerEnd + bodySize + 20 == data.Length;
        }

        var flags = U32(data, 0);
        if ((flags & ~0x00030000u) != 0) return false;
        if ((flags & 0x00020000u) != 0) return data.Length >= 92;
        var count = U16(data, 4);
        var end = 10L + count * 12L;
        if (count == 0 || data.Length < end) return false;
        var size = U32(data, 6);
        return end + size == data.Length || end + size + 20 == data.Length;
    }

    public static LoadedMixArchive Parse(string path, bool expandedChild = false, uint? sourceCrc = null)
    {
        var data = File.ReadAllBytes(path);
        return Parse(data, Path.GetFileName(path), expandedChild, sourceCrc);
    }

    public static LoadedMixArchive Parse(byte[] data, string name, bool expandedChild = false, uint? sourceCrc = null)
    {
        if (data.Length < 6) throw new MixArchiveException("File is too small to be a valid Westwood .MIX archive.");
        var first = U16(data, 0);
        if (first != 0)
            return ParseHeader(data, data, 0, checked(6 + first * 12), name, MixFormatVariant.TiberianDawn, false, expandedChild, sourceCrc);

        if (data.Length < 10) throw new MixArchiveException("File is too small to be a valid Westwood .MIX archive.");
        var flags = U32(data, 0);
        var encrypted = (flags & 0x00020000u) != 0;
        if (!encrypted)
        {
            var count = U16(data, 4);
            return ParseHeader(data, data, 4, checked(10 + count * 12), name, MixFormatVariant.RedAlertTS, false, expandedChild, sourceCrc);
        }

        if (data.Length < 92) throw new MixArchiveException("Encrypted .MIX archive is too small to contain its key and header.");
        var keyBlock = data.AsSpan(4, 80).ToArray();
        var firstCipher = data.AsSpan(84, 8).ToArray();
        var mode = FindBlowfishMode(keyBlock, firstCipher, data.Length)
            ?? throw new MixArchiveException($"Could not decrypt the RSA/Blowfish header in {name}.");
        var firstPlain = DecryptBlowfish(firstCipher, mode);
        var countEntries = U16(firstPlain, 0);
        var rawHeaderLength = 6 + countEntries * 12;
        var paddedLength = (rawHeaderLength + 7) / 8 * 8;
        var encryptedEnd = 84 + paddedLength;
        if (data.Length < encryptedEnd) throw new MixArchiveException($"{name} encrypted header extends past EOF.");
        var cipherHeader = data.AsSpan(84, paddedLength).ToArray();
        var plainHeader = DecryptBlowfish(cipherHeader, mode);
        return ParseHeader(data, plainHeader, 0, encryptedEnd, name, MixFormatVariant.RedAlertTSEncrypted, true, expandedChild, sourceCrc);
    }

    private static LoadedMixArchive ParseHeader(byte[] file, byte[] header, int headerOffset, int bodyStart, string name,
        MixFormatVariant variant, bool encrypted, bool expandedChild, uint? sourceCrc)
    {
        if (header.Length < headerOffset + 6) throw new MixArchiveException($"Corrupt .MIX header in {name}.");
        var count = U16(header, headerOffset);
        var bodySize = U32(header, headerOffset + 2);
        var indexStart = headerOffset + 6;
        if ((long)header.Length < indexStart + (long)count * 12) throw new MixArchiveException($"{name} claims too many entries for its header.");
        if ((long)bodyStart + bodySize > (long)file.Length + 20) throw new MixArchiveException($"{name} body extends past EOF.");

        var entries = new List<MixEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var at = indexStart + i * 12;
            var crc = U32(header, at);
            var relative = U32(header, at + 4);
            var size = U32(header, at + 8);
            var start = (long)bodyStart + relative;
            var end = start + size;
            if (start < bodyStart || end > file.Length) throw new MixArchiveException($"Entry 0x{crc:X8} in {name} points outside the archive.");
            var payload = file.AsSpan((int)start, (int)size).ToArray();
            if (crc == NameDatabase.LocalMixDatabaseCrc) NameDatabase.Shared.ImportBytes(payload);
            entries.Add(new MixEntry(crc, relative, size, name, payload));
        }

        return new LoadedMixArchive(Guid.NewGuid(), name, file.Length, variant, encrypted, expandedChild, sourceCrc, entries);
    }

    public static MergeResult Merge(IReadOnlyList<LoadedMixArchive> archives, DuplicateStrategy strategy, bool includeRaFlag)
    {
        var combined = new List<MixEntry>();
        var indexById = new Dictionary<uint, int>();
        var duplicates = 0;
        foreach (var archive in archives)
        foreach (var entry in archive.Entries)
        {
            if (indexById.TryGetValue(entry.CrcId, out var existing))
            {
                duplicates++;
                if (strategy == DuplicateStrategy.OverwriteLater) combined[existing] = entry;
                else if (strategy == DuplicateStrategy.KeepAll) combined.Add(entry);
            }
            else
            {
                indexById.Add(entry.CrcId, combined.Count);
                combined.Add(entry);
            }
        }

        combined.Sort((left, right) => left.CrcId.CompareTo(right.CrcId));
        var bodySize = combined.Sum(entry => (long)entry.Payload.Length);
        if (combined.Count > ushort.MaxValue || bodySize > uint.MaxValue) throw new MixArchiveException("The merged archive exceeds the .MIX format limits.");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        if (includeRaFlag) writer.Write(0u);
        writer.Write((ushort)combined.Count);
        writer.Write((uint)bodySize);
        uint offset = 0;
        foreach (var entry in combined)
        {
            writer.Write(entry.CrcId);
            writer.Write(offset);
            writer.Write((uint)entry.Payload.Length);
            offset = checked(offset + (uint)entry.Payload.Length);
        }
        foreach (var entry in combined) writer.Write(entry.Payload);
        return new MergeResult(output.ToArray(), combined, duplicates);
    }

    public static int BatchExtract(IEnumerable<MixEntry> entries, string directory)
    {
        Directory.CreateDirectory(directory);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry.SuggestedFilename);
            if (!names.Add(name))
            {
                name = $"{entry.HexId}_{count}_{name}";
                names.Add(name);
            }
            File.WriteAllBytes(Path.Combine(directory, name), entry.Payload);
            count++;
        }
        return count;
    }

    public static uint WestwoodCrc(string filename)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(filename.ToUpperInvariant()).ToList();
        while (bytes.Count % 4 != 0) bytes.Add(0);
        uint crc = 0;
        for (var i = 0; i < bytes.Count; i += 4)
        {
            var chunk = (uint)(bytes[i] | bytes[i + 1] << 8 | bytes[i + 2] << 16 | bytes[i + 3] << 24);
            crc = BitOperations.RotateLeft(crc, 1) + chunk;
        }
        return crc;
    }

    private sealed record BlowfishMode(byte[] Key, bool SwapKeyWords, bool SwapDataWords);

    private static BlowfishMode? FindBlowfishMode(byte[] keyBlock, byte[] firstCipher, int totalSize)
    {
        var mod = new BigInteger(PublicModulus, true, true);
        var exponent = new BigInteger(65537);
        var dec1 = BigInteger.ModPow(new BigInteger(keyBlock.AsSpan(0, 40), true, true), exponent, mod).ToByteArray(true, true);
        var dec2 = BigInteger.ModPow(new BigInteger(keyBlock.AsSpan(40, 40), true, true), exponent, mod).ToByteArray(true, true);
        var dec1Reversed = BigInteger.ModPow(new BigInteger(keyBlock.AsSpan(0, 40).ToArray().Reverse().ToArray(), true, true), exponent, mod).ToByteArray(true, true);
        var dec2Reversed = BigInteger.ModPow(new BigInteger(keyBlock.AsSpan(40, 40).ToArray().Reverse().ToArray(), true, true), exponent, mod).ToByteArray(true, true);
        dec1 = Pad40(dec1); dec2 = Pad40(dec2); dec1Reversed = Pad40(dec1Reversed); dec2Reversed = Pad40(dec2Reversed);

        var keys = new[]
        {
            dec1.Skip(1).Concat(dec2.Skip(1)).Take(56).ToArray(),
            dec1.Reverse().Take(39).Concat(dec2.Reverse().Take(39)).Take(56).ToArray(),
            dec1Reversed.Reverse().Take(39).Concat(dec2Reversed.Reverse().Take(39)).Take(56).ToArray(),
            dec1Reversed.Skip(1).Concat(dec2Reversed.Skip(1)).Take(56).ToArray()
        };
        foreach (var key in keys)
        foreach (var swapKey in new[] { false, true })
        foreach (var swapData in new[] { false, true })
        {
            var mode = new BlowfishMode(key, swapKey, swapData);
            try
            {
                var plain = DecryptBlowfish(firstCipher, mode);
                var fileCount = U16(plain, 0);
                var bodySize = U32(plain, 2);
                if (fileCount == 0 || fileCount >= 32768) continue;
                var expected = 84L + ((6L + fileCount * 12 + 7) / 8 * 8) + bodySize;
                if (expected == totalSize || expected + 20 == totalSize) return mode;
            }
            catch (Exception) { }
        }
        return null;
    }

    private static byte[] DecryptBlowfish(byte[] data, BlowfishMode mode)
    {
        if (data.Length % 8 != 0 || mode.Key.Length == 0 || mode.Key.Length % 4 != 0) throw new MixArchiveException("Invalid Blowfish input.");
        var key = mode.SwapKeyWords ? SwapWords(mode.Key) : mode.Key;
        var input = mode.SwapDataWords ? SwapWords(data) : data;
        var output = new byte[input.Length];
        var cipher = new BlowfishEngine();
        cipher.Init(false, new KeyParameter(key));
        for (var offset = 0; offset < input.Length; offset += 8) cipher.ProcessBlock(input, offset, output, offset);
        return mode.SwapDataWords ? SwapWords(output) : output;
    }

    private static byte[] Pad40(byte[] value)
    {
        if (value.Length >= 40) return value[^40..];
        return new byte[40 - value.Length].Concat(value).ToArray();
    }

    private static byte[] SwapWords(byte[] bytes)
    {
        var result = (byte[])bytes.Clone();
        for (var i = 0; i + 3 < result.Length; i += 4) Array.Reverse(result, i, 4);
        return result;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
