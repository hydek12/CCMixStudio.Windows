CCMixStudio is a  utility for inspecting, extracting, merging, and exporting assets from Westwood Studios .MIX archives.

Archive Handling: Parses unencrypted and RSA/Blowfish-encrypted .MIX archives, expands and unexpands nested .MIX sub-archives in place, deduplicates or overwrites colliding CRC IDs during archive merges, and exports combined .MIX files or batch-extracted assets.

Filename Resolution: Combines a built-in Command & Conquer / Red Alert filename dictionary with automatic ingestion of embedded local mix database.dat chunks and external XCC global mix database.dat or .txt lists.

Asset Inspection & Export:

Sprites (.SHP): Decodes LCW (Format80) and XOR delta (Format40/Format20) frames with palette switching (including built-in Snow and Temperate presets and imported 768-byte .PAL palettes), animation playback, single-frame PNG export, and multi-frame PNG sprite sheet stitching.

Audio (.AUD & .VQA SND2): Decodes IMA ADPCM and Westwood ADPCM streams to PCM WAV for in-app playback and .WAV export.

Video (.VQA): Parses VQHD metadata (dimensions, frame count, FPS, audio presence), CPAL/CPLZ palettes, and CBF/CBFZ/VPT/VPTZ frame data with frame stepping and playback controls.
