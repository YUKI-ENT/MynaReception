using System.Text.Json;
using System.Text.RegularExpressions;
using iCallManager.Core;

namespace iCallManager.Bridge;

/// <summary>Durable claims prevent retrying operations with uncertain outcomes.</summary>
public sealed class FileBridge : IDisposable
{
    private sealed record Journal(string Fingerprint, BridgeResponse? Response);
    private readonly string requestDirectory, responseDirectory, processingDirectory, rejectedDirectory, journalDirectory;
    private readonly Func<BridgeRequest, CancellationToken, Task<OperationResult>> execute;
    private readonly Action<string> log;
    private readonly FileStream ownerLock;

    public FileBridge(string directory, string stateDirectory,
        Func<BridgeRequest, CancellationToken, Task<OperationResult>> execute, Action<string> log)
    {
        requestDirectory = Path.Combine(directory, "request");
        responseDirectory = Path.Combine(directory, "response");
        processingDirectory = Path.Combine(stateDirectory, "processing");
        rejectedDirectory = Path.Combine(stateDirectory, "rejected");
        journalDirectory = Path.Combine(stateDirectory, "journal");
        foreach (var folder in new[] { requestDirectory, responseDirectory, processingDirectory, rejectedDirectory, journalDirectory })
            Directory.CreateDirectory(folder);
        ownerLock = new FileStream(Path.Combine(stateDirectory, "bridge.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        this.execute = execute;
        this.log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try { await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log($"SMB受信エラー: {ex.GetType().Name}（次回再確認）"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        foreach (var path in Directory.EnumerateFiles(processingDirectory, "*.json").Take(100))
        { ct.ThrowIfCancellationRequested(); await ProcessAsync(path, ct); }
        foreach (var path in Directory.EnumerateFiles(requestDirectory, "*.json").Take(100))
        {
            ct.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(path);
            if (!ValidId(id)) { Reject(path, "要求ファイル名が不正です。"); continue; }
            string staged = Path.Combine(processingDirectory, Path.GetFileName(path));
            string temporary = staged + ".tmp";
            try
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (source.Length > 65536) { source.Close(); Reject(path, "要求サイズが64KBを超えています。"); continue; }
                using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                { source.CopyTo(target); target.Flush(true); }
                File.Move(temporary, staged);
            }
            catch (IOException) { continue; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            File.Delete(path);
            await ProcessAsync(staged, ct);
        }
    }

    private static bool ValidId(string id) => Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z") &&
        !Regex.IsMatch(id, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase);

    private void Reject(string path, string reason)
    {
        string rejected = Path.Combine(rejectedDirectory, Guid.NewGuid().ToString("N") + ".json");
        File.Copy(path, rejected);
        File.Delete(path);
        log(reason + " 要求をrejectedへ移しました。");
    }

    private async Task ProcessAsync(string path, CancellationToken ct)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        BridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(await File.ReadAllTextAsync(path, ct), AppSettings.Json);
            if (request is null || !ValidId(id) || request.RequestId != id)
            { Reject(path, "requestIdとファイル名が一致しません。"); return; }
        }
        catch (JsonException) { Reject(path, "JSON形式が不正です。tmpからjsonへのリネームで送信してください。"); return; }
        string fingerprint = request.Fingerprint();
        string journalPath = Path.Combine(journalDirectory, id + ".json");
        BridgeResponse response;
        if (File.Exists(journalPath))
        {
            var existing = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(journalPath, ct), AppSettings.Json)
                ?? throw new InvalidDataException("要求記録を読めません。");
            if (existing.Fingerprint != fingerprint) { Reject(path, "同じrequestIdに異なる内容が届きました。"); return; }
            response = existing.Response ?? Response(request, new(false, "outcome_unknown",
                "前回要求の処理完了を確認できません。再実行せず職員がiCall画面を確認してください。"));
        }
        else
        {
            WriteAtomic(journalPath, new Journal(fingerprint, null));
            OperationResult result;
            try { result = await execute(request, ct); }
            catch { result = new(false, "outcome_unknown", "要求の処理結果が不明です。再送せず職員が確認してください。"); }
            response = Response(request, result);
        }
        WriteAtomic(journalPath, new Journal(fingerprint, response));
        WriteAtomic(Path.Combine(responseDirectory, id + ".json"), response);
        File.Delete(path);
        log($"要求 {id}: {response.Code} — {response.Message}");
    }

    private static BridgeResponse Response(BridgeRequest request, OperationResult result) =>
        new(request.RequestId, result.Success, result.Code, result.Message, request.PatientId,
            result.Reservation?.ReceptionNo, result.Reservation?.PatientName, DateTimeOffset.Now, request.Fingerprint());

    private static void WriteAtomic<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, AppSettings.Json); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => ownerLock.Dispose();
}
