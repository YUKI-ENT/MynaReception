using System.Text.Json;

namespace ReceptionAgent;

public sealed class AgentSettings
{
    public string OqsRoot { get; set; } = "";
    public static AgentSettings Load(string directory)
    {
        string path = Path.Combine(directory, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("設定ファイルを読み取れません。") : new();
    }
    public void Save(string directory)
    {
        string normalized = NormalizeRoot(OqsRoot);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json"), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new AgentSettings { OqsRoot = normalized }, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temp, path, true); OqsRoot = normalized;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string NormalizeRoot(string value)
    {
        string root = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathFullyQualified(root) || root.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || root.IndexOfAny(['*', '?']) >= 0)
            throw new ArgumentException("OQSフォルダーを絶対パスまたはUNCパスで指定してください。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}
