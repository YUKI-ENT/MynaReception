using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ReceptionAgent.Face;

namespace ReceptionAgent.Reception;

public sealed class FaceCaptureMonitor(CaptureStore store, AgentSettings settings, Func<FaceIdentity, CancellationToken, Task<FaceLookupResult>> lookup)
{
    private readonly ConcurrentDictionary<string, (long, DateTime)> seen = new(StringComparer.OrdinalIgnoreCase);
    public string CaptureStatus { get; private set; } = "監視開始";
    public async Task RunAsync(CancellationToken token)
    {
        settings.ValidateMonitoring();
        using var lease = store.AcquireMonitorLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task Guard(Func<CancellationToken, Task> work)
        { try { await work(linked.Token); } finally { linked.Cancel(); } }
        await Task.WhenAll(Guard(ScanLoop), Guard(ProcessLoop));
    }
    public static bool TryTimestamp(string name, out DateTime timestamp)
    {
        var match = Regex.Match(name, @"\AOQSsiquc01res_face_.+_(\d{14})\.xml\z", RegexOptions.IgnoreCase);
        return DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }
    private async Task ScanLoop(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); var errors = new List<string>();
            foreach (string folder in new[] { settings.FaceXmlDirectory, settings.FaceTrashDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var paths = Directory.GetFiles(folder, "OQSsiquc01res_face_*.xml");
                    await Parallel.ForEachAsync(paths, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (path, ct) =>
                    {
                        if (!TryTimestamp(Path.GetFileName(path), out var time) || time.Date != DateTime.Today) return;
                        try
                        {
                            var info = new FileInfo(path); var stamp = (info.Length, info.LastWriteTimeUtc);
                            if (seen.TryGetValue(path, out var previous) && previous == stamp) return;
                            await CaptureAsync(path, time, ct); seen[path] = stamp;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                        { lock (errors) errors.Add("XML読取待ち: " + Path.GetFileName(path) + "（" + ex.Message + "）"); }
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add("監視フォルダー確認: " + folder + "（" + ex.Message + "）"); }
            }
            CaptureStatus = errors.Count == 0 ? "監視中（当日分・出力先＋trash）" : errors[0];
            await Task.Delay(1000, token);
        }
    }
    public async Task<bool> CaptureAsync(string path, DateTime generatedAt, CancellationToken token)
    {
        byte[]? previous = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            token.ThrowIfCancellationRequested();
            string actual = File.Exists(path) ? path : Path.Combine(settings.FaceTrashDirectory, Path.GetFileName(path));
            try
            {
                DateTime created = File.GetCreationTimeUtc(actual);
                using var output = new MemoryStream();
                using (var stream = new FileStream(actual, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buffer = new byte[8192]; int read;
                    while ((read = await stream.ReadAsync(buffer, token)) > 0)
                    { if (output.Length + read > 4 * 1024 * 1024) throw new IOException("XMLサイズ上限を超えています。"); output.Write(buffer, 0, read); }
                }
                byte[] bytes = output.ToArray();
                if (previous is not null && previous.AsSpan().SequenceEqual(bytes))
                {
                    FaceIdentity? face = null; string? error = null;
                    try { face = FaceXmlParser.Parse(bytes, settings.FaceEncoding); }
                    catch (System.Xml.XmlException) { previous = bytes; await Task.Delay(300, token); continue; }
                    catch (Exception ex) when (ex is InvalidDataException or System.Text.DecoderFallbackException or ArgumentException) { error = ex.Message; }
                    return store.Capture(new CaptureRecord { FileName = Path.GetFileName(path), ContentHash = Convert.ToHexString(SHA256.HashData(bytes)),
                        SourcePath = actual, GeneratedAt = generatedAt, FileCreatedAt = new DateTimeOffset(created), Encoding = settings.FaceEncoding,
                        Face = face, XmlPatientName = face?.PatientName ?? "", RequestDirectory = settings.ICallRequestDirectory, ResponseDirectory = settings.ICallResponseDirectory,
                        MarkArrived = settings.MarkArrived, LinkReservation = settings.LinkReservation,
                        Stage = error is null ? CaptureStage.Captured : CaptureStage.NeedsReview, Status = error ?? "XML取得済み" }, bytes);
                }
                previous = bytes;
            }
            catch (IOException) { previous = null; }
            await Task.Delay(300, token);
        }
        throw new IOException("書込み完了またはtrashへの移動を待っています。");
    }
    private async Task ProcessLoop(CancellationToken token)
    {
        var workflow = new ReceptionWorkflow(store, lookup);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            foreach (var record in store.List(true).Where(r => r.RetryAfter <= DateTimeOffset.Now))
            {
                token.ThrowIfCancellationRequested();
                try { await workflow.StepAsync(record, token); }
                catch (InvalidDataException) { /* Concurrent capture detected conflicting file content; keep it halted. */ }
            }
            await Task.Delay(200, token);
        }
    }
}
