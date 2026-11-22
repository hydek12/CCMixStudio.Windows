using System.Globalization;
using System.IO;
using System.Text;

namespace CCMixStudio.Windows.Core;

public enum MixOutputFormat
{
    OpenRA,
    Auto,
    RA,
    Classic
}

public enum LocalMixDbPolicy
{
    Merge,
    Keep,
    Strip,
    Generate
}

public sealed class MixCombineOptions
{
    public DuplicateStrategy DuplicateStrategy { get; set; } = DuplicateStrategy.OverwriteLater;
    public MixOutputFormat Format { get; set; } = MixOutputFormat.OpenRA;
    public bool? IncludeRaHeaderFlagOverride { get; set; }
    public bool ExpandNestedMix { get; set; }
    public int? MaxNestedDepth { get; set; }
    public List<string> NestedFilters { get; set; } = [];
    public bool KeepNestedContainers { get; set; }
    public string? FilterQuery { get; set; }
    public List<string> IncludePatterns { get; set; } = [];
    public List<string> ExcludePatterns { get; set; } = [];
    public LocalMixDbPolicy LocalDbPolicy { get; set; } = LocalMixDbPolicy.Merge;
    public bool VerifyOutput { get; set; } = true;
}

public sealed record MixVerificationReport(
    string ArchiveName,
    bool ValidForWestwood,
    bool OpenRaCompatible,
    string FormatVariant,
    bool IsEncrypted,
    int EntryCount,
    long BodySize,
    int TotalSize,
    bool IsSortedByCrc,
    int DuplicateCrcCount,
    int NestedMixCount,
    bool HasLocalMixDatabase,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record MixCombineResult(
    byte[] Data,
    IReadOnlyList<MixEntry> MergedEntries,
    int DuplicatesResolved,
    int ExpandedNestedCount,
    bool UsedRaHeaderFlag,
    string LocalDbAction,
    IReadOnlyList<string> Warnings,
    MixVerificationReport? Verification);

public static class MixCombineEngine
{
    public static MixOutputFormat? ParseOutputFormat(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "openra" => MixOutputFormat.OpenRA,
            "auto" => MixOutputFormat.Auto,
            "ra" or "extended" or "ts" or "ra2" => MixOutputFormat.RA,
            "classic" or "td" => MixOutputFormat.Classic,
            _ => null
        };
    }

    public static LocalMixDbPolicy? ParseLocalDbPolicy(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "merge" => LocalMixDbPolicy.Merge,
            "keep" => LocalMixDbPolicy.Keep,
            "strip" or "none" or "remove" => LocalMixDbPolicy.Strip,
            "generate" or "gen" => LocalMixDbPolicy.Generate,
            _ => null
        };
    }

    public static MixCombineResult Combine(IReadOnlyList<LoadedMixArchive> inputArchives, MixCombineOptions? options = null)
    {
        options ??= new MixCombineOptions();
        if (inputArchives.Count == 0)
            throw new MixArchiveException("No input .MIX archives or files were provided.");

        if (options.Format == MixOutputFormat.OpenRA && options.DuplicateStrategy == DuplicateStrategy.KeepAll)
        {
            throw new MixArchiveException(
                "OpenRA-compatible .MIX archives cannot use 'keep-all' duplicate policy because duplicate CRC IDs conflict with OpenRA's package index. Use 'overwrite-later' or 'keep-first' (or pass --format ra / --format classic).");
        }

        var workingArchives = inputArchives.ToList();
        var expandedCount = 0;
        var warnings = new List<string>();

        if (options.ExpandNestedMix || options.NestedFilters.Count > 0)
        {
            var expanded = ExpandNestedArchives(
                workingArchives,
                options.MaxNestedDepth,
                options.NestedFilters,
                options.KeepNestedContainers);
            workingArchives = expanded.Archives;
            expandedCount = expanded.ExpandedCount;
            warnings.AddRange(expanded.Warnings);
        }

        var anyInputHadLocalDb = workingArchives.Any(archive =>
            archive.Entries.Any(entry => entry.CrcId == NameDatabase.LocalMixDatabaseCrc));

        var filteredArchives = workingArchives.Select(archive =>
        {
            var kept = archive.Entries.Where(entry =>
                MatchesFilters(entry, options.FilterQuery, options.IncludePatterns, options.ExcludePatterns)).ToList();
            return archive with { Entries = kept };
        }).ToList();

        var combined = new List<MixEntry>();
        var indexById = new Dictionary<uint, int>();
        var duplicates = 0;

        foreach (var archive in filteredArchives)
        {
            foreach (var entry in archive.Entries)
            {
                if (options.LocalDbPolicy == LocalMixDbPolicy.Strip && entry.CrcId == NameDatabase.LocalMixDatabaseCrc)
                    continue;

                if (indexById.TryGetValue(entry.CrcId, out var existingIdx))
                {
                    duplicates++;
                    switch (options.DuplicateStrategy)
                    {
                        case DuplicateStrategy.OverwriteLater:
                            combined[existingIdx] = entry;
                            break;
                        case DuplicateStrategy.KeepFirst:
                            continue;
                        case DuplicateStrategy.KeepAll:
                            combined.Add(entry);
                            break;
                    }
                }
                else
                {
                    indexById.Add(entry.CrcId, combined.Count);
                    combined.Add(entry);
                }
            }
        }

        var localDbAction = "none";
        var shouldRebuildLocalDb = options.LocalDbPolicy switch
        {
            LocalMixDbPolicy.Generate => true,
            LocalMixDbPolicy.Merge => anyInputHadLocalDb,
            _ => false
        };

        if (options.LocalDbPolicy == LocalMixDbPolicy.Strip)
        {
            localDbAction = "stripped";
        }
        else if (shouldRebuildLocalDb)
        {
            var nonDbEntries = combined.Where(entry => entry.CrcId != NameDatabase.LocalMixDatabaseCrc).ToList();
            var resolvedNames = new List<string>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in nonDbEntries)
            {
                if (entry.KnownName is { } known && seenNames.Add(known))
                    resolvedNames.Add(known);
            }

            if (resolvedNames.Count > 0)
            {
                const string lmdName = "local mix database.dat";
                if (seenNames.Add(lmdName))
                    resolvedNames.Add(lmdName);

                var lmdData = BuildLocalMixDatabase(resolvedNames);
                var lmdEntry = new MixEntry(
                    NameDatabase.LocalMixDatabaseCrc,
                    0,
                    checked((uint)lmdData.Length),
                    "CCMixStudio (Merged LMD)",
                    lmdData);

                combined = nonDbEntries;
                combined.Add(lmdEntry);
                localDbAction = $"merged ({resolvedNames.Count} names)";
            }
            else
            {
                combined = nonDbEntries;
                localDbAction = "skipped (no known names)";
            }
        }
        else if (combined.Any(entry => entry.CrcId == NameDatabase.LocalMixDatabaseCrc))
        {
            localDbAction = "kept";
        }

        combined.Sort((left, right) => left.CrcId.CompareTo(right.CrcId));

        var useRaHeaderFlag = ResolveUseRaHeaderFlag(workingArchives, combined.Count, options);

        if (!useRaHeaderFlag && combined.Count == 0)
        {
            throw new MixArchiveException(
                "Classic (Tiberian Dawn) .MIX format cannot encode 0 entries because a leading 0x0000 count is interpreted as an extended Red Alert header. Use --format openra or --format ra.");
        }

        if (combined.Count > ushort.MaxValue)
        {
            throw new MixArchiveException(
                $"Combined archive has {combined.Count} entries, exceeding the .MIX format maximum of 65,535 entries.");
        }

        var totalBodySize = combined.Sum(entry => (long)entry.Payload.Length);
        if (totalBodySize > uint.MaxValue)
        {
            throw new MixArchiveException(
                $"Combined payload size ({totalBodySize} bytes) exceeds the 4 GiB .MIX format limit.");
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        if (useRaHeaderFlag) writer.Write(0u);
        writer.Write((ushort)combined.Count);
        writer.Write((uint)totalBodySize);

        uint runningOffset = 0;
        var rebuiltEntries = new List<MixEntry>(combined.Count);
        foreach (var entry in combined)
        {
            var size = checked((uint)entry.Payload.Length);
            writer.Write(entry.CrcId);
            writer.Write(runningOffset);
            writer.Write(size);
            rebuiltEntries.Add(new MixEntry(entry.CrcId, runningOffset, size, entry.SourceArchive, entry.Payload));
            runningOffset = checked(runningOffset + size);
        }

        foreach (var entry in combined)
            writer.Write(entry.Payload);

        var bytes = output.ToArray();

        var remainingNested = rebuiltEntries.Count(entry => entry.IsNestedMix);
        if (remainingNested > 0 && options.Format == MixOutputFormat.OpenRA)
        {
            warnings.Add(
                $"{remainingNested} unexpanded nested .MIX archive(s) remain inside the output; OpenRA does not automatically flatten nested .MIX entries unless expanded with --expand-nested.");
        }

        MixVerificationReport? verification = null;
        if (options.VerifyOutput)
        {
            verification = Verify(bytes, "COMBINED.MIX", options.Format == MixOutputFormat.OpenRA);
            if (verification.Errors.Count > 0)
            {
                throw new MixArchiveException($"Archive verification failed: {string.Join("; ", verification.Errors)}");
            }
        }

        return new MixCombineResult(
            bytes,
            rebuiltEntries,
            duplicates,
            expandedCount,
            useRaHeaderFlag,
            localDbAction,
            warnings,
            verification);
    }

    public static (List<LoadedMixArchive> Archives, int ExpandedCount, List<string> Warnings) ExpandNestedArchives(
        IReadOnlyList<LoadedMixArchive> archives,
        int? maxDepth = null,
        IReadOnlyList<string>? targetFilters = null,
        bool keepContainers = false)
    {
        var resultArchives = new List<LoadedMixArchive>();
        var expandedCount = 0;
        var warnings = new List<string>();
        var visitedFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var filters = targetFilters ?? [];

        static string Fingerprint(byte[] data)
        {
            var prefixHex = Convert.ToHexString(data.AsSpan(0, Math.Min(32, data.Length)));
            return $"{data.Length}:{prefixHex}";
        }

        void Process(LoadedMixArchive archive, int depth)
        {
            var remainingEntries = new List<MixEntry>();
            var childArchives = new List<LoadedMixArchive>();
            var canExpandHere = !maxDepth.HasValue || depth < maxDepth.Value;

            foreach (var entry in archive.Entries)
            {
                if (!canExpandHere || !entry.IsNestedMix)
                {
                    remainingEntries.Add(entry);
                    continue;
                }

                var matchesTarget = filters.Count == 0 || filters.Any(filter => MatchesEntryIdentifier(entry, filter));
                if (!matchesTarget)
                {
                    remainingEntries.Add(entry);
                    continue;
                }

                var label = entry.KnownName ?? $"{entry.HexId}.MIX";
                var fp = Fingerprint(entry.Payload);
                if (!visitedFingerprints.Add(fp))
                {
                    warnings.Add($"Skipped duplicate/cyclic nested archive '{label}'.");
                    if (keepContainers) remainingEntries.Add(entry);
                    continue;
                }

                try
                {
                    var parsed = MixArchiveEngine.Parse(entry.Payload, label, expandedChild: true, sourceCrc: entry.CrcId);
                    expandedCount++;
                    if (keepContainers) remainingEntries.Add(entry);
                    childArchives.Add(parsed);
                }
                catch (Exception ex)
                {
                    visitedFingerprints.Remove(fp);
                    var explicitlyNamedMix =
                        entry.KnownName?.EndsWith(".MIX", StringComparison.OrdinalIgnoreCase) == true ||
                        filters.Count > 0;
                    if (explicitlyNamedMix)
                    {
                        throw new MixArchiveException($"Failed to expand nested .MIX '{label}': {ex.Message}");
                    }

                    warnings.Add($"Ignored non-archive entry {entry.DisplayLabel} that resembled a .MIX header: {ex.Message}");
                    remainingEntries.Add(entry);
                }
            }

            resultArchives.Add(archive with { Entries = remainingEntries });
            foreach (var child in childArchives)
                Process(child, depth + 1);
        }

        foreach (var archive in archives)
            Process(archive, 0);

        return (resultArchives, expandedCount, warnings);
    }

    public static MixCombineResult PackFiles(IReadOnlyList<string> filePaths, MixCombineOptions? options = null)
    {
        options ??= new MixCombineOptions();
        if (filePaths.Count == 0)
            throw new MixArchiveException("No input .MIX archives or files were provided.");

        var entries = new List<MixEntry>(filePaths.Count);
        var registeredNames = new List<string>(filePaths.Count);
        var totalSize = 0;

        foreach (var path in filePaths)
        {
            var data = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            var crc = MixArchiveEngine.WestwoodCrc(name);
            registeredNames.Add(name);
            entries.Add(new MixEntry(crc, checked((uint)totalSize), checked((uint)data.Length), name, data));
            totalSize = checked(totalSize + data.Length);
        }

        NameDatabase.Shared.RegisterExact(registeredNames);

        var syntheticArchive = new LoadedMixArchive(
            Guid.NewGuid(),
            "PACKED_INPUT",
            totalSize,
            options.Format == MixOutputFormat.Classic ? MixFormatVariant.TiberianDawn : MixFormatVariant.RedAlertTS,
            false,
            false,
            null,
            entries);

        return Combine([syntheticArchive], options);
    }

    public static byte[] BuildLocalMixDatabase(IReadOnlyList<string> filenames, uint gameId = 1)
    {
        var cleaned = filenames
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .ToList();

        using var payloadStream = new MemoryStream();
        foreach (var name in cleaned)
        {
            var encoded = Encoding.Latin1.GetBytes(name);
            payloadStream.Write(encoded);
            payloadStream.WriteByte(0);
        }

        var payload = payloadStream.ToArray();
        var totalSize = checked((uint)(52 + payload.Length));

        using var output = new MemoryStream((int)totalSize);
        using var writer = new BinaryWriter(output);
        writer.Write(Encoding.ASCII.GetBytes("XCC by Olaf van der Spek"));
        writer.Write([0x1A, 0x04, 0x17, 0x27, 0x10, 0x19, 0x80, 0x00]);
        writer.Write(totalSize);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(gameId);
        writer.Write((uint)cleaned.Count);
        writer.Write(payload);
        return output.ToArray();
    }

    public static MixVerificationReport Verify(byte[] data, string name, bool strictOpenRa = true)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            var parsed = MixArchiveEngine.Parse(data, name);
            var isSorted = true;
            var seenCrcs = new HashSet<uint>();
            var duplicateCrcs = 0;
            uint previousCrc = 0;

            for (var i = 0; i < parsed.Entries.Count; i++)
            {
                var entry = parsed.Entries[i];
                if (i > 0 && entry.CrcId < previousCrc)
                    isSorted = false;
                previousCrc = entry.CrcId;
                if (!seenCrcs.Add(entry.CrcId))
                    duplicateCrcs++;
            }

            if (!isSorted)
                errors.Add("Index entries are not sorted in ascending CRC order.");

            if (duplicateCrcs > 0)
            {
                var msg = $"Archive contains {duplicateCrcs} duplicate CRC ID(s).";
                if (strictOpenRa)
                    errors.Add(msg);
                else
                    warnings.Add(msg);
            }

            if (parsed.WasEncrypted)
            {
                warnings.Add(
                    "Archive header is RSA/Blowfish encrypted (valid for retail RA/TS, though unencrypted is recommended for custom OpenRA packs).");
            }

            var nestedCount = parsed.Entries.Count(entry => entry.IsNestedMix);
            if (nestedCount > 0)
            {
                warnings.Add($"Archive contains {nestedCount} nested .MIX sub-archive(s) that were not flattened.");
            }

            var hasLmd = parsed.Entries.Any(entry => entry.CrcId == NameDatabase.LocalMixDatabaseCrc);
            var bodySize = parsed.Entries.Sum(entry => (long)entry.Size);

            return new MixVerificationReport(
                name,
                isSorted,
                errors.Count == 0,
                FormatVariantLabel(parsed.Variant),
                parsed.WasEncrypted,
                parsed.Entries.Count,
                bodySize,
                data.Length,
                isSorted,
                duplicateCrcs,
                nestedCount,
                hasLmd,
                errors,
                warnings);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new MixVerificationReport(
                name,
                false,
                false,
                "Invalid",
                false,
                0,
                0,
                data.Length,
                false,
                0,
                0,
                false,
                errors,
                warnings);
        }
    }

    public static MixEntry ResolveEntry(IReadOnlyList<MixEntry> entries, string query)
    {
        var exactMatches = entries.Where(entry => MatchesEntryIdentifier(entry, query)).ToList();
        if (exactMatches.Count == 1) return exactMatches[0];
        if (exactMatches.Count > 1)
        {
            throw new MixArchiveException(
                $"Entry '{query}' matched multiple entries: {string.Join(", ", exactMatches.Select(e => $"{e.DisplayLabel} [{e.SourceArchive}]"))}. Specify an exact 0xCRC ID or full filename.");
        }

        var fuzzyMatches = entries.Where(entry => MatchesPattern(entry, query)).ToList();
        if (fuzzyMatches.Count == 1) return fuzzyMatches[0];
        if (fuzzyMatches.Count > 1)
        {
            throw new MixArchiveException(
                $"Entry '{query}' matched multiple entries: {string.Join(", ", fuzzyMatches.Select(e => $"{e.DisplayLabel} [{e.SourceArchive}]"))}. Specify an exact 0xCRC ID or full filename.");
        }

        throw new MixArchiveException($"Entry '{query}' was not found.");
    }

    public static bool MatchesEntryIdentifier(MixEntry entry, string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return false;

        if (TryParseHexCrc(trimmed, out var parsedCrc) && parsedCrc == entry.CrcId)
            return true;
        if (entry.KnownName is not null && string.Equals(entry.KnownName, trimmed, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(entry.SuggestedFilename, trimmed, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(entry.DisplayLabel, trimmed, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static bool MatchesFilters(
        MixEntry entry,
        string? filterQuery,
        IReadOnlyList<string> includePatterns,
        IReadOnlyList<string> excludePatterns)
    {
        if (!string.IsNullOrWhiteSpace(filterQuery))
        {
            var q = filterQuery.Trim();
            var matchesQuery =
                entry.DisplayLabel.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.SourceArchive.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.DetectedType.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.SuggestedFilename.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (!matchesQuery) return false;
        }

        if (includePatterns.Count > 0 && !includePatterns.Any(pattern => MatchesPattern(entry, pattern)))
            return false;

        if (excludePatterns.Any(pattern => MatchesPattern(entry, pattern)))
            return false;

        return true;
    }

    public static bool MatchesPattern(MixEntry entry, string pattern)
    {
        var trimmed = pattern.Trim();
        if (trimmed.Length == 0) return false;

        if (TryParseHexCrc(trimmed, out var crc) && crc == entry.CrcId)
            return true;

        if (trimmed.Contains('*') || trimmed.Contains('?'))
        {
            var candidates = new[] { entry.KnownName, entry.SuggestedFilename, entry.HexId, entry.SourceArchive };
            return candidates.Any(c => c is not null && GlobMatch(trimmed, c));
        }

        return entry.DisplayLabel.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
               entry.SuggestedFilename.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
               entry.SourceArchive.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
               entry.DetectedType.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseHexCrc(string text, out uint crc)
    {
        crc = 0;
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        else if (s.Length != 8)
            return false;

        return s.Length is > 0 and <= 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out crc);
    }

    public static string FormatVariantLabel(MixFormatVariant variant) => variant switch
    {
        MixFormatVariant.TiberianDawn => "Tiberian Dawn (Classic)",
        MixFormatVariant.RedAlertTS => "Red Alert / TS / RA2",
        MixFormatVariant.RedAlertTSEncrypted => "Red Alert / TS / RA2 (Decrypted)",
        _ => variant.ToString()
    };

    private static bool GlobMatch(string pattern, string text)
    {
        var p = pattern.ToUpperInvariant();
        var t = text.ToUpperInvariant();
        int pi = 0, ti = 0, matchIdx = 0;
        int? starIdx = null;

        while (ti < t.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || p[pi] == t[ti]))
            {
                pi++;
                ti++;
            }
            else if (pi < p.Length && p[pi] == '*')
            {
                starIdx = pi;
                matchIdx = ti;
                pi++;
            }
            else if (starIdx.HasValue)
            {
                pi = starIdx.Value + 1;
                matchIdx++;
                ti = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (pi < p.Length && p[pi] == '*')
            pi++;
        return pi == p.Length;
    }

    private static bool ResolveUseRaHeaderFlag(
        IReadOnlyList<LoadedMixArchive> archives,
        int entryCount,
        MixCombineOptions options)
    {
        if (options.IncludeRaHeaderFlagOverride is { } explicitOverride)
            return explicitOverride;

        return options.Format switch
        {
            MixOutputFormat.OpenRA or MixOutputFormat.RA => true,
            MixOutputFormat.Classic => false,
            MixOutputFormat.Auto => entryCount == 0 || archives.Any(a => a.Variant is MixFormatVariant.RedAlertTS or MixFormatVariant.RedAlertTSEncrypted),
            _ => true
        };
    }
}
