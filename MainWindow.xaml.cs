using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using CCMixStudio.Windows.Core;

namespace CCMixStudio.Windows;

public partial class MainWindow : Window
{
    public ObservableCollection<LoadedMixArchive> Archives { get; } = [];
    public ObservableCollection<MixEntry> Entries { get; } = [];
    private readonly ObservableCollection<PaletteChoice> _palettes = [];
    private readonly System.Windows.Media.MediaPlayer _audioPlayer = new();
    private readonly DispatcherTimer _animationTimer = new();
    private LoadedMixArchive? _selectedArchive;
    private MixEntry? _selectedEntry;
    private DecodedSprite? _sprite;
    private DecodedVqa? _vqa;
    private int _frameIndex;
    private string? _playingPath;
    private bool _audioLoaded;
    private bool _isPlaybackRunning;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        PalettePicker.ItemsSource = _palettes;
        _palettes.Add(new PaletteChoice("Built-in Red Alert Snow", SpriteDecoder.BuiltInSnowPalette));
        _palettes.Add(new PaletteChoice("Built-in Red Alert Temperate", SpriteDecoder.BuiltInTemperatePalette));
        PalettePicker.SelectedIndex = 0;
        _animationTimer.Tick += AnimationTimer_Tick;
        RefreshMergedEntries();
        _audioPlayer.MediaEnded += (_, _) =>
        {
            _audioPlayer.Position = TimeSpan.Zero;
            if (!_animationTimer.IsEnabled) _isPlaybackRunning = false;
            UpdatePlaybackButton();
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var config = AppLaunchConfiguration.Shared;
        DuplicatePolicy.SelectedIndex = config.DuplicateStrategy switch
        {
            DuplicateStrategy.KeepFirst => 1,
            DuplicateStrategy.KeepAll => 2,
            _ => 0
        };
        if (config.UseRaHeaderFlag is { } raFlag)
        {
            RaFlag.IsChecked = raFlag;
        }
        foreach (var namePath in config.NameDatabasePaths)
        {
            if (File.Exists(namePath))
            {
                try { NameDatabase.Shared.ImportFile(namePath); }
                catch { }
            }
        }
        if (config.InputPaths.Count > 0)
        {
            await AddMixFilesAsync(config.InputPaths, config.ExpandNested);
        }
        else
        {
            RefreshMergedEntries();
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Add Westwood .MIX archives", Filter = "MIX archives (*.mix)|*.mix|All files (*.*)|*.*", Multiselect = true };
        if (FileDialogFolderHistory.Shared.LastMixArchiveFolder is { } initialDirectory)
        {
            picker.InitialDirectory = initialDirectory;
        }
        if (picker.ShowDialog(this) != true) return;
        FileDialogFolderHistory.Shared.RememberMixArchiveFiles(picker.FileNames);
        await AddMixFilesAsync(picker.FileNames);
    }

    private async Task AddMixFilesAsync(IEnumerable<string> paths, bool expandNested = false)
    {
        SetBusy(true);
        ErrorText.Text = string.Empty;
        var loaded = new List<LoadedMixArchive>();
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try { loaded.Add(await Task.Run(() => MixArchiveEngine.Parse(path))); }
            catch (Exception exception) { errors.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
        }
        if (expandNested && loaded.Count > 0)
        {
            try
            {
                loaded = await Task.Run(() => MixCombineEngine.ExpandNestedArchives(loaded, keepContainers: true).Archives);
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
            }
        }
        foreach (var archive in loaded) Archives.Add(archive);
        SetBusy(false);
        if (errors.Count > 0) ErrorText.Text = string.Join(Environment.NewLine, errors);
        if (loaded.Count > 0) StatusText.Text = $"Loaded {loaded.Count} archive{(loaded.Count == 1 ? "" : "s")}.";
        RefreshMergedEntries();
    }

    private void Window_DragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await AddMixFilesAsync(paths.Where(path => File.Exists(path) && Path.GetExtension(path).Equals(".mix", StringComparison.OrdinalIgnoreCase)));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Archives.Clear();
        _selectedArchive = null;
        _selectedEntry = null;
        StopPlayback();
        RefreshMergedEntries();
        ClearPreview();
    }

