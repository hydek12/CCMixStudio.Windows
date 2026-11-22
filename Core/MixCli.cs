using System.IO;
using System.Text;
using System.Text.Json;

namespace CCMixStudio.Windows.Core;

public sealed class AppLaunchConfiguration
{
    public List<string> InputPaths { get; } = [];
    public List<string> NameDatabasePaths { get; } = [];
    public DuplicateStrategy DuplicateStrategy { get; set; } = DuplicateStrategy.OverwriteLater;
    public bool? UseRaHeaderFlag { get; set; }
    public bool ExpandNested { get; set; }

    public static AppLaunchConfiguration Shared { get; set; } = new();
}

public sealed class CliIO
{
    public Action<string> WriteStdout { get; }
    public Action<string> WriteStderr { get; }
    public Action<byte[]> WriteRawStdout { get; }

    public CliIO(Action<string> writeStdout, Action<string> writeStderr, Action<byte[]>? writeRawStdout = null)
    {
        WriteStdout = writeStdout;
        WriteStderr = writeStderr;
        WriteRawStdout = writeRawStdout ?? (bytes => writeStdout(Encoding.Latin1.GetString(bytes)));
    }

    public static CliIO Standard { get; } = new(
        text => Console.Out.Write(text),
        text => Console.Error.Write(text),
        bytes =>
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        });

    public void Out(string line) => WriteStdout(line + Environment.NewLine);
    public void Err(string line) => WriteStderr(line + Environment.NewLine);
}

internal sealed class CliParseException : Exception
{
    public string? HelpTopic { get; }
    public bool IsHelpRequest => HelpTopic is not null || string.IsNullOrEmpty(Message);

    private CliParseException(string message, string? helpTopic) : base(message)
    {
        HelpTopic = helpTopic;
    }

    public static CliParseException InvalidArguments(string message) => new(message, null);
    public static CliParseException HelpRequested(string? topic) => new(string.Empty, topic ?? string.Empty);
}

