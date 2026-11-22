using System.IO;
using System.Text.Json;

namespace CCMixStudio.Windows.Core;

public sealed class FileDialogFolderHistory
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CCMixStudio",
        "file-dialog-folders.json");

    private readonly FolderSettings _settings;

    public static FileDialogFolderHistory Shared { get; } = new();

    public string? LastMixArchiveFolder => ExistingFolder(_settings.MixArchiveFolder);
    public string? LastNamesFolder => ExistingFolder(_settings.NamesFolder);

    private FileDialogFolderHistory()
    {
        try
        {
            _settings = JsonSerializer.Deserialize<FolderSettings>(File.ReadAllText(SettingsPath)) ?? new FolderSettings();
        }
        catch (IOException)
        {
            _settings = new FolderSettings();
        }
        catch (JsonException)
        {
            _settings = new FolderSettings();
        }
        catch (UnauthorizedAccessException)
        {
            _settings = new FolderSettings();
        }
    }

    public void RememberMixArchiveFiles(IEnumerable<string> paths)
    {
        _settings.MixArchiveFolder = GetFirstParentDirectory(paths);
        Save();
    }

    public void RememberNamesFiles(IEnumerable<string> paths)
    {
        _settings.NamesFolder = GetFirstParentDirectory(paths);
        Save();
    }

    private static string? GetFirstParentDirectory(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) return directory;
        }
        return null;
    }

    private static string? ExistingFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings));
        }
        catch (IOException)
        {
            // Remembering a dialog folder is optional and should not interrupt archive work.
        }
        catch (UnauthorizedAccessException)
        {
            // The next app session can continue with the default dialog location.
        }
    }

    private sealed class FolderSettings
    {
        public FolderSettings() { }

        public string? MixArchiveFolder { get; set; }
        public string? NamesFolder { get; set; }
    }
}
