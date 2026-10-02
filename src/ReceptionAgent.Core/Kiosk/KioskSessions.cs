using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReceptionAgent.Face;
using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

public enum KioskSessionState { WaitingForXml, LookingUp, Guidance, StaffHelp, Cancelled, Expired, Unavailable, Closed }
public sealed record KioskAnswers(string NameKana, string Birthdate, bool HasVisitedBefore, string? PatientId = null);
public sealed class KioskSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Device { get; set; } = "";
    public string CommandId { get; set; } = "";
    public KioskAnswers Answers { get; set; } = new("", "", false);
    public KioskOptions Options { get; set; } = new();
    public KioskSessionState State { get; set; } = KioskSessionState.WaitingForXml;
    public long Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? CaptureId { get; set; }
    public ReceptionCategory Category { get; set; } = ReceptionCategory.Pending;
    public string Title { get; set; } = "カードを置いてください";
    public string Message { get; set; } = "顔認証カードリーダーを操作してください。";
}

public sealed class KioskSessions
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly CaptureStore captures;
    private readonly Func<KioskOptions> options;
    private readonly Func<bool> available;
    private readonly Func<DateTimeOffset> clock;
    public KioskSessions(string directory, CaptureStore captures, Func<KioskOptions> options, Func<bool> available, Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "kiosk.sqlite3") }.ToString();
        this.captures = captures; this.options = options; this.available = available; this.clock = clock ?? (() => DateTimeOffset.Now);
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, device TEXT NOT NULL, command_id TEXT NOT NULL, capture_id TEXT UNIQUE, data TEXT NOT NULL, UNIQUE(device,command_id)); CREATE UNIQUE INDEX IF NOT EXISTS single_active_session ON sessions((1)) WHERE json_extract(data,'$.State') IN (0,1);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public IReadOnlyList<KioskSession> List()
    {
        lock (gate)
        {
            using var connection = Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT data FROM sessions ORDER BY rowid DESC LIMIT 100";
            using var reader = cmd.ExecuteReader(); var result = new List<KioskSession>();
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<KioskSession>(reader.GetString(0))!);
            return result;
        }
    }
    private KioskSession Read(string id, string device)
    {
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM sessions WHERE id=$id AND device=$device"; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$device", device);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<KioskSession>(json)! : throw new KeyNotFoundException("セッションが見つかりません。");
    }
    public KioskSession? Current(string device)
    {
        lock (gate)
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM sessions WHERE device=$device AND json_extract(data,'$.State') IN (0,1) ORDER BY rowid DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$device", device);
            return cmd.ExecuteScalar() is string id ? Get(id, device) : null;
        }
    }
    private void Save(KioskSession session, bool insert = false)
    {
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = insert ? "INSERT INTO sessions(id,device,command_id,capture_id,data) VALUES($id,$device,$command,$capture,$data)" : "UPDATE sessions SET capture_id=$capture,data=$data WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", session.Id); cmd.Parameters.AddWithValue("$device", session.Device); cmd.Parameters.AddWithValue("$command", session.CommandId);
        cmd.Parameters.AddWithValue("$capture", (object?)session.CaptureId ?? DBNull.Value); cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(session)); cmd.ExecuteNonQuery();
    }
    public KioskSession Start(string device, string commandId, KioskAnswers answers)
    {
        lock (gate)
        {
            if (!Guid.TryParseExact(commandId, "N", out _)) throw new ArgumentException("要求IDが不正です。");
            using (var connection = Open())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT data FROM sessions WHERE device=$device AND command_id=$command";
                cmd.Parameters.AddWithValue("$device", device); cmd.Parameters.AddWithValue("$command", commandId);
                if (cmd.ExecuteScalar() is string json)
                {
                    var existing = JsonSerializer.Deserialize<KioskSession>(json)!;
                    if (existing.Answers != answers) throw new InvalidOperationException("同じ要求IDの入力内容が異なります。");
                    return existing;
                }
            }
            if (!available()) throw new InvalidOperationException("ReceptionAgentの監視を開始してください。");
            if (answers is null || string.IsNullOrWhiteSpace(answers.NameKana) || answers.NameKana.Length > 100 || PatientNameMatcher.NormalizeKana(answers.NameKana).Length == 0 ||
                !DateOnly.TryParseExact(answers.Birthdate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birth) || birth > DateOnly.FromDateTime(clock().LocalDateTime) ||
                answers.PatientId is { Length: > 0 } id && (id.Length > 50 || !id.All(char.IsAsciiDigit)))
                throw new ArgumentException("氏名カナ・生年月日・診察券番号を確認してください。");
            ExpireAll();
            // Query all active sessions, not the limited console history.
            using (var connection = Open())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM sessions WHERE json_extract(data,'$.State') IN (0,1)";
                if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) throw new InvalidOperationException("別の受付がカード操作・照会中です。少しお待ちください。");
            }
            var settings = options(); settings.Validate();
            var session = new KioskSession { Device = device, CommandId = commandId, Answers = answers,
                StartedAt = clock(), ExpiresAt = clock().AddSeconds(settings.FaceTimeoutSeconds), Options = settings };
            Save(session, true); return session;
        }
    }
    private void ExpireAll()
    {
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM sessions WHERE json_extract(data,'$.State') IN (0,1)";
        var active = new List<KioskSession>(); using (var reader = cmd.ExecuteReader()) while (reader.Read()) active.Add(JsonSerializer.Deserialize<KioskSession>(reader.GetString(0))!);
        foreach (var session in active) if (clock() >= session.ExpiresAt) { session.State = KioskSessionState.Expired; session.Title = "時間切れ"; session.Message = "受付へお声掛けください。"; session.Version++; Save(session); }
    }
    public KioskSession Get(string id, string device)
    {
        lock (gate)
        {
            var session = Read(id, device);
            if (session.State is not (KioskSessionState.WaitingForXml or KioskSessionState.LookingUp)) return session;
            if (!available()) { session.State = KioskSessionState.Unavailable; session.Title = "受付へお声掛けください"; session.Message = "現在、この端末での受付をご案内できません。"; }
            else if (clock() >= session.ExpiresAt) { session.State = KioskSessionState.Expired; session.Title = "時間切れ"; session.Message = "受付へお声掛けください。"; }
            else
            {
                if (session.CaptureId is null)
                {
                    var birth = DateOnly.ParseExact(session.Answers.Birthdate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var days = new[] { DateOnly.FromDateTime(session.StartedAt.LocalDateTime), DateOnly.FromDateTime(session.ExpiresAt.LocalDateTime) }.Distinct();
                    var matches = days.SelectMany(day => captures.List(limit: int.MaxValue, date: day)).Where(r =>
                        !r.Conflict && r.Face is not null && r.CapturedAt > session.StartedAt && r.CapturedAt <= session.ExpiresAt &&
                        r.GeneratedAt >= session.StartedAt.LocalDateTime && r.GeneratedAt <= session.ExpiresAt.LocalDateTime &&
                        r.Face.Birthdate == birth && PatientNameMatcher.NormalizeKanaForMatching(r.Face.NameKana) == PatientNameMatcher.NormalizeKanaForMatching(session.Answers.NameKana)).ToArray();
                    if (matches.Length > 1) { session.State = KioskSessionState.StaffHelp; session.Title = "受付で確認します"; session.Message = "複数の読取結果があります。職員へお声掛けください。"; }
                    else if (matches.Length == 1)
                    {
                        var capture = matches[0];
                        if (!string.IsNullOrWhiteSpace(session.Answers.PatientId) &&
                            (!string.IsNullOrWhiteSpace(capture.Face!.ReferenceNumber) && capture.Face.ReferenceNumber != session.Answers.PatientId ||
                             capture.Lookup?.Selected is { } patient && patient.PatientId != session.Answers.PatientId))
                        { session.State = KioskSessionState.StaffHelp; session.Title = "受付で確認します"; session.Message = "診察券番号との照合が必要です。"; }
                        else
                        {
                            using var connection = Open(); using var cmd = connection.CreateCommand();
                            cmd.CommandText = "SELECT count(*) FROM sessions WHERE capture_id=$id"; cmd.Parameters.AddWithValue("$id", capture.Id);
                            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
                            { session.CaptureId = capture.Id; session.State = KioskSessionState.LookingUp; session.Title = "予約を確認しています"; session.Message = "そのままお待ちください。"; session.ExpiresAt = clock().AddSeconds(session.Options.LookupTimeoutSeconds); }
                        }
                    }
                }
                if (session.CaptureId is { } captureId)
                {
                    var capture = captures.Get(captureId); session.Category = capture.ReceptionCategory;
                    if (session.Category != ReceptionCategory.Pending)
                    {
                        bool review = session.Category == ReceptionCategory.NeedsReview || session.Answers.HasVisitedBefore && capture.ChartNumberFoundAtReception == false ||
                            !string.IsNullOrWhiteSpace(session.Answers.PatientId) && capture.Lookup?.Selected?.PatientId != session.Answers.PatientId;
                        if (review) { session.State = KioskSessionState.StaffHelp; session.Title = "受付で確認します"; session.Message = "患者情報の確認が必要です。受付へお声掛けください。"; }
                        else
                        {
                            var rule = session.Options.Rules.Single(r => r.Category == session.Category);
                            session.Title = rule.Title; session.Message = rule.Message;
                            session.State = rule.NextStep == KioskNextStep.Finish ? KioskSessionState.Guidance : KioskSessionState.StaffHelp;
                        }
                    }
                }
            }
            session.Version++; Save(session); return session;
        }
    }
    public KioskSession End(string id, string device, bool cancel)
    {
        lock (gate)
        {
            var session = Read(id, device);
            if (session.State is KioskSessionState.Cancelled or KioskSessionState.Closed) return session;
            if (!cancel && session.State is KioskSessionState.WaitingForXml or KioskSessionState.LookingUp) throw new InvalidOperationException("照会中は終了できません。");
            session.State = cancel ? KioskSessionState.Cancelled : KioskSessionState.Closed;
            session.Title = "終了しました"; session.Message = ""; session.Version++; Save(session); return session;
        }
    }
}