public static class MixCli
{
    public const string Version = "1.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool ShouldRunCli(string[] arguments)
    {
        if (arguments.Length == 0)
            return false;

        var first = arguments[0];
        if (string.Equals(first, "--gui", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(first, "gui", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static AppLaunchConfiguration ParseGuiLaunchConfiguration(string[] arguments)
    {
        var config = new AppLaunchConfiguration();
        for (var i = 0; i < arguments.Length; i++)
        {
            var arg = arguments[i];
            if (string.Equals(arg, "--gui", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "gui", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (arg == "--ra-header")
            {
                config.UseRaHeaderFlag = true;
            }
            else if (arg == "--no-ra-header")
            {
                config.UseRaHeaderFlag = false;
            }
            else if (arg is "--expand-nested" or "--flatten")
            {
                config.ExpandNested = true;
            }
            else if (arg == "--duplicates" && i + 1 < arguments.Length)
            {
                i++;
                if (ParseDuplicateStrategy(arguments[i]) is { } parsed)
                    config.DuplicateStrategy = parsed;
            }
            else if (arg == "--names" && i + 1 < arguments.Length)
            {
                i++;
                config.NameDatabasePaths.Add(arguments[i]);
            }
            else if (!arg.StartsWith('-'))
            {
                config.InputPaths.Add(arg);
            }
        }
        return config;
    }

    public static int Run(string[] arguments, CliIO? io = null)
    {
        io ??= CliIO.Standard;
        if (arguments.Length == 0)
        {
            io.Out(GlobalHelpText);
            return 0;
        }

        var command = arguments[0];
        var rest = arguments.Skip(1).ToArray();
        var cmdLower = command.ToLowerInvariant();

        try
        {
            switch (cmdLower)
            {
                case "-h":
                case "--help":
                case "help":
                    io.Out(rest.Length > 0 ? HelpTextFor(rest[0].ToLowerInvariant()) : GlobalHelpText);
                    return 0;
                case "-v":
                case "--version":
                case "version":
                    io.Out($"CCMixStudio.Windows {Version}");
                    return 0;
                case "combine":
                case "merge":
                    RunCombine(rest, io);
                    return 0;
                case "pack":
                    RunPack(rest, io);
                    return 0;
                case "inspect":
                case "info":
                    RunInspect(rest, io);
                    return 0;
                case "list":
                case "ls":
                    RunList(rest, io);
                    return 0;
                case "extract":
                    RunExtract(rest, io);
                    return 0;
                case "cat":
                case "preview":
                    RunCat(rest, io);
                    return 0;
                case "export-shp":
                    RunExportShp(rest, io);
                    return 0;
                case "export-aud":
                    RunExportAud(rest, io);
                    return 0;
                case "export-vqa":
                    RunExportVqa(rest, io);
                    return 0;
                case "crc":
                    RunCrc(rest, io);
                    return 0;
                case "dict":
                    RunDict(rest, io);
                    return 0;
                case "verify":
                    RunVerify(rest, io);
                    return 0;
                default:
                    throw CliParseException.InvalidArguments(
                        $"Unknown command '{command}'. Run 'CCMixStudio.Windows --help' for usage, or pass '--gui' to launch the desktop window.");
            }
        }
        catch (CliParseException parseEx) when (parseEx.IsHelpRequest)
        {
            io.Out(HelpTextFor(string.IsNullOrEmpty(parseEx.HelpTopic) ? null : parseEx.HelpTopic));
            return 0;
        }
        catch (CliParseException parseEx)
        {
            io.Err($"Error: {parseEx.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            io.Err($"Error: {ex.Message}");
            return 1;
        }
    }

    // MARK: - combine

    private static void RunCombine(string[] args, CliIO io)
    {
        var mixInputs = new List<string>();
        var looseFiles = new List<string>();
        var looseDirs = new List<string>();
        var recursiveLoose = false;
        string? outputPath = null;
        var namePaths = new List<string>();
        var options = new MixCombineOptions();
        var dryRun = false;
        var jsonOutput = false;
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    throw CliParseException.HelpRequested("combine");
                case "-o":
                case "--output":
                    outputPath = NextValue(args, ref i, arg);
                    break;
                case "-i":
                case "--input":
                    mixInputs.Add(NextValue(args, ref i, arg));
                    break;
                case "--add-file":
                    looseFiles.Add(NextValue(args, ref i, arg));
                    break;
                case "--add-dir":
                    looseDirs.Add(NextValue(args, ref i, arg));
                    break;
                case "-r":
                case "--recursive":
                    recursiveLoose = true;
                    break;
                case "--duplicates":
                case "--duplicate-policy":
                {
                    var val = NextValue(args, ref i, arg);
                    options.DuplicateStrategy = ParseDuplicateStrategy(val)
                        ?? throw CliParseException.InvalidArguments(
                            $"Invalid duplicate policy '{val}'. Expected: overwrite-later, keep-first, keep-all.");
                    break;
                }
                case "--format":
                {
                    var val = NextValue(args, ref i, arg);
                    options.Format = MixCombineEngine.ParseOutputFormat(val)
                        ?? throw CliParseException.InvalidArguments(
                            $"Invalid format '{val}'. Expected: openra, auto, ra, classic.");
                    break;
                }
                case "--ra-header":
                    options.IncludeRaHeaderFlagOverride = true;
                    break;
                case "--no-ra-header":
                    options.IncludeRaHeaderFlagOverride = false;
                    break;
                case "--expand-nested":
                case "--flatten":
                    options.ExpandNestedMix = true;
                    break;
                case "--expand":
                    options.NestedFilters.Add(NextValue(args, ref i, arg));
                    break;
                case "--expand-depth":
                {
                    var raw = NextValue(args, ref i, arg);
                    if (!int.TryParse(raw, out var depth) || depth < 0)
                        throw CliParseException.InvalidArguments($"Invalid --expand-depth '{raw}'. Expected a non-negative integer.");
                    options.MaxNestedDepth = depth;
                    options.ExpandNestedMix = true;
                    break;
                }
                case "--keep-nested-containers":
                    options.KeepNestedContainers = true;
                    break;
                case "--filter":
                    options.FilterQuery = NextValue(args, ref i, arg);
                    break;
                case "--include":
                    options.IncludePatterns.Add(NextValue(args, ref i, arg));
                    break;
                case "--exclude":
                    options.ExcludePatterns.Add(NextValue(args, ref i, arg));
                    break;
                case "--names":
                    namePaths.Add(NextValue(args, ref i, arg));
                    break;
                case "--local-db":
                {
                    var val = NextValue(args, ref i, arg);
                    options.LocalDbPolicy = MixCombineEngine.ParseLocalDbPolicy(val)
                        ?? throw CliParseException.InvalidArguments(
                            $"Invalid --local-db policy '{val}'. Expected: merge, keep, strip, generate.");
                    break;
                }
                case "--verify":
                    options.VerifyOutput = true;
                    break;
                case "--no-verify":
                    options.VerifyOutput = false;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "-q":
                case "--quiet":
                    quiet = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw CliParseException.InvalidArguments($"Unknown option '{arg}' for 'combine'.");
                    mixInputs.Add(arg);
                    break;
            }
        }

        if (!dryRun && string.IsNullOrEmpty(outputPath))
            throw CliParseException.InvalidArguments("Missing required output path (-o / --output <output.mix>).");

        ImportNameDatabases(namePaths);

        var archives = LoadArchives(mixInputs);
        var extraFiles = CollectInputFiles(looseFiles, looseDirs, recursiveLoose);
        if (extraFiles.Count > 0)
            archives.Add(BuildSyntheticArchive(extraFiles));

        var result = MixCombineEngine.Combine(archives, options);

        if (!dryRun && outputPath is not null)
        {
            EnsureParentDirectory(outputPath);
            WriteAllBytesAtomic(outputPath, result.Data);
        }

        if (jsonOutput)
        {
            var payload = new
            {
                outputPath,
                dryRun,
                inputArchiveCount = mixInputs.Count + (extraFiles.Count == 0 ? 0 : 1),
                expandedNestedCount = result.ExpandedNestedCount,
                entryCount = result.MergedEntries.Count,
                duplicatesResolved = result.DuplicatesResolved,
                byteSize = result.Data.Length,
                usedRAHeaderFlag = result.UsedRaHeaderFlag,
                format = options.Format.ToString().ToLowerInvariant(),
                localDBAction = result.LocalDbAction,
                warnings = result.Warnings,
                verification = result.Verification
            };
            io.Out(EncodeJson(payload));
            return;
        }

        if (!quiet)
        {
            var targetLabel = dryRun ? "[dry-run]" : (outputPath ?? "COMBINED.MIX");
            io.Out($"Combined -> {targetLabel}");
            io.Out($"  Entries: {result.MergedEntries.Count} ({result.DuplicatesResolved} duplicate(s) resolved via {FormatDuplicateLabel(options.DuplicateStrategy)})");
            io.Out($"  Size: {result.Data.Length} bytes ({FormatBytes(result.Data.Length)})");
            io.Out($"  Header: {(result.UsedRaHeaderFlag ? "Red Alert / Extended (4-byte 0x00000000 flags)" : "Tiberian Dawn / Classic (6-byte)")} [mode: {options.Format.ToString().ToLowerInvariant()}]");
            if (result.ExpandedNestedCount > 0)
                io.Out($"  Expanded nested .MIX archives: {result.ExpandedNestedCount}");
            io.Out($"  Local Mix Database: {result.LocalDbAction}");
            if (result.Verification is { } v)
                io.Out($"  OpenRA Compatible: {(v.OpenRaCompatible ? "YES" : "NO")} (sorted={v.IsSortedByCrc.ToString().ToLowerInvariant()}, duplicateCRCs={v.DuplicateCrcCount})");
            foreach (var warning in result.Warnings)
                io.Err($"Warning: {warning}");
        }
    }

    // MARK: - pack

    private static void RunPack(string[] args, CliIO io)
    {
        var inputFiles = new List<string>();
        var inputDirs = new List<string>();
        var recursive = false;
        string? outputPath = null;
        var namePaths = new List<string>();
        var options = new MixCombineOptions { LocalDbPolicy = LocalMixDbPolicy.Generate };
        var dryRun = false;
        var jsonOutput = false;
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    throw CliParseException.HelpRequested("pack");
                case "-o":
                case "--output":
                    outputPath = NextValue(args, ref i, arg);
                    break;
                case "-r":
                case "--recursive":
                    recursive = true;
                    break;
                case "--duplicates":
                {
                    var val = NextValue(args, ref i, arg);
                    options.DuplicateStrategy = ParseDuplicateStrategy(val)
                        ?? throw CliParseException.InvalidArguments($"Invalid duplicate policy '{val}'.");
                    break;
                }
                case "--format":
                {
                    var val = NextValue(args, ref i, arg);
                    options.Format = MixCombineEngine.ParseOutputFormat(val)
                        ?? throw CliParseException.InvalidArguments($"Invalid format '{val}'.");
                    break;
                }
                case "--ra-header":
                    options.IncludeRaHeaderFlagOverride = true;
                    break;
                case "--no-ra-header":
                    options.IncludeRaHeaderFlagOverride = false;
                    break;
                case "--local-db":
                {
                    var val = NextValue(args, ref i, arg);
                    options.LocalDbPolicy = MixCombineEngine.ParseLocalDbPolicy(val)
                        ?? throw CliParseException.InvalidArguments($"Invalid --local-db policy '{val}'.");
                    break;
                }
                case "--names":
                    namePaths.Add(NextValue(args, ref i, arg));
                    break;
                case "--verify":
                    options.VerifyOutput = true;
                    break;
                case "--no-verify":
                    options.VerifyOutput = false;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "-q":
                case "--quiet":
                    quiet = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw CliParseException.InvalidArguments($"Unknown option '{arg}' for 'pack'.");
                    if (Directory.Exists(arg))
                        inputDirs.Add(arg);
                    else
                        inputFiles.Add(arg);
                    break;
            }
        }

        if (!dryRun && string.IsNullOrEmpty(outputPath))
            throw CliParseException.InvalidArguments("Missing required output path (-o / --output <output.mix>).");

        ImportNameDatabases(namePaths);
        var files = CollectInputFiles(inputFiles, inputDirs, recursive);
        var result = MixCombineEngine.PackFiles(files, options);

        if (!dryRun && outputPath is not null)
        {
            EnsureParentDirectory(outputPath);
            WriteAllBytesAtomic(outputPath, result.Data);
        }

        if (jsonOutput)
        {
            io.Out(EncodeJson(new
            {
                outputPath,
                dryRun,
                packedFileCount = files.Count,
                entryCount = result.MergedEntries.Count,
                byteSize = result.Data.Length,
                usedRAHeaderFlag = result.UsedRaHeaderFlag,
                verification = result.Verification
            }));
            return;
        }

        if (!quiet)
        {
            io.Out($"Packed {files.Count} file(s) -> {(dryRun ? "[dry-run]" : (outputPath ?? ""))}");
            io.Out($"  Entries: {result.MergedEntries.Count} • Size: {result.Data.Length} bytes ({FormatBytes(result.Data.Length)})");
            io.Out($"  OpenRA Compatible: {(result.Verification?.OpenRaCompatible == true ? "YES" : "unverified")}");
        }
    }

    // MARK: - inspect

    private static void RunInspect(string[] args, CliIO io)
    {
        var parsed = ParseArchiveQueryArgs(args, "inspect", allowShowEntries: true);
        ImportNameDatabases(parsed.NamePaths);
        var loaded = LoadArchives(parsed.InputPaths);
        var (archives, expandedCount) = PrepareArchivesForQuery(loaded, parsed);

        var hasRa = archives.Any(a => a.Variant is MixFormatVariant.RedAlertTS or MixFormatVariant.RedAlertTSEncrypted);
        var merged = MixArchiveEngine.Merge(archives, parsed.DuplicateStrategy, hasRa);
        var filtered = FilterEntries(merged.Entries, parsed);

        if (parsed.JsonOutput)
        {
            var payload = new
            {
                archives = archives.Select(a => new
                {
                    fileName = a.FileName,
                    fileSize = a.FileSize,
                    variant = MixCombineEngine.FormatVariantLabel(a.Variant),
                    wasEncrypted = a.WasEncrypted,
                    isExpandedChild = a.IsExpandedChild,
                    entryCount = a.Entries.Count,
                    nestedMixCount = a.Entries.Count(e => e.IsNestedMix)
                }),
                expandedNestedCount = expandedCount,
                mergedEntryCount = merged.Entries.Count,
                filteredEntryCount = filtered.Count,
                duplicatesResolved = merged.DuplicatesResolved,
                mergedByteSize = merged.Data.Length,
                dictionaryCount = NameDatabase.Shared.Count,
                entries = parsed.ShowEntries ? filtered.Select(ToEntryJson).ToList() : null
            };
            io.Out(EncodeJson(payload));
            return;
        }

        io.Out($"Archives ({archives.Count}):");
        foreach (var arch in archives)
        {
            var nested = arch.Entries.Count(e => e.IsNestedMix);
            var enc = arch.WasEncrypted ? " [Encrypted Header]" : "";
            var child = arch.IsExpandedChild ? " [Expanded Nested]" : "";
            var nestedText = nested > 0 ? $", {nested} nested .MIX" : "";
            io.Out($"  - {arch.FileName}: {arch.Entries.Count} entries, {FormatBytes(arch.FileSize)}, {MixCombineEngine.FormatVariantLabel(arch.Variant)}{enc}{child}{nestedText}");
        }
        io.Out($"Merge Preview: {merged.Entries.Count} entries, {FormatBytes(merged.Data.Length)}, {merged.DuplicatesResolved} duplicate(s) resolved ({FormatDuplicateLabel(parsed.DuplicateStrategy)})");
        io.Out($"Global Name Dictionary: {NameDatabase.Shared.Count} known names");

        if (parsed.ShowEntries)
        {
            io.Out("");
            PrintEntryTable(filtered, io);
        }
    }

    // MARK: - list

    private static void RunList(string[] args, CliIO io)
    {
        var parsed = ParseArchiveQueryArgs(args, "list", allowRawList: true);
        ImportNameDatabases(parsed.NamePaths);
        var loaded = LoadArchives(parsed.InputPaths);
        var (archives, _) = PrepareArchivesForQuery(loaded, parsed);

        IReadOnlyList<MixEntry> baseEntries = parsed.RawList
            ? archives.SelectMany(a => a.Entries).ToList()
            : MixArchiveEngine.Merge(archives, parsed.DuplicateStrategy, false).Entries;

        var filtered = FilterEntries(baseEntries, parsed);

        if (parsed.JsonOutput)
        {
            io.Out(EncodeJson(filtered.Select(ToEntryJson).ToList()));
            return;
        }

        PrintEntryTable(filtered, io);
        io.Out($"Total: {filtered.Count} entry(s)");
    }

    // MARK: - extract

    private static void RunExtract(string[] args, CliIO io)
    {
        string? outputPath = null;
        var entryQueries = new List<string>();
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-o" or "--output")
                outputPath = NextValue(args, ref i, arg);
            else if (arg == "--entry")
                entryQueries.Add(NextValue(args, ref i, arg));
            else
                remaining.Add(arg);
        }

        var parsed = ParseArchiveQueryArgs(remaining.ToArray(), "extract");
        if (string.IsNullOrEmpty(outputPath))
            throw CliParseException.InvalidArguments("Missing required output path (-o / --output <dir-or-file>).");

        ImportNameDatabases(parsed.NamePaths);
        var loaded = LoadArchives(parsed.InputPaths);
        var (archives, _) = PrepareArchivesForQuery(loaded, parsed);
        var merged = MixArchiveEngine.Merge(archives, parsed.DuplicateStrategy, false);

        var entriesToExtract = entryQueries.Count > 0
            ? entryQueries.Select(q => MixCombineEngine.ResolveEntry(merged.Entries, q)).ToList()
            : FilterEntries(merged.Entries, parsed);

        if (entriesToExtract.Count == 0)
            throw new MixArchiveException("No matching entries to extract.");

        var endsWithSlash = outputPath.EndsWith(Path.DirectorySeparatorChar) || outputPath.EndsWith(Path.AltDirectorySeparatorChar);
        if (entriesToExtract.Count == 1 && entryQueries.Count > 0 && !Directory.Exists(outputPath) && !endsWithSlash)
        {
            var single = entriesToExtract[0];
            EnsureParentDirectory(outputPath);
            WriteAllBytesAtomic(outputPath, single.Payload);
            if (parsed.JsonOutput)
            {
                io.Out(EncodeJson(new { extracted = new[] { Path.GetFullPath(outputPath) }, count = 1 }));
            }
            else
            {
                io.Out($"Extracted {single.DisplayLabel} -> {outputPath} ({single.Payload.Length} bytes)");
            }
            return;
        }

        Directory.CreateDirectory(outputPath);
        var count = MixArchiveEngine.BatchExtract(entriesToExtract, outputPath);

        if (parsed.JsonOutput)
        {
            io.Out(EncodeJson(new { outputDirectory = Path.GetFullPath(outputPath), extractedCount = count }));
        }
        else
        {
            io.Out($"Extracted {count} file(s) to {outputPath}");
        }
    }

    // MARK: - cat

    private static void RunCat(string[] args, CliIO io)
    {
        string? entryQuery = null;
        var forceHex = false;
        var rawOutput = false;
        var maxBytes = 65_536;
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--entry":
                    entryQuery = NextValue(args, ref i, arg);
                    break;
                case "--hex":
                    forceHex = true;
                    break;
                case "--raw":
                    rawOutput = true;
                    break;
                case "--max-bytes":
                {
                    var val = NextValue(args, ref i, arg);
                    if (!int.TryParse(val, out maxBytes) || maxBytes <= 0)
                        throw CliParseException.InvalidArguments($"Invalid --max-bytes '{val}'.");
                    break;
                }
                default:
                    remaining.Add(arg);
                    break;
            }
        }

        var parsed = ParseArchiveQueryArgs(remaining.ToArray(), "cat");
        if (string.IsNullOrEmpty(entryQuery))
            throw CliParseException.InvalidArguments("Missing required --entry <name-or-crc> for 'cat'.");

        ImportNameDatabases(parsed.NamePaths);
        var loaded = LoadArchives(parsed.InputPaths);
        var (archives, _) = PrepareArchivesForQuery(loaded, parsed);
        var merged = MixArchiveEngine.Merge(archives, parsed.DuplicateStrategy, false);
        var entry = MixCombineEngine.ResolveEntry(merged.Entries, entryQuery);

        if (rawOutput)
        {
            io.WriteRawStdout(entry.Payload);
            return;
        }

        if (!forceHex && entry.DetectedType == "INI / Text")
        {
            var slice = entry.Payload.AsSpan(0, Math.Min(maxBytes, entry.Payload.Length));
            io.Out(Encoding.Latin1.GetString(slice));
            return;
        }

        if (!forceHex && entry.DetectedType == "PAL (256-Color)" && entry.Payload.Length >= 768)
        {
            io.Out($"Palette {entry.DisplayLabel} (256 6-bit VGA entries):");
            for (var i = 0; i < 256; i++)
            {
                var b = i * 3;
                io.Out($"  [{i,3}] R={entry.Payload[b],2} G={entry.Payload[b + 1],2} B={entry.Payload[b + 2],2}");
            }
            return;
        }

        var hexSlice = entry.Payload.AsSpan(0, Math.Min(maxBytes, entry.Payload.Length));
        io.Out(HexDump(hexSlice));
    }

