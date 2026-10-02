using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReceptionAgent.ICall;

// Property order and serializer options are part of iCallManager's request fingerprint contract.
public sealed record ICallRequest(string RequestId, string Action, string PatientId, string? ExpectedReceptionNo = null, string? ExpectedPatientName = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PatientName { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? NameKana { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GivenName { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Birthdate { get; init; }
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, ICallFileClient.Json))));
}
public sealed record ICallResponse(string RequestId, bool Success, string Code, string Message, string? PatientId, string? ReceptionNo,
    string? PatientName, DateTimeOffset CompletedAt, string RequestFingerprint = "")
{
    public IReadOnlyList<ICallReservationCandidate>? Candidates { get; init; }
}
public sealed record ICallReservationCandidate(string ReceptionNo, string? PatientId, string PatientName,
    string ParsedName, string Birthdate, string NameMatch, bool RequiresConfirmation = true);

public sealed class ICallFileClient
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<ICallResponse> SendAsync(ICallRequest request, string requestDirectory, string responseDirectory, TimeSpan timeout, CancellationToken token)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.RequestId, @"\Ara-[a-f0-9]{32}-(find|find_candidates|arrived|link)\z"))
            throw new ArgumentException("ReceptionAgentのrequestIdが不正です。");
        if (!Directory.Exists(requestDirectory) || !Directory.Exists(responseDirectory)) throw new DirectoryNotFoundException("iCallManagerのrequest/responseフォルダーを確認してください。");
        string requestPath = Path.Combine(requestDirectory, request.RequestId + ".json"), responsePath = Path.Combine(responseDirectory, request.RequestId + ".json");
        if (File.Exists(requestPath))
        {
            var existing = await ReadAsync<ICallRequest>(requestPath, token);
            if (existing.Fingerprint() != request.Fingerprint()) throw new InvalidDataException("同じ要求IDに異なる要求があります。");
        }
        else if (!File.Exists(responsePath))
        {
            string temp = Path.Combine(requestDirectory, request.RequestId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, request, Json); stream.Flush(true); }
                token.ThrowIfCancellationRequested(); File.Move(temp, requestPath, false);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = await ReadAsync<ICallResponse>(responsePath, token);
                string? expectedPatientId = request.Action == "find_candidates" ? null : request.PatientId;
                if (response.RequestId != request.RequestId || response.PatientId != expectedPatientId || response.RequestFingerprint != request.Fingerprint())
                    throw new InvalidDataException("iCallManagerの応答が要求と一致しません。");
                return response;
            }
            catch (FileNotFoundException) { }
            catch (JsonException) { } // A partially written response is retried; never treated as a result.
            await Task.Delay(200, token).ConfigureAwait(false);
        }
        throw new TimeoutException("iCall応答待ち。同じrequestIdで結果を再確認します。");
    }
    private static async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 65536) throw new InvalidDataException("iCall応答がサイズ上限を超えています。");
        var buffer = new byte[65537]; int length = 0, read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(length), token)) > 0)
        { length += read; if (length > 65536) throw new InvalidDataException("iCall応答がサイズ上限を超えています。"); }
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), Json) ?? throw new InvalidDataException("iCall JSONが空です。");
    }
}
