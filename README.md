# CCMixStudio for Windows

This is the Windows desktop port and headless CLI of CCMixStudio. It supports loading and inspecting Westwood `.MIX` archives, encrypted archive parsing, nested archive expansion, duplicate-aware merging, loose-file packing, `local mix database.dat` merging, OpenRA compatibility verification, batch extraction, filename CRC lookup/name database import, SHP and VQA preview, palette selection, frame and sprite-sheet PNG export, and AUD/VQA audio playback and WAV export.

## Run a provided build

- Intel/AMD Windows: unzip `dist/CCMixStudio.Windows-win-x64.zip` and launch `CCMixStudio.Windows.exe`.
- ARM Windows / Parallels on Apple silicon: unzip `dist/CCMixStudio.Windows-win-arm64.zip` and launch `CCMixStudio.Windows.exe`.

Both builds are self-contained and include the .NET runtime. Windows SmartScreen may show its standard warning because these builds are unsigned.

## Build on Windows

Install the .NET 9 SDK, open PowerShell in this folder, then run:

```powershell
./publish-windows.ps1
```

The default output targets 64-bit Intel/AMD Windows. To publish for Windows on ARM, run `./publish-windows.ps1 -Runtime win-arm64`.

The encrypted MIX parser uses BouncyCastle.Cryptography from NuGet. The first build needs access to NuGet.org.

## Command-Line Interface (CLI)

`CCMixStudio.Windows.exe` supports headless command-line execution directly from PowerShell or Command Prompt (`cmd.exe`) without opening a GUI window (attaching to the parent console automatically):

```powershell
.\CCMixStudio.Windows.exe --help
```

### Combining Multiple `.MIX` Files for OpenRA Clients

```powershell
# Combine multiple .MIX files (including encrypted archives and nested sub-archives)
# into a single unencrypted, CRC-sorted, deduplicated .MIX compatible with OpenRA:
.\CCMixStudio.Windows.exe combine MAIN.MIX EXPAND.MIX MOD.MIX -o COMBINED.MIX --expand-nested

# Keep first occurrence on duplicate CRC IDs, add loose files, and import an external name list:
.\CCMixStudio.Windows.exe combine CONQUER.MIX GENERAL.MIX --add-file RULES.INI -o CUSTOM.MIX `
  --format openra --duplicates keep-first --names "global mix database.dat"

# Verify OpenRA compatibility on an existing .MIX archive:
.\CCMixStudio.Windows.exe verify COMBINED.MIX --strict-openra

# Pack loose assets or extract / export assets from the command line:
.\CCMixStudio.Windows.exe pack .\mod_assets -r -o MOD_PACK.MIX --format openra
.\CCMixStudio.Windows.exe extract COMBINED.MIX -o .\extracted --expand-nested
.\CCMixStudio.Windows.exe export-shp COMBINED.MIX --entry 1TNK.SHP --sheet --palette temperate -o 1TNK_sheet.png
.\CCMixStudio.Windows.exe export-aud COMBINED.MIX --entry AWAIT1.AUD -o await1.wav

# Launch the desktop GUI preloaded with archives and options:
.\CCMixStudio.Windows.exe --gui CONQUER.MIX EXPAND.MIX --ra-header --expand-nested
```

#### OpenRA Compatibility Behavior (`--format openra`, default for `combine` & `pack`)

- Emits unencrypted archives with an ascending `UInt32` CRC-sorted index and validated entry count (`<= 65,535`) and payload size (`<= 4 GiB`).
- Enforces unique CRC IDs (rejects `--duplicates keep-all` in `openra` mode) so OpenRA package indexes do not collide.
- Flattens nested `.MIX` archives recursively when `--expand-nested` (`--flatten`) is passed, removing redundant nested container blobs by default (override with `--keep-nested-containers`, `--expand <name-or-crc>`, or `--expand-depth <n>`).
- Merges embedded `local mix database.dat` tables across combined archives by default (`--local-db merge|keep|strip|generate`) and verifies the generated archive (`--verify`).

### All Supported CLI Subcommands

- `combine` (`merge`): Combine multiple `.MIX` archives (and optional `--add-file` / `--add-dir` loose files) into a single `.MIX` archive (`--format openra|auto|ra|classic`, `--duplicates overwrite-later|keep-first|keep-all`, `--expand-nested`, `--local-db merge|keep|strip|generate`, `--filter`, `--include`, `--exclude`, `--verify`, `--dry-run`, `--json`).
- `pack`: Pack loose files and directories into a `.MIX` archive.
- `inspect` (`info`): Display archive metadata, format variant, encryption status, nested archive counts, and merge summary.
- `list` (`ls`): List and filter entries by name, CRC (`0x...`), detected type, or source archive.
- `extract`: Batch-extract entries to a folder or extract specific `--entry` items.
- `cat` (`preview`): Print `INI / Text` payloads, palette values, hex dumps (`--hex`), or raw bytes (`--raw`).
- `export-shp`: Export a `.SHP` sprite frame (`--frame N`), stitched PNG sprite sheet (`--sheet`), or all frames (`--all-frames`) with Snow, Temperate, archive `.PAL`, or external `.PAL` palette selection.
- `export-aud`: Decode `.AUD` audio streams to `.WAV`.
- `export-vqa`: Inspect `.VQA` metadata (`--info`), export decoded `SND2` audio (`--audio-out`), or render `.VQA` frames (`-o` / `--frames-dir`).
- `crc`: Compute Westwood CRC IDs for filenames.
- `dict`: Inspect or query the Global Filename Dictionary (`stats`, `lookup`, `check`) with `--names`.
- `verify`: Validate `.MIX` structure, CRC sorting, uniqueness, and OpenRA compatibility.
- `gui` / `--gui`: Launch the CCMixStudio desktop GUI optionally preloaded with `.MIX` files and merge options.