    private void RemoveArchive_Click(object sender, RoutedEventArgs e)
    {
        if (ArchiveList.SelectedItem is not LoadedMixArchive archive) return;
        Archives.Remove(archive);
        _selectedArchive = null;
        RefreshMergedEntries();
    }

    private void Unexpand_Click(object sender, RoutedEventArgs e)
    {
        var expandedArchives = GetExpandedArchivesForSelection();
        if (expandedArchives.Count == 0) return;

        foreach (var archive in expandedArchives)
        {
            Archives.Remove(archive);
        }
        if (_selectedArchive is not null && expandedArchives.Contains(_selectedArchive))
        {
            _selectedArchive = null;
            ArchiveList.SelectedItem = null;
        }

        StatusText.Text = expandedArchives.Count == 1
            ? $"Unexpanded {expandedArchives[0].FileName}."
            : $"Unexpanded {expandedArchives.Count} nested archives.";
        RefreshMergedEntries();
    }

    private async void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is not { IsNestedMix: true } entry) return;
        if (Archives.Any(archive => archive.IsExpandedChild && archive.SourceCrc == entry.CrcId && archive.FileName == entry.SuggestedFilename))
        {
            StatusText.Text = "This nested archive is already expanded.";
            return;
        }
        SetBusy(true);
        try
        {
            var nested = await Task.Run(() => MixArchiveEngine.Parse(entry.Payload, entry.SuggestedFilename, true, entry.CrcId));
            Archives.Add(nested);
            StatusText.Text = $"Expanded {nested.FileName} ({nested.Entries.Count} inner files).";
            RefreshMergedEntries();
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
        finally { SetBusy(false); }
    }

    private void ExtractAll_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0) return;
        var picker = new OpenFolderDialog { Title = "Choose an extraction folder", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var count = MixArchiveEngine.BatchExtract(Entries, picker.FolderName);
            StatusText.Text = $"Extracted {count} files to {picker.FolderName}.";
        }
        catch (Exception exception) { ErrorText.Text = $"Batch extraction failed: {exception.Message}"; }
    }

    private void ExportMix_Click(object sender, RoutedEventArgs e)
    {
        if (Archives.Count == 0) return;
        var picker = new SaveFileDialog { Title = "Export combined archive", FileName = "COMBINED.MIX", Filter = "MIX archive (*.mix)|*.mix|All files (*.*)|*.*", DefaultExt = ".mix" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var result = BuildMerge();
            File.WriteAllBytes(picker.FileName, result.Data);
            StatusText.Text = $"Exported {Path.GetFileName(picker.FileName)} ({result.Entries.Count} files, {FormatBytes(result.Data.Length)}).";
        }
        catch (Exception exception) { ErrorText.Text = $"Export failed: {exception.Message}"; }
    }

    private void ImportNames_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Import XCC name database or text list", Filter = "Name lists (*.dat;*.txt;*.ini)|*.dat;*.txt;*.ini|All files (*.*)|*.*", Multiselect = true };
        if (FileDialogFolderHistory.Shared.LastNamesFolder is { } initialDirectory)
        {
            picker.InitialDirectory = initialDirectory;
        }
        if (picker.ShowDialog(this) != true) return;
        FileDialogFolderHistory.Shared.RememberNamesFiles(picker.FileNames);
        try
        {
            var imported = picker.FileNames.Sum(NameDatabase.Shared.ImportFile);
            DictionaryCount.Text = $"{NameDatabase.Shared.Count:N0} names loaded (+{imported:N0})";
            StatusText.Text = $"Imported names from {picker.FileNames.Length} file(s).";
            RefreshMergedEntries();
        }
        catch (Exception exception) { ErrorText.Text = $"Name import failed: {exception.Message}"; }
    }

    private void Options_Changed(object sender, RoutedEventArgs e) => RefreshMergedEntries();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshMergedEntries();

    private void CrcInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (CrcOutput is null || CrcInput is null) return;
        CrcOutput.Text = $"CRC ID: 0x{MixArchiveEngine.WestwoodCrc(CrcInput.Text.Trim()):X8}";
    }

    private void EntryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedEntry = EntryGrid.SelectedItem as MixEntry;
        LoadPreview(_selectedEntry);
        UpdateUnexpandButton();
    }

    private void ArchiveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedArchive = ArchiveList.SelectedItem as LoadedMixArchive;
        UpdateUnexpandButton();
    }

    private List<LoadedMixArchive> GetExpandedArchivesForSelection()
    {
        if (_selectedEntry is { IsNestedMix: true } entry)
        {
            var expanded = Archives
                .Where(archive => archive.IsExpandedChild && archive.SourceCrc == entry.CrcId && archive.FileName == entry.SuggestedFilename)
                .ToList();
            if (expanded.Count > 0) return expanded;
        }

        return _selectedArchive is { IsExpandedChild: true } selectedArchive ? [selectedArchive] : [];
    }

    private void UpdateUnexpandButton()
    {
        if (UnexpandButton is not null)
        {
            UnexpandButton.IsEnabled = GetExpandedArchivesForSelection().Count > 0;
        }
    }

    private void RefreshMergedEntries()
    {
        if (!IsInitialized) return;
        MergeResult merged;
        try { merged = BuildMerge(); }
        catch (Exception exception) { ErrorText.Text = exception.Message; return; }
        var search = SearchBox.Text.Trim();
        var filtered = merged.Entries.Where(entry => search.Length == 0 ||
            entry.DisplayLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            entry.HexId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            entry.DetectedType.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            entry.SourceArchive.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        Entries.Clear();
        foreach (var entry in filtered) Entries.Add(entry);
        MergeSummary.Text = $"{merged.Entries.Count:N0} entries  •  {FormatBytes(merged.Data.Length)}  •  {merged.DuplicatesResolved:N0} duplicate IDs";
        QueueSummary.Text = $"{Archives.Count} archives  •  {Archives.Sum(archive => archive.Entries.Count):N0} source entries";
        DictionaryCount.Text = $"{NameDatabase.Shared.Count:N0} names loaded";
        UpdateUnexpandButton();
        if (_selectedEntry is not null && Entries.All(entry => entry.CrcId != _selectedEntry.CrcId))
        {
            _selectedEntry = null;
            ClearPreview();
        }
    }

    private MergeResult BuildMerge()
    {
        var selectedPolicy = (DuplicatePolicy.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var strategy = selectedPolicy switch
        {
            "KeepFirst" => DuplicateStrategy.KeepFirst,
            "KeepAll" => DuplicateStrategy.KeepAll,
            _ => DuplicateStrategy.OverwriteLater
        };
        return MixArchiveEngine.Merge(Archives, strategy, RaFlag.IsChecked == true);
    }

    private void LoadPreview(MixEntry? entry)
    {
        StopPlayback();
        _sprite = null;
        _vqa = null;
        _frameIndex = 0;
        UpdatePlaybackButton();
        if (entry is null) { ClearPreview(); return; }
        PreviewTitle.Text = $"{entry.DisplayLabel}  •  {entry.DetectedType}";
        PreviewDetails.Text = $"{FormatBytes(entry.Payload.Length)}  •  from {entry.SourceArchive}  •  {entry.HexId}";
        FrameLabel.Text = string.Empty;
        _palettes.Clear();
        _palettes.Add(new PaletteChoice("Built-in Red Alert Snow", SpriteDecoder.BuiltInSnowPalette));
        _palettes.Add(new PaletteChoice("Built-in Red Alert Temperate", SpriteDecoder.BuiltInTemperatePalette));
        foreach (var candidate in Entries.Where(item => item.Payload.Length >= 768 && item.Payload.Length <= 1024 * 1024 && (item.Payload.Length == 768 || item.KnownName?.EndsWith(".PAL", StringComparison.OrdinalIgnoreCase) == true)))
            _palettes.Add(new PaletteChoice(candidate.KnownName ?? candidate.HexId, candidate.Payload[..768]));
        PalettePicker.SelectedIndex = 0;

        try
        {
            if (SpriteDecoder.IsSHP(entry.Payload))
            {
                _sprite = SpriteDecoder.Decode(entry.Payload);
                if (_sprite is not null) ShowRaster(_sprite.RenderFrame(0, SelectedPalette));
                FrameLabel.Text = _sprite is null ? string.Empty : $"Frame 1 / {_sprite.FrameCount}";
                UpdatePlaybackButton();
                return;
            }
            if (AudioDecoder.IsVQA(entry.Payload))
            {
                _vqa = AudioDecoder.DecodeVQA(entry.Payload);
                if (_vqa is not null)
                {
                    ShowRaster(_vqa.RenderFrame(0, entry.Payload));
                    FrameLabel.Text = $"Frame 1 / {_vqa.FrameCount}  •  {_vqa.Fps} FPS";
                    PreviewDetails.Text += $"  •  {_vqa.Width}×{_vqa.Height}";
                }
                UpdatePlaybackButton();
                return;
            }
            if (entry.Payload.Length == 768 || entry.KnownName?.EndsWith(".PAL", StringComparison.OrdinalIgnoreCase) == true)
            {
                ShowPalettePreview(entry.Payload);
                return;
            }
            if (AudioDecoder.IsAUD(entry.Payload))
            {
                var wav = AudioDecoder.DecodeAUD(entry.Payload);
                PreviewDetails.Text += wav is null ? "  •  Audio decode failed" : "  •  decoded Westwood audio";
                if (wav is not null) _playingPath = WriteTempWav(wav);
            }
        }
        catch (Exception exception) { ErrorText.Text = $"Preview failed: {exception.Message}"; }
    }

    private byte[] SelectedPalette => (PalettePicker.SelectedItem as PaletteChoice)?.Data ?? SpriteDecoder.BuiltInSnowPalette;

    private void PalettePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedEntry is null) return;
        if (_sprite is not null) ShowRaster(_sprite.RenderFrame(_frameIndex, SelectedPalette));
        else if (_vqa is not null) ShowRaster(_vqa.RenderFrame(_frameIndex, _selectedEntry.Payload));
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        var count = _sprite?.FrameCount ?? _vqa?.FrameOffsets.Length ?? 0;
        if (count == 0) return;
        _frameIndex = (_frameIndex - 1 + count) % count;
        UpdateCurrentFrame();
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        var count = _sprite?.FrameCount ?? _vqa?.FrameOffsets.Length ?? 0;
        if (count == 0) return;
        _frameIndex = (_frameIndex + 1) % count;
        UpdateCurrentFrame();
    }

    private void UpdateCurrentFrame()
    {
        if (_sprite is not null)
        {
            ShowRaster(_sprite.RenderFrame(_frameIndex, SelectedPalette));
            FrameLabel.Text = $"Frame {_frameIndex + 1} / {_sprite.FrameCount}";
        }
        else if (_vqa is not null && _selectedEntry is not null)
        {
            ShowRaster(_vqa.RenderFrame(_frameIndex, _selectedEntry.Payload));
            FrameLabel.Text = $"Frame {_frameIndex + 1} / {_vqa.FrameCount}  •  {_vqa.Fps} FPS";
        }
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        var frameCount = _sprite?.FrameCount ?? _vqa?.FrameOffsets.Length ?? 0;
        if (frameCount <= 1) return;

        if (_frameIndex + 1 >= frameCount)
        {
            if (_sprite is not null)
            {
                _frameIndex = 0;
            }
            else
            {
                _animationTimer.Stop();
                _audioPlayer.Stop();
                _audioPlayer.Position = TimeSpan.Zero;
                _isPlaybackRunning = false;
                UpdatePlaybackButton();
                return;
            }
        }
        else
        {
            _frameIndex++;
        }

        UpdateCurrentFrame();
    }

    private void ExportImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null || (_sprite is null && _vqa is null)) return;
        var exportChoice = _sprite is { FrameCount: > 1 }
            ? MessageBox.Show(this, "Export all sprite frames as one PNG sprite sheet? Choose No to export the current frame.", "Export sprite", MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBoxResult.No;
        if (exportChoice == MessageBoxResult.Cancel) return;
        var saveSheet = exportChoice == MessageBoxResult.Yes;
        var raster = saveSheet ? _sprite!.RenderSpriteSheet(SelectedPalette) : _sprite is not null ? _sprite.RenderFrame(_frameIndex, SelectedPalette) : _vqa!.RenderFrame(_frameIndex, _selectedEntry.Payload);
        var picker = new SaveFileDialog { Title = "Export preview image", FileName = $"{_selectedEntry.HexId}_{(_frameIndex + 1):D4}.png", Filter = "PNG image (*.png)|*.png" };
        if (picker.ShowDialog(this) != true) return;
        SavePng(raster, picker.FileName);
        StatusText.Text = $"Exported {Path.GetFileName(picker.FileName)}.";
    }

    private void TogglePlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null) return;

        if (_sprite is { FrameCount: > 1 } || _vqa is { FrameCount: > 1 })
        {
            if (_isPlaybackRunning)
            {
                _animationTimer.Stop();
                if (_audioLoaded) _audioPlayer.Pause();
                _isPlaybackRunning = false;
            }
            else
            {
                var fps = Math.Clamp(_vqa?.Fps ?? 15, 1, 60);
                _animationTimer.Interval = TimeSpan.FromSeconds(1d / fps);
                if (_vqa is not null && _frameIndex >= _vqa.FrameOffsets.Length - 1) _frameIndex = 0;
                _isPlaybackRunning = true;
                _animationTimer.Start();
                StartVqaAudioIfAvailable();
            }
            UpdatePlaybackButton();
            return;
        }

        if (_isPlaybackRunning)
        {
            _audioPlayer.Pause();
            _isPlaybackRunning = false;
            UpdatePlaybackButton();
            return;
        }

        try
        {
            if (!_audioLoaded)
            {
                var wav = _vqa?.WavAudioData ?? (_selectedEntry.DetectedType == "WAV / RIFF" ? _selectedEntry.Payload : AudioDecoder.DecodeAUD(_selectedEntry.Payload));
                if (wav is null) { ErrorText.Text = "No playable audio is available for this entry."; return; }
                _playingPath ??= WriteTempWav(wav);
                _audioPlayer.Open(new Uri(_playingPath));
                _audioLoaded = true;
            }
            _audioPlayer.Play();
            _isPlaybackRunning = true;
            UpdatePlaybackButton();
        }
        catch (Exception exception) { ErrorText.Text = $"Playback failed: {exception.Message}"; }
    }

    private void ExportWav_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null) return;
        var wav = _vqa?.WavAudioData ?? (_selectedEntry.DetectedType == "WAV / RIFF" ? _selectedEntry.Payload : AudioDecoder.DecodeAUD(_selectedEntry.Payload));
        if (wav is null) { ErrorText.Text = "This entry does not contain decodable audio."; return; }
        var picker = new SaveFileDialog { Title = "Export decoded WAV", FileName = $"{_selectedEntry.HexId}.WAV", Filter = "WAV audio (*.wav)|*.wav" };
        if (picker.ShowDialog(this) != true) return;
        File.WriteAllBytes(picker.FileName, wav);
        StatusText.Text = $"Exported decoded WAV: {Path.GetFileName(picker.FileName)}.";
    }

    private void StartVqaAudioIfAvailable()
    {
        if (_vqa?.WavAudioData is not { } wav) return;
        try
        {
            if (!_audioLoaded)
            {
                _playingPath ??= WriteTempWav(wav);
                _audioPlayer.Open(new Uri(_playingPath));
                _audioLoaded = true;
            }
            _audioPlayer.Play();
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"Video audio playback failed: {exception.Message}";
        }
    }

    private void ShowPalettePreview(byte[] palette)
    {
        if (palette.Length < 768) return;
        var width = 16 * 28; var height = 16 * 28; var rgba = new byte[width * height * 4];
        for (var index = 0; index < 256; index++)
        for (var y = 0; y < 28; y++)
        for (var x = 0; x < 28; x++)
        {
            var b = index * 3; var dst = (((index / 16) * 28 + y) * width + (index % 16) * 28 + x) * 4;
            rgba[dst] = (byte)Math.Min(255, palette[b] * 255 / 63); rgba[dst + 1] = (byte)Math.Min(255, palette[b + 1] * 255 / 63); rgba[dst + 2] = (byte)Math.Min(255, palette[b + 2] * 255 / 63); rgba[dst + 3] = 255;
        }
        ShowRaster(new RasterImage(width, height, rgba));
    }

    private void ShowRaster(RasterImage raster)
    {
        var bitmap = BitmapSource.Create(raster.Width, raster.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, ToBgra(raster.Rgba), raster.Width * 4);
        bitmap.Freeze(); PreviewImage.Source = bitmap;
    }

    private static byte[] ToBgra(byte[] rgba)
    {
        var output = new byte[rgba.Length];
        for (var i = 0; i < output.Length; i += 4) { output[i] = rgba[i + 2]; output[i + 1] = rgba[i + 1]; output[i + 2] = rgba[i]; output[i + 3] = rgba[i + 3]; }
        return output;
    }

    private static void SavePng(RasterImage image, string path)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, ToBgra(image.Rgba), image.Width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static string WriteTempWav(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"CCMixStudio-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, data); return path;
    }

    private void StopPlayback()
    {
        _animationTimer.Stop();
        _audioPlayer.Stop();
        _audioPlayer.Close();
        _playingPath = null;
        _audioLoaded = false;
        _isPlaybackRunning = false;
        UpdatePlaybackButton();
    }

    private void UpdatePlaybackButton()
    {
        if (PlaybackButton is null) return;
        var hasAnimation = _sprite is { FrameCount: > 1 } || _vqa is { FrameCount: > 1 };
        var hasAudio = _vqa?.WavAudioData is not null || _selectedEntry?.DetectedType is "AUD Audio" or "WAV / RIFF";
        PlaybackButton.IsEnabled = hasAnimation || hasAudio;
        PlaybackButton.Content = _isPlaybackRunning
            ? "Pause"
            : _vqa is { FrameCount: > 1 }
                ? "Play Video"
                : _sprite is { FrameCount: > 1 }
                    ? "Play Animation"
                    : "Play Audio";
    }

    private void ClearPreview() { PreviewImage.Source = null; PreviewTitle.Text = string.Empty; PreviewDetails.Text = string.Empty; FrameLabel.Text = string.Empty; UpdatePlaybackButton(); }
    private void SetBusy(bool busy) { IsEnabled = !busy; Cursor = busy ? System.Windows.Input.Cursors.Wait : null; }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes / (1024d * 1024):0.0} MB";
    private sealed record PaletteChoice(string Name, byte[] Data) { public override string ToString() => Name; }
}