    // MARK: - export-shp

    private static void RunExportShp(string[] args, CliIO io)
    {
        string? entryQuery = null;
        string? outputPath = null;
        var paletteSpec = "snow";
        int? frameIndex = null;
        var exportSheet = false;
        var exportAllFrames = false;
        int? sheetColumns = null;
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--entry":
                    entryQuery = NextValue(args, ref i, arg);
                    break;
                case "-o":
                case "--output":
                    outputPath = NextValue(args, ref i, arg);
                    break;
                case "--palette":
                    paletteSpec = NextValue(args, ref i, arg);
                    break;
                case "--frame":
                {
                    var val = NextValue(args, ref i, arg);
                    if (!int.TryParse(val, out var n) || n < 0)
                        throw CliParseException.InvalidArguments($"Invalid --frame '{val}'.");
                    frameIndex = n;
                    break;
                }
                case "--sheet":
                    exportSheet = true;
                    break;
                case "--all-frames":
                    exportAllFrames = true;
                    break;
                case "--columns":
                {
                    var val = NextValue(args, ref i, arg);
                    if (!int.TryParse(val, out var n) || n <= 0)
                        throw CliParseException.InvalidArguments($"Invalid --columns '{val}'.");
                    sheetColumns = n;
                    break;
                }
                default:
                    remaining.Add(arg);
                    break;
            }
        }

        var parsed = ParseArchiveQueryArgs(remaining.ToArray(), "export-shp");
        if (string.IsNullOrEmpty(outputPath))
            throw CliParseException.InvalidArguments("Missing required output path (-o / --output <out.png or dir>).");

        ImportNameDatabases(parsed.NamePaths);

        byte[] shpData;
        IReadOnlyList<MixEntry> mergedEntries;
        string baseName;

        if (parsed.InputPaths.Count == 1 && entryQuery is null && !parsed.InputPaths[0].EndsWith(".mix", StringComparison.OrdinalIgnoreCase))
        {
            shpData = File.ReadAllBytes(parsed.InputPaths[0]);
            mergedEntries = [];
            baseName = Path.GetFileNameWithoutExtension(parsed.InputPaths[0]);
        }
        else
        {
            if (string.IsNullOrEmpty(entryQuery))
                throw CliParseException.InvalidArguments("Specify --entry <name-or-crc> when reading from .MIX archive(s), or pass a standalone .SHP file.");

            var loaded = LoadArchives(parsed.InputPaths);
            var (archives, _) = PrepareArchivesForQuery(loaded, parsed);
            var merged = MixArchiveEngine.Merge(archives, parsed.DuplicateStrategy, false);
            mergedEntries = merged.Entries;
            var entry = MixCombineEngine.ResolveEntry(mergedEntries, entryQuery);
            shpData = entry.Payload;
            baseName = entry.KnownName is not null ? Path.GetFileNameWithoutExtension(entry.KnownName) : entry.HexId;
        }

        var sprite = SpriteDecoder.Decode(shpData)
            ?? throw new MixArchiveException("Input is not a valid Westwood .SHP sprite.");

        var palette = ResolvePalette(paletteSpec, mergedEntries);

        if (exportAllFrames)
        {
            Directory.CreateDirectory(outputPath);
            var writtenPaths = new List<string>(sprite.FrameCount);
            for (var f = 0; f < sprite.FrameCount; f++)
            {
                var png = PngWriter.EncodeRgba(sprite.RenderFrame(f, palette));
                var filePath = Path.Combine(outputPath, $"{baseName}_frame{f + 1}.png");
                WriteAllBytesAtomic(filePath, png);
                writtenPaths.Add(Path.GetFullPath(filePath));
            }
            if (parsed.JsonOutput)
            {
                io.Out(EncodeJson(new { frames = writtenPaths, width = sprite.Width, height = sprite.Height }));
            }
            else
            {
                io.Out($"Exported {sprite.FrameCount} SHP frame(s) ({sprite.Width}x{sprite.Height}) to {outputPath}");
            }
            return;
        }

        byte[] pngData;
        if (exportSheet)
        {
            pngData = PngWriter.EncodeRgba(sprite.RenderSpriteSheet(palette, sheetColumns));
        }
        else
        {
            var f = frameIndex ?? 0;
            if (f >= sprite.FrameCount)
                throw CliParseException.InvalidArguments($"Frame index {f} out of bounds (sprite has {sprite.FrameCount} frame(s), 0..{sprite.FrameCount - 1}).");
            pngData = PngWriter.EncodeRgba(sprite.RenderFrame(f, palette));
        }

        EnsureParentDirectory(outputPath);
        WriteAllBytesAtomic(outputPath, pngData);

        if (parsed.JsonOutput)
        {
            io.Out(EncodeJson(new
            {
                outputPath = Path.GetFullPath(outputPath),
                width = sprite.Width,
                height = sprite.Height,
                frameCount = sprite.FrameCount,
                mode = exportSheet ? "sheet" : "frame"
            }));
        }
        else
        {
            io.Out($"Exported SHP {(exportSheet ? "sprite sheet" : $"frame {frameIndex ?? 0}")} ({sprite.Width}x{sprite.Height}, {sprite.FrameCount} frames) -> {outputPath}");
        }
    }

    // MARK: - export-aud

    private static void RunExportAud(string[] args, CliIO io)
    {
        string? entryQuery = null;
        string? outputPath = null;
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--entry")
                entryQuery = NextValue(args, ref i, arg);
            else if (arg is "-o" or "--output")
                outputPath = NextValue(args, ref i, arg);
            else
                remaining.Add(arg);
        }

        var parsed = ParseArchiveQueryArgs(remaining.ToArray(), "export-aud");
        if (string.IsNullOrEmpty(outputPath))
            throw CliParseException.InvalidArguments("Missing required output path (-o / --output <out.wav>).");

        ImportNameDatabases(parsed.NamePaths);
        var audData = LoadEntryOrStandalonePayload(parsed, entryQuery, ".aud");

        var wavData = AudioDecoder.DecodeAUD(audData)
            ?? throw new MixArchiveException("Input could not be decoded as a Westwood .AUD audio stream.");

        EnsureParentDirectory(outputPath);
        WriteAllBytesAtomic(outputPath, wavData);

        if (parsed.JsonOutput)
        {
            io.Out(EncodeJson(new { outputPath = Path.GetFullPath(outputPath), wavBytes = wavData.Length }));
        }
        else
        {
            io.Out($"Exported decoded WAV ({wavData.Length} bytes) -> {outputPath}");
        }
    }

    // MARK: - export-vqa

    private static void RunExportVqa(string[] args, CliIO io)
    {
        string? entryQuery = null;
        string? audioOutPath = null;
        string? frameOutPath = null;
        string? framesDirPath = null;
        var frameIndex = 0;
        var infoOnly = false;
        var remaining = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--entry":
                    entryQuery = NextValue(args, ref i, arg);
                    break;
                case "--audio-out":
                    audioOutPath = NextValue(args, ref i, arg);
                    break;
                case "-o":
                case "--output":
                    frameOutPath = NextValue(args, ref i, arg);
                    break;
                case "--frames-dir":
                    framesDirPath = NextValue(args, ref i, arg);
                    break;
                case "--frame":
                {
                    var val = NextValue(args, ref i, arg);
                    if (!int.TryParse(val, out frameIndex) || frameIndex < 0)
                        throw CliParseException.InvalidArguments($"Invalid --frame '{val}'.");
                    break;
                }
                case "--info":
                    infoOnly = true;
                    break;
                default:
                    remaining.Add(arg);
                    break;
            }
        }

        var parsed = ParseArchiveQueryArgs(remaining.ToArray(), "export-vqa");
        ImportNameDatabases(parsed.NamePaths);
        var vqaData = LoadEntryOrStandalonePayload(parsed, entryQuery, ".vqa");

        var vqa = AudioDecoder.DecodeVQA(vqaData)
            ?? throw new MixArchiveException("Input is not a valid Westwood .VQA video.");

        if (audioOutPath is not null)
        {
            if (vqa.WavAudioData is null)
                throw new MixArchiveException("VQA stream contains no SND2 audio.");
            EnsureParentDirectory(audioOutPath);
            WriteAllBytesAtomic(audioOutPath, vqa.WavAudioData);
        }

        if (frameOutPath is not null)
        {
            if (frameIndex >= vqa.FrameOffsets.Length)
                throw CliParseException.InvalidArguments($"Frame index {frameIndex} out of bounds ({vqa.FrameOffsets.Length} frames).");
            var png = PngWriter.EncodeRgba(vqa.RenderFrame(frameIndex, vqaData));
            EnsureParentDirectory(frameOutPath);
            WriteAllBytesAtomic(frameOutPath, png);
        }

        if (framesDirPath is not null)
        {
            Directory.CreateDirectory(framesDirPath);
            for (var f = 0; f < vqa.FrameOffsets.Length; f++)
            {
                var png = PngWriter.EncodeRgba(vqa.RenderFrame(f, vqaData));
                var filePath = Path.Combine(framesDirPath, $"frame_{f + 1:D4}.png");
                WriteAllBytesAtomic(filePath, png);
            }
        }

        if (parsed.JsonOutput)
        {
            io.Out(EncodeJson(new
            {
                width = vqa.Width,
                height = vqa.Height,
                frameCount = vqa.FrameCount,
                decodedFrameOffsets = vqa.FrameOffsets.Length,
                fps = vqa.Fps,
                hasAudio = vqa.WavAudioData is not null,
                audioWavBytes = vqa.WavAudioData?.Length,
                audioOutPath,
                frameOutPath,
                framesDirPath
            }));
            return;
        }

        if (infoOnly || (audioOutPath is null && frameOutPath is null && framesDirPath is null))
        {
            var audioText = vqa.WavAudioData is { } wav ? $"yes ({wav.Length} WAV bytes)" : "no";
            io.Out($"VQA: {vqa.Width}x{vqa.Height}, {vqa.FrameOffsets.Length} frame(s), {vqa.Fps} FPS, audio={audioText}");
        }
        else
        {
            io.Out($"Exported VQA assets ({vqa.Width}x{vqa.Height}, {vqa.FrameOffsets.Length} frame(s), {vqa.Fps} FPS)");
        }
    }

    // MARK: - crc

    private static void RunCrc(string[] args, CliIO io)
    {
        var filenames = new List<string>();
        var jsonOutput = false;
        foreach (var arg in args)
        {
            if (arg is "-h" or "--help")
                throw CliParseException.HelpRequested("crc");
            if (arg == "--json")
                jsonOutput = true;
            else if (arg.StartsWith('-'))
                throw CliParseException.InvalidArguments($"Unknown option '{arg}' for 'crc'.");
            else
                filenames.Add(arg);
        }

        if (filenames.Count == 0)
            throw CliParseException.InvalidArguments("Provide at least one filename for 'crc'.");

        if (jsonOutput)
        {
            var items = filenames.Select(name =>
            {
                var crc = MixArchiveEngine.WestwoodCrc(name);
                return new
                {
                    filename = name,
                    normalized = name.ToUpperInvariant(),
                    crcHex = $"0x{crc:X8}",
                    crcDecimal = crc
                };
            }).ToList();
            io.Out(EncodeJson(items));
            return;
        }

        foreach (var name in filenames)
        {
            var crc = MixArchiveEngine.WestwoodCrc(name);
            io.Out($"{name.ToUpperInvariant()} -> 0x{crc:X8}");
        }
    }

    // MARK: - dict

    private static void RunDict(string[] args, CliIO io)
    {
        if (args.Length == 0)
            throw CliParseException.InvalidArguments("Missing action for 'dict'. Expected: lookup, check, stats.");

        var action = args[0].ToLowerInvariant();
        if (action is "-h" or "--help")
            throw CliParseException.HelpRequested("dict");

        var namePaths = new List<string>();
        var queries = new List<string>();
        var jsonOutput = false;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
                throw CliParseException.HelpRequested("dict");
            if (arg == "--names")
                namePaths.Add(NextValue(args, ref i, arg));
            else if (arg == "--json")
                jsonOutput = true;
            else if (arg.StartsWith('-'))
                throw CliParseException.InvalidArguments($"Unknown option '{arg}' for 'dict'.");
            else
                queries.Add(arg);
        }

        var importedCount = ImportNameDatabases(namePaths);

        switch (action)
        {
            case "stats":
                if (jsonOutput)
                    io.Out(EncodeJson(new { totalCount = NameDatabase.Shared.Count, newlyImported = importedCount }));
                else
                    io.Out($"Dictionary entries: {NameDatabase.Shared.Count} (newly imported: {importedCount})");
                break;

            case "lookup":
                if (queries.Count == 0)
                    throw CliParseException.InvalidArguments("Provide one or more CRC IDs (e.g. 0xB9A5D8F1) for 'dict lookup'.");

                var lookupItems = queries.Select(q =>
                {
                    if (!MixCombineEngine.TryParseHexCrc(q, out var crc))
                        throw CliParseException.InvalidArguments($"Invalid hex CRC '{q}'.");
                    return new { crcHex = $"0x{crc:X8}", name = NameDatabase.Shared.Lookup(crc) };
                }).ToList();

                if (jsonOutput)
                {
                    io.Out(EncodeJson(lookupItems));
                }
                else
                {
                    foreach (var item in lookupItems)
                        io.Out($"{item.crcHex} -> {item.name ?? "<unknown>"}");
                }
                break;

            case "check":
                if (queries.Count == 0)
                    throw CliParseException.InvalidArguments("Provide one or more filenames for 'dict check'.");

                var checkItems = queries.Select(q =>
                {
                    var crc = MixArchiveEngine.WestwoodCrc(q);
                    var known = NameDatabase.Shared.Lookup(crc);
                    return new
                    {
                        filename = q,
                        crcHex = $"0x{crc:X8}",
                        inDictionary = known is not null,
                        registeredName = known
                    };
                }).ToList();

                if (jsonOutput)
                {
                    io.Out(EncodeJson(checkItems));
                }
                else
                {
                    foreach (var item in checkItems)
                    {
                        var status = item.inDictionary ? $"KNOWN ({item.registeredName})" : "NOT IN DICTIONARY";
                        io.Out($"{item.filename} [{item.crcHex}] -> {status}");
                    }
                }
                break;

            default:
                throw CliParseException.InvalidArguments($"Unknown 'dict' action '{action}'. Expected: lookup, check, stats.");
        }
    }

    // MARK: - verify

    private static void RunVerify(string[] args, CliIO io)
    {
        var paths = new List<string>();
        var namePaths = new List<string>();
        var strictOpenRa = true;
        var jsonOutput = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    throw CliParseException.HelpRequested("verify");
                case "--names":
                    namePaths.Add(NextValue(args, ref i, arg));
                    break;
                case "--strict-openra":
                    strictOpenRa = true;
                    break;
                case "--no-strict-openra":
                    strictOpenRa = false;
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw CliParseException.InvalidArguments($"Unknown option '{arg}' for 'verify'.");
                    paths.Add(arg);
                    break;
            }
        }

        if (paths.Count == 0)
            throw CliParseException.InvalidArguments("Provide at least one .MIX file to verify.");

        ImportNameDatabases(namePaths);

        var reports = new List<MixVerificationReport>(paths.Count);
        var anyInvalid = false;
        foreach (var path in paths)
        {
            var data = File.ReadAllBytes(path);
            var report = MixCombineEngine.Verify(data, Path.GetFileName(path), strictOpenRa);
            reports.Add(report);
            if (!report.OpenRaCompatible)
                anyInvalid = true;
        }

        if (jsonOutput)
        {
            io.Out(EncodeJson(reports));
        }
        else
        {
            foreach (var r in reports)
            {
                io.Out($"{r.ArchiveName}: OpenRA={(r.OpenRaCompatible ? "PASS" : "FAIL")} • Westwood={(r.ValidForWestwood ? "PASS" : "FAIL")} • {r.FormatVariant} • {r.EntryCount} entries • {r.TotalSize} bytes");
                foreach (var w in r.Warnings)
                    io.Out($"  Warning: {w}");
                foreach (var e in r.Errors)
                    io.Err($"  Error: {e}");
            }
        }

        if (anyInvalid)
            throw new MixArchiveException($"Archive verification failed: {string.Join("; ", reports.SelectMany(r => r.Errors))}");
    }

    // MARK: - Helpers

    private sealed class ArchiveQueryArgs
    {
        public List<string> InputPaths { get; } = [];
        public List<string> NamePaths { get; } = [];
        public DuplicateStrategy DuplicateStrategy { get; set; } = DuplicateStrategy.OverwriteLater;
        public bool ExpandNested { get; set; }
        public List<string> ExpandFilters { get; } = [];
        public int? MaxDepth { get; set; }
        public bool KeepContainers { get; set; }
        public string? FilterQuery { get; set; }
        public List<string> IncludePatterns { get; } = [];
        public List<string> ExcludePatterns { get; } = [];
        public string? TypeFilter { get; set; }
        public bool ShowEntries { get; set; }
        public bool RawList { get; set; }
        public bool JsonOutput { get; set; }
    }

    private static ArchiveQueryArgs ParseArchiveQueryArgs(
        string[] args,
        string command,
        bool allowShowEntries = false,
        bool allowRawList = false)
    {
        var parsed = new ArchiveQueryArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    throw CliParseException.HelpRequested(command);
                case "-i":
                case "--input":
                    parsed.InputPaths.Add(NextValue(args, ref i, arg));
                    break;
                case "--names":
                    parsed.NamePaths.Add(NextValue(args, ref i, arg));
                    break;
                case "--duplicates":
                case "--duplicate-policy":
                {
                    var val = NextValue(args, ref i, arg);
                    parsed.DuplicateStrategy = ParseDuplicateStrategy(val)
                        ?? throw CliParseException.InvalidArguments($"Invalid duplicate policy '{val}'.");
                    break;
                }
                case "--expand-nested":
                case "--flatten":
                    parsed.ExpandNested = true;
                    break;
                case "--expand":
                    parsed.ExpandFilters.Add(NextValue(args, ref i, arg));
                    break;
                case "--expand-depth":
                {
                    var raw = NextValue(args, ref i, arg);
                    if (!int.TryParse(raw, out var d) || d < 0)
                        throw CliParseException.InvalidArguments($"Invalid --expand-depth '{raw}'.");
                    parsed.MaxDepth = d;
                    parsed.ExpandNested = true;
                    break;
                }
                case "--keep-nested-containers":
                    parsed.KeepContainers = true;
                    break;
                case "--filter":
                    parsed.FilterQuery = NextValue(args, ref i, arg);
                    break;
                case "--include":
                    parsed.IncludePatterns.Add(NextValue(args, ref i, arg));
                    break;
                case "--exclude":
                    parsed.ExcludePatterns.Add(NextValue(args, ref i, arg));
                    break;
                case "--type":
                    parsed.TypeFilter = NextValue(args, ref i, arg);
                    break;
                case "--entries" when allowShowEntries:
                    parsed.ShowEntries = true;
                    break;
                case "--raw-list" when allowRawList:
                    parsed.RawList = true;
                    break;
                case "--json":
                    parsed.JsonOutput = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw CliParseException.InvalidArguments($"Unknown option '{arg}' for '{command}'.");
                    parsed.InputPaths.Add(arg);
                    break;
            }
        }

        if (parsed.InputPaths.Count == 0)
            throw CliParseException.InvalidArguments($"Provide at least one input file for '{command}'.");
        return parsed;
    }

    private static (List<LoadedMixArchive> Archives, int ExpandedCount) PrepareArchivesForQuery(
        List<LoadedMixArchive> loaded,
        ArchiveQueryArgs queryArgs)
    {
        if (queryArgs.ExpandNested || queryArgs.ExpandFilters.Count > 0)
        {
            var expanded = MixCombineEngine.ExpandNestedArchives(
                loaded,
                queryArgs.MaxDepth,
                queryArgs.ExpandFilters,
                queryArgs.KeepContainers);
            return (expanded.Archives, expanded.ExpandedCount);
        }
        return (loaded, 0);
    }

    private static List<MixEntry> FilterEntries(IReadOnlyList<MixEntry> entries, ArchiveQueryArgs queryArgs)
    {
        return entries.Where(entry =>
        {
            if (!MixCombineEngine.MatchesFilters(entry, queryArgs.FilterQuery, queryArgs.IncludePatterns, queryArgs.ExcludePatterns))
                return false;
            if (!string.IsNullOrWhiteSpace(queryArgs.TypeFilter))
                return entry.DetectedType.Contains(queryArgs.TypeFilter.Trim(), StringComparison.OrdinalIgnoreCase);
            return true;
        }).ToList();
    }

    private static byte[] LoadEntryOrStandalonePayload(ArchiveQueryArgs parsed, string? entryQuery, string extensionHint)
    {
        if (parsed.InputPaths.Count == 1 && entryQuery is null && parsed.InputPaths[0].EndsWith(extensionHint, StringComparison.OrdinalIgnoreCase))
            return File.ReadAllBytes(parsed.InputPaths[0]);

        if (string.IsNullOrEmpty(entryQuery))
            throw CliParseException.InvalidArguments(
                $"Specify --entry <name-or-crc> when reading from .MIX archive(s), or pass a standalone {extensionHint.ToUpperInvariant()} file.");

        var loaded = LoadArchives(parsed.InputPaths);
        var (archives, _) = PrepareArchivesForQuery(loaded, parsed);
        var merged = MixArchiveEngine.Merge(archives, parsed.DuplicateStrategy, false);
        return MixCombineEngine.ResolveEntry(merged.Entries, entryQuery).Payload;
    }

    private static byte[] ResolvePalette(string spec, IReadOnlyList<MixEntry> entries)
    {
        var lower = spec.Trim().ToLowerInvariant();
        if (lower == "snow")
            return SpriteDecoder.BuiltInSnowPalette;
        if (lower is "temperate" or "temp")
            return SpriteDecoder.BuiltInTemperatePalette;

        if (File.Exists(spec))
        {
            var data = File.ReadAllBytes(spec);
            if (data.Length < 768)
                throw CliParseException.InvalidArguments($"Palette file '{spec}' must be at least 768 bytes.");
            return data[..768];
        }

        if (entries.Count > 0)
        {
            try
            {
                var entry = MixCombineEngine.ResolveEntry(entries, spec);
                if (entry.Payload.Length >= 768)
                    return entry.Payload[..768];
            }
            catch (MixArchiveException)
            {
            }
        }

        throw CliParseException.InvalidArguments(
            $"Unknown palette '{spec}'. Use 'snow', 'temperate', a .PAL entry name/CRC, or a 768-byte .PAL file path.");
    }

    private static int ImportNameDatabases(IReadOnlyList<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
            added += NameDatabase.Shared.ImportFile(path);
        return added;
    }

    private static List<LoadedMixArchive> LoadArchives(IReadOnlyList<string> paths)
    {
        var list = new List<LoadedMixArchive>(paths.Count);
        foreach (var path in paths)
            list.Add(MixArchiveEngine.Parse(path));
        return list;
    }

    private static List<string> CollectInputFiles(IReadOnlyList<string> files, IReadOnlyList<string> directories, bool recursive)
    {
        var paths = new List<string>(files);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var dir in directories)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", option))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith('.'))
                    paths.Add(file);
            }
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static LoadedMixArchive BuildSyntheticArchive(IReadOnlyList<string> filePaths)
    {
        var entries = new List<MixEntry>(filePaths.Count);
        var names = new List<string>(filePaths.Count);
        var total = 0;
        foreach (var path in filePaths)
        {
            var data = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            names.Add(name);
            entries.Add(new MixEntry(
                MixArchiveEngine.WestwoodCrc(name),
                checked((uint)total),
                checked((uint)data.Length),
                name,
                data));
            total = checked(total + data.Length);
        }
        NameDatabase.Shared.RegisterExact(names);
        return new LoadedMixArchive(
            Guid.NewGuid(),
            "LOOSE_FILES",
            total,
            MixFormatVariant.RedAlertTS,
            false,
            false,
            null,
            entries);
    }

    private static DuplicateStrategy? ParseDuplicateStrategy(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "overwrite-later" or "overwrite" or "later" or "last" => DuplicateStrategy.OverwriteLater,
            "keep-first" or "first" => DuplicateStrategy.KeepFirst,
            "keep-all" or "all" => DuplicateStrategy.KeepAll,
            _ => null
        };
    }

    private static string FormatDuplicateLabel(DuplicateStrategy strategy) => strategy switch
    {
        DuplicateStrategy.OverwriteLater => "Overwrite with Later Archive",
        DuplicateStrategy.KeepFirst => "Keep First Occurrence",
        DuplicateStrategy.KeepAll => "Keep All Entries",
        _ => strategy.ToString()
    };

    private static string NextValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
            throw CliParseException.InvalidArguments($"Option '{flag}' requires a value.");
        index++;
        return args[index];
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static void WriteAllBytesAtomic(string filePath, byte[] data)
    {
        var full = Path.GetFullPath(filePath);
        var tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, full, overwrite: true);
    }

    private static object ToEntryJson(MixEntry entry) => new
    {
        crcHex = entry.HexId,
        crcDecimal = entry.CrcId,
        knownName = entry.KnownName,
        suggestedFilename = entry.SuggestedFilename,
        detectedType = entry.DetectedType,
        offset = entry.OriginalOffset,
        size = entry.Size,
        sourceArchive = entry.SourceArchive
    };

    private static void PrintEntryTable(IReadOnlyList<MixEntry> entries, CliIO io)
    {
        io.Out("Name / CRC File ID                  Type             Offset      Size        Source");
        io.Out(new string('-', 96));
        foreach (var entry in entries)
        {
            var nameCol = entry.DisplayLabel.PadRight(35);
            var typeCol = entry.DetectedType.PadRight(16);
            var offCol = $"0x{entry.OriginalOffset:X6}".PadRight(11);
            var sizeCol = entry.Size.ToString().PadRight(11);
            io.Out($"{nameCol} {typeCol} {offCol} {sizeCol} {entry.SourceArchive}");
        }
    }

    private static string HexDump(ReadOnlySpan<byte> data)
    {
        var lines = new List<string>();
        var offset = 0;
        while (offset < data.Length)
        {
            var len = Math.Min(16, data.Length - offset);
            var chunk = data.Slice(offset, len);
            var hexPart = string.Join(" ", chunk.ToArray().Select(b => $"{b:X2}")).PadRight(48);
            var asciiPart = new string(chunk.ToArray().Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray());
            lines.Add($"{offset:X4}:  {hexPart}  |{asciiPart}|");
            offset += 16;
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes) =>
        bytes < 1024 ? $"{bytes} bytes" :
        bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" :
        $"{bytes / (1024d * 1024):0.0} MB";

    private static string EncodeJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static readonly string GlobalHelpText = $"""
        CCMixStudio.Windows {Version} — Westwood .MIX Archive CLI & Studio

        USAGE:
          CCMixStudio.Windows <command> [options] [arguments]
          CCMixStudio.Windows [--gui [archive.mix ...]]

        COMMANDS:
          combine (merge)   Combine multiple .MIX archives (and optional loose files) into a single OpenRA-compatible .MIX
          pack              Pack loose files / directories into a .MIX archive
          inspect (info)    Inspect .MIX archive metadata and merge summary
          list (ls)         List and filter entries across .MIX archives
          extract           Extract single or batch entries from .MIX archives
          cat (preview)     Print text, palette, hex dump, or raw bytes of an entry
          export-shp        Decode a .SHP sprite to PNG frame(s) or a sprite sheet
          export-aud        Decode a Westwood .AUD audio stream to .WAV
          export-vqa        Inspect or export audio/frames from a .VQA video
          crc               Calculate Westwood CRC file IDs for filenames
          dict              Lookup, check, or import filename dictionaries
          verify            Validate .MIX archives for Westwood & OpenRA compatibility
          gui               Launch the CCMixStudio desktop GUI (optionally preloading .MIX files)
          help [command]    Show global or command-specific help

        EXAMPLES:
          Combine multiple .MIX archives into one OpenRA-compatible .MIX (flattening nested .MIX files):
            CCMixStudio.Windows combine MAIN.MIX EXPAND.MIX MOD.MIX -o COMBINED.MIX --expand-nested

          Combine classic Tiberian Dawn archives with a specific duplicate policy:
            CCMixStudio.Windows combine CONQUER.MIX GENERAL.MIX -o TD_ALL.MIX --format openra --duplicates overwrite-later

          Verify an archive for OpenRA client compatibility:
            CCMixStudio.Windows verify COMBINED.MIX --strict-openra

        Run 'CCMixStudio.Windows help <command>' for detailed options on any command.
        """;

    private static string HelpTextFor(string? topic) => topic switch
    {
        "combine" or "merge" => """
            USAGE:
              CCMixStudio.Windows combine <archive1.mix> [archive2.mix ...] -o <output.mix> [options]

            OPTIONS:
              -i, --input <path>             Add an input .MIX archive (or pass positionally)
              -o, --output <path>            Output .MIX path (required unless --dry-run)
                  --add-file <path>          Include a loose asset file in the combined archive
                  --add-dir <dir>            Include files from a directory (-r/--recursive for subdirs)
              -r, --recursive                Recurse into --add-dir directories
                  --duplicates <policy>      overwrite-later (default), keep-first, keep-all
                  --format <mode>            openra (default), auto, ra, classic
                  --ra-header                Force 4-byte 0x00000000 RA/TS/RA2 flag header
                  --no-ra-header             Force classic 6-byte Tiberian Dawn header
                  --expand-nested            Recursively expand nested .MIX archives (alias: --flatten)
                  --expand <name-or-crc>     Expand only matching nested .MIX entry (repeatable)
                  --expand-depth <n>         Maximum nested expansion recursion depth
                  --keep-nested-containers   Keep original nested .MIX entries alongside expanded files
                  --filter <query>           Filter entries by name, 0xCRC, type, or source
                  --include <pattern>        Keep entries matching glob/substring/0xCRC (repeatable)
                  --exclude <pattern>        Drop entries matching glob/substring/0xCRC (repeatable)
                  --names <path>             Import XCC database.dat or .txt/.ini filename list (repeatable)
                  --local-db <policy>        merge (default), keep, strip, generate
                  --verify / --no-verify     Verify output archive after build (default: --verify)
                  --dry-run                  Simulate merge and verification without writing output
                  --json                     Output machine-readable JSON
              -q, --quiet                    Suppress status output
            """,
        "pack" => """
            USAGE:
              CCMixStudio.Windows pack <file-or-dir ...> -o <output.mix> [options]

            OPTIONS:
              -o, --output <path>            Output .MIX file path (required unless --dry-run)
              -r, --recursive                Walk directories recursively
                  --format <mode>            openra (default), auto, ra, classic
                  --duplicates <policy>      overwrite-later (default), keep-first, keep-all
                  --ra-header / --no-ra-header
                  --local-db <policy>        generate (default), merge, keep, strip
                  --names <path>             Import additional filename lists
                  --verify / --no-verify     Verify output archive (default: --verify)
                  --dry-run                  Simulate without writing output
                  --json                     Output machine-readable JSON
            """,
        "inspect" or "info" => """
            USAGE:
              CCMixStudio.Windows inspect <archive.mix ...> [options]

            OPTIONS:
                  --entries                  Also print the merged entry table
                  --expand-nested            Recursively expand nested .MIX archives
                  --expand <name-or-crc>     Expand specific nested .MIX entry
                  --duplicates <policy>      overwrite-later, keep-first, keep-all
                  --filter <query>           Filter entries
                  --names <path>             Import XCC database.dat or filename list
                  --json                     Output JSON
            """,
        "list" or "ls" => """
            USAGE:
              CCMixStudio.Windows list <archive.mix ...> [options]

            OPTIONS:
                  --expand-nested            Recursively expand nested .MIX archives
                  --expand <name-or-crc>     Expand specific nested .MIX entry
                  --duplicates <policy>      overwrite-later, keep-first, keep-all
                  --raw-list                 List raw entries per archive without deduplication
                  --filter <query>           Filter by name, 0xCRC, type, or source
                  --include <pattern>        Include matching entries (repeatable)
                  --exclude <pattern>        Exclude matching entries (repeatable)
                  --type <type>              Filter by detected type substring (e.g. SHP, AUD)
                  --names <path>             Import filename dictionary
                  --json                     Output JSON
            """,
        "extract" => """
            USAGE:
              CCMixStudio.Windows extract <archive.mix ...> -o <dir-or-file> [options]

            OPTIONS:
              -o, --output <path>            Destination directory (or file path for single --entry)
                  --entry <name-or-crc>      Extract specific entry (repeatable)
                  --expand-nested            Recursively expand nested .MIX archives
                  --duplicates <policy>      overwrite-later, keep-first, keep-all
                  --filter / --include / --exclude
                  --names <path>             Import filename dictionary
                  --json                     Output JSON
            """,
        "cat" or "preview" => """
            USAGE:
              CCMixStudio.Windows cat <archive.mix ...> --entry <name-or-crc> [--hex | --raw] [--max-bytes N]
            """,
        "export-shp" => """
            USAGE:
              CCMixStudio.Windows export-shp <archive.mix ... --entry <name-or-crc> | sprite.shp> -o <out.png | dir> [options]

            OPTIONS:
                  --palette <spec>           snow (default), temperate, archive .PAL name/CRC, or .PAL file path
                  --frame <index>            0-based frame index to export (default: 0)
                  --sheet                    Stitch all frames into a single transparent PNG sprite sheet
                  --columns <n>              Column count for sprite sheet grid
                  --all-frames               Export every frame as individual PNGs into output directory
                  --expand-nested            Expand nested .MIX archives when reading from .MIX
                  --names <path>             Import filename dictionary
                  --json                     Output JSON
            """,
        "export-aud" => """
            USAGE:
              CCMixStudio.Windows export-aud <archive.mix ... --entry <name-or-crc> | sound.aud> -o <out.wav> [options]
            """,
        "export-vqa" => """
            USAGE:
              CCMixStudio.Windows export-vqa <archive.mix ... --entry <name-or-crc> | video.vqa> [options]

            OPTIONS:
                  --info                     Print VQA metadata (default if no export flag given)
                  --audio-out <out.wav>      Export decoded SND2 audio stream to WAV
              -o, --output <out.png>         Export single rendered frame (select with --frame <0-based>)
                  --frame <index>            0-based frame index (default: 0)
                  --frames-dir <dir>         Export all rendered frames as PNGs to directory
                  --json                     Output JSON
            """,
        "crc" => """
            USAGE:
              CCMixStudio.Windows crc <filename...> [--json]
            """,
        "dict" => """
            USAGE:
              CCMixStudio.Windows dict <stats | lookup <0xCRC...> | check <filename...>> [--names <path>] [--json]
            """,
        "verify" => """
            USAGE:
              CCMixStudio.Windows verify <archive.mix ...> [--strict-openra | --no-strict-openra] [--names <path>] [--json]
            """,
        _ => GlobalHelpText
    };
}
