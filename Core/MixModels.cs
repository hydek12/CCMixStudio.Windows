using System.Collections.ObjectModel;
using System.IO;
using System.Text;

namespace CCMixStudio.Windows.Core;

public enum MixFormatVariant
{
    TiberianDawn,
    RedAlertTS,
    RedAlertTSEncrypted
}

public enum DuplicateStrategy
{
    OverwriteLater,
    KeepFirst,
    KeepAll
}

public sealed record MixEntry(uint CrcId, uint OriginalOffset, uint Size, string SourceArchive, byte[] Payload)
{
    public string HexId => $"0x{CrcId:X8}";
    public string? KnownName => NameDatabase.Shared.Lookup(CrcId);
    public string DisplayLabel => KnownName is { } name ? $"{name} ({HexId})" : HexId;
    public bool IsNestedMix => MixArchiveEngine.LooksLikeMixArchive(Payload);
    public string DetectedType
    {
        get
        {
            if (IsNestedMix) return "Nested .MIX";
            if (SpriteDecoder.IsSHP(Payload)) return "SHP Sprite";
            if (AudioDecoder.IsAUD(Payload)) return "AUD Audio";
            if (Payload.Length >= 12 && Payload.AsSpan(0, 4).SequenceEqual("FORM"u8) && Payload.AsSpan(8, 4).SequenceEqual("WVQA"u8)) return "VQA Video";
            if (Payload.Length >= 4 && Payload.AsSpan(0, 4).SequenceEqual("RIFF"u8)) return "WAV / RIFF";
            if (Payload.Length == 768) return "PAL (256-Color)";
            if (Payload.Length > 0 && Payload.Take(64).Count(b => b is 9 or 10 or 13 || b is >= 32 and <= 126) / (double)Math.Min(Payload.Length, 64) > 0.92) return "INI / Text";
            return Payload.Length >= 4 && Payload.AsSpan(0, 4).SequenceEqual("FORM"u8) ? "AUD / IFF" : "TMP / Data";
        }
    }
    public string SuggestedFilename
    {
        get
        {
            if (KnownName is { } name) return name;
            if (IsNestedMix) return $"{HexId}.MIX";
            return DetectedType switch
            {
                "SHP Sprite" => $"{HexId}.SHP",
                "WAV / RIFF" => $"{HexId}.WAV",
                "VQA Video" => $"{HexId}.VQA",
                "AUD Audio" => $"{HexId}.AUD",
                "INI / Text" => $"{HexId}.TXT",
                "PAL (256-Color)" => $"{HexId}.PAL",
                _ => $"{HexId}.BIN"
            };
        }
    }
}

public sealed record LoadedMixArchive(
    Guid Id,
    string FileName,
    int FileSize,
    MixFormatVariant Variant,
    bool WasEncrypted,
    bool IsExpandedChild,
    uint? SourceCrc,
    IReadOnlyList<MixEntry> Entries);

public sealed record MergeResult(byte[] Data, IReadOnlyList<MixEntry> Entries, int DuplicatesResolved);

public sealed class MixArchiveException(string message) : Exception(message);

public sealed class NameDatabase
{
    private readonly Dictionary<uint, string> _names = new();
    private readonly object _gate = new();
    public static NameDatabase Shared { get; }
    public static readonly uint LocalMixDatabaseCrc = MixArchiveEngine.WestwoodCrc("local mix database.dat");
    public int Count { get { lock (_gate) return _names.Count; } }

    static NameDatabase() => Shared = new NameDatabase();

    private NameDatabase() => RegisterExact(BuiltIns);

    public string? Lookup(uint crc) { lock (_gate) return _names.GetValueOrDefault(crc); }

    public int ImportFile(string path)
    {
        return ImportBytes(File.ReadAllBytes(path));
    }

