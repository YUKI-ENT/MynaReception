using System.Text.Json;

namespace iCallManager.Core;

public sealed class AppSettings
{
    public int SyncIntervalSeconds { get; set; } = 10;
    public string WindowTitleContains { get; set; } = "- 管理画面";
    public string FrameworkId { get; set; } = "InternetExplorer";
    public string BridgeDirectory { get; set; } = Path.Combine(DataDirectory, "Bridge");
    // Zero-based cell indexes. -1 means resolve using column headers.
    public int ReceptionNoColumn { get; set; } = -1;
    public int PatientIdColumn { get; set; } = -1;
    public int PatientNameColumn { get; set; } = -1;
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iCallManager");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        if (!File.Exists(path)) new AppSettings().Save(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("設定ファイルが空です。");
        settings.Validate();
        settings.BridgeDirectory = NormalizeBridgeDirectory(settings.BridgeDirectory);
        return settings;
    }

    public void Validate()
    {
        if (SyncIntervalSeconds is < 2 or > 3600 ||
            string.IsNullOrWhiteSpace(WindowTitleContains) ||
            string.IsNullOrWhiteSpace(FrameworkId) ||
            new[] { ReceptionNoColumn, PatientIdColumn, PatientNameColumn }.Any(i => i < -1))
            throw new InvalidDataException("設定を確認してください。同期は2～3600秒、BridgeDirectoryはこのPCの絶対パスを指定します。");
        NormalizeBridgeDirectory(BridgeDirectory);
    }

    public static string NormalizeBridgeDirectory(string? value)
    {
        string path = Environment.ExpandEnvironmentVariables(value?.Trim() ?? "");
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/') || path[3..].Contains(':') ||
            path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('*') || path.Contains('?'))
            throw new InvalidDataException("API連携フォルダーは、このPCの絶対パスを指定してください（例: C:\\iCallBridge）。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string PrepareBridgeDirectory(string value)
    {
        string path = NormalizeBridgeDirectory(value);
        foreach (string folder in new[] { path, Path.Combine(path, "request"), Path.Combine(path, "response") })
        {
            Directory.CreateDirectory(folder);
            // Probe with a new private temporary file; existing requests and responses are untouched.
            using var probe = new FileStream(Path.Combine(folder, ".icall-write-test-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
        return path;
    }

    public void Save(string? path = null)
    {
        Validate();
        path = Path.GetFullPath(path ?? SettingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, this, Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
