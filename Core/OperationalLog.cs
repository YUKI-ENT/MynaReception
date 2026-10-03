using System.Text.Json;

namespace iCallManager.Core;

internal static class OperationalLog
{
    private static readonly object Gate = new();
    [ThreadStatic] internal static string? RequestId;
    public static string DirectoryPath => Path.Combine(AppSettings.DataDirectory, "Logs");
    public static void Write(string eventName, object detail)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, $"automation-{DateTime.Now:yyyyMMdd}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 20 * 1024 * 1024) return;
                File.AppendAllText(path, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, eventName, requestId = RequestId, detail }) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public static string? SaveFailureTree(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                string prefix = $"layout-{DateTime.Now:yyyyMMdd}-";
                if (Directory.EnumerateFiles(DirectoryPath, prefix + "*.txt").Take(10).Count() >= 10) return null;
                string path = Path.Combine(DirectoryPath, prefix + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(path, text.Length > 2_000_000 ? text[..2_000_000] + "\r\n[truncated]" : text);
                return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