    public int ImportBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > 52 && Encoding.ASCII.GetString(data, 0, 24).StartsWith("XCC by Olaf van der Spek", StringComparison.Ordinal))
        {
            var names = new List<string>();
            var current = new List<byte>();
            foreach (var value in data.AsSpan(52))
            {
                if (value == 0)
                {
                    if (current.Count > 0) names.Add(Encoding.Latin1.GetString(current.ToArray()));
                    current.Clear();
                }
                else if (value >= 32) current.Add(value);
            }
            return RegisterExact(names);
        }
        var text = Encoding.UTF8.GetString(data);
        return Register(text.Split(['\r', '\n', '\0'], StringSplitOptions.RemoveEmptyEntries));
    }

    public int RegisterExact(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (_gate)
        {
            var added = 0;
            foreach (var raw in names)
            {
                var cleaned = raw.Trim();
                if (cleaned.Length == 0 || cleaned.StartsWith(';') || cleaned.StartsWith('#')) continue;
                var crc = MixArchiveEngine.WestwoodCrc(cleaned);
                if (!_names.ContainsKey(crc)) added++;
                _names[crc] = cleaned.ToUpperInvariant();
            }
            return added;
        }
    }

    public int Register(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (_gate)
        {
            var added = 0;
            foreach (var raw in names)
            {
                var cleaned = raw.Trim();
                if (cleaned.Length == 0 || cleaned.StartsWith(';') || cleaned.StartsWith('#')) continue;
                var candidate = string.Equals(cleaned, "local mix database.dat", StringComparison.OrdinalIgnoreCase)
                    ? cleaned
                    : cleaned.Split([' ', '\t', '='], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (string.IsNullOrEmpty(candidate)) continue;
                var crc = MixArchiveEngine.WestwoodCrc(candidate);
                if (!_names.ContainsKey(crc)) added++;
                _names[crc] = candidate.ToUpperInvariant();
            }
            return added;
        }
    }

    private static readonly string[] BuiltIns =
    [
        .."GENERAL.MIX CONQUER.MIX SOUNDS.MIX SCORES.MIX LOCAL.MIX SPEECH.MIX TEMPERAT.MIX SNOW.MIX INTERIOR.MIX DESERT.MIX WINTER.MIX MOVIES1.MIX MOVIES2.MIX HIRES.MIX LORES.MIX EXPAND.MIX EXPAND2.MIX MAIN.MIX REDALERT.MIX TRANSIT.MIX UPDATE.MIX CACHE.MIX CAMEO.MIX SIDE1.MIX SIDE2.MIX LANGUAGE.MIX MULTI.MIX MAPS01.MIX MAPS02.MIX AUD.MIX SETUP.MIX INSTALL.MIX CD1.MIX CD2.MIX ALLIES.MIX SOVIET.MIX RUSSIAN.MIX EDHI.MIX EDLO.MIX EDITOR.MIX RULES.INI AFTRMATH.INI MPLAYER.INI MISSION.INI TUTORIAL.INI CONQUER.ENG PALETTE.PAL TEMPERAT.PAL SNOW.PAL INTERIOR.PAL DESERT.PAL UNIT.PAL CAMEO.PAL MOUSE.SHP FONT.FNT 1TNK.SHP 2TNK.SHP 3TNK.SHP 4TNK.SHP V2RL.SHP ARTY.SHP HARV.SHP MCV.SHP JEEP.SHP APC.SHP E1.SHP E2.SHP E3.SHP E4.SHP E6.SHP E7.SHP EINSTEIN.SHP CHAN.SHP FACT.SHP POWR.SHP APWR.SHP PROC.SHP SILO.SHP TENT.SHP BARR.SHP WEAP.SHP FIX.SHP HPAD.SHP AFLD.SHP DOME.SHP ATEK.SHP STEK.SHP PDOX.SHP IRON.SHP MSLO.SHP KENN.SHP SYRD.SHP SPEN.SHP PBOX.SHP HBOX.SHP GUN.SHP AGUN.SHP GAP.SHP FTUR.SHP TSLA.SHP SAM.SHP HELL2V22.AUD BIGF226M.AUD CRUS226M.AUD FAC1226M.AUD FAC2226M.AUD INTL226M.AUD MUD1226M.AUD RUN1226M.AUD SMASH226.AUD TWIN226M.AUD VECTOR1A.AUD WORK226M.AUD await1.aud ready.aud report1.aud yessir1.aud affirm1.aud ackno.aud movout1.aud overout1.aud unitrdy1.aud bldging1.aud constru2.aud abldgin1.aud cancld1.aud onhold1.aud newopt1.aud nodeply1.aud place2.aud repair1.aud sell1.aud silond1.aud lowpowr1.aud nukewrn1.aud chronrdy.aud ironrdy1.aud tslachg2.aud".Split(' ', StringSplitOptions.RemoveEmptyEntries),
        "local mix database.dat"
    ];
}
