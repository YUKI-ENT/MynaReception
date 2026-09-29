using System.Diagnostics;
using System.Text.Json;
using iCallManager.Core;

namespace iCallManager.Bridge;

/// <summary>Reusable from ReceptionAgent. Both paths reside on the iCall PC's SMB share.</summary>
public sealed class FileICallBridgeClient(string bridgeDirectory)
{
    public async Task<BridgeResponse> SendAsync(BridgeRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.RequestId, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z") ||
            System.Text.RegularExpressions.Regex.IsMatch(request.RequestId, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("requestIdの形式が不正です。", nameof(request));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        string requestPath = Path.Combine(bridgeDirectory, "request", request.RequestId + ".json");
        string responsePath = Path.Combine(bridgeDirectory, "response", request.RequestId + ".json");
        string temporary = Path.Combine(bridgeDirectory, "request", request.RequestId + "." + Guid.NewGuid().ToString("N") + ".tmp");
        // Each new logical operation needs a unique ID. Only an identical retry may reuse an ID.
        if (!File.Exists(responsePath) && !File.Exists(requestPath))
        {
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, request, AppSettings.Json, ct);
                    await stream.FlushAsync(ct);
                }
                try { File.Move(temporary, requestPath); }
                catch (IOException) when (File.Exists(requestPath)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(responsePath))
            {
                var response = JsonSerializer.Deserialize<BridgeResponse>(await File.ReadAllTextAsync(responsePath, ct), AppSettings.Json)
                    ?? throw new InvalidDataException("応答が空です。");
                if (response.RequestId != request.RequestId || response.PatientId != request.PatientId ||
                    response.RequestFingerprint != request.Fingerprint())
                    throw new InvalidDataException("要求と応答が一致しません。");
                return response;
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"要求 {request.RequestId} の応答待ちがタイムアウトしました。要求は取り消されていません。同じIDの応答を再確認してください。");
    }
}
