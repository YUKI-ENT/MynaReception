using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ReceptionAgent.Face;
using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

public enum KioskSessionState { WaitingForXml, LookingUp, Guidance, StaffHelp, Cancelled, Expired, Unavailable, Closed }
public sealed record KioskAnswers(string NameKana, string Birthdate, bool HasVisitedBefore, string? PatientId = null);
public sealed record KioskMonthDayAnswers(int Month, int Day, bool? HasFever = null, bool? SaysReserved = null);
public sealed class KioskSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Device { get; set; } = "";
    public string CommandId { get; set; } = "";
    public KioskAnswers Answers { get; set; } = new("", "", false);
    public bool MonthDayOnly { get; set; }
    public KioskMonthDayAnswers? MonthDayAnswers { get; set; }
    public DateTimeOffset? InputCompletedAt { get; set; }
    public string ConfirmedName { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string ReceptionNo { get; set; } = "";
    public KioskOptions Options { get; set; } = new();
    public string? FlowPageId { get; set; }
    public bool UsesFlow { get; set; }
    public Dictionary<string, string> Variables { get; set; } = [];
    public Dictionary<string, KioskFlowAnswer> FlowAnswers { get; set; } = [];
    public KioskSessionState State { get; set; } = KioskSessionState.WaitingForXml;
    public long Version { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? XmlWindowExpiresAt { get; set; }
    public string? CaptureId { get; set; }
    public ReceptionCategory Category { get; set; } = ReceptionCategory.Pending;
    public string Title { get; set; } = "カードを置いてください";
    public string Message { get; set; } = "顔認証カードリーダーを操作してください。";
    public KioskAutomationJob? Automation { get; set; }
}

public sealed class KioskSessions
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly CaptureStore captures;
    private readonly Func<KioskOptions> options;
    private readonly Func<bool> available;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<AgentSettings> receptionSettings;
    private readonly string automationLockPath;
    public KioskSessions(string directory, CaptureStore captures, Func<KioskOptions> options, Func<bool> available, Func<DateTimeOffset>? clock = null, Func<AgentSettings>? receptionSettings = null)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "kiosk.sqlite3") }.ToString();
        this.captures = captures; this.options = options; this.available = available; this.clock = clock ?? (() => DateTimeOffset.Now);
        this.receptionSettings = receptionSettings ?? (() => new AgentSettings());
        automationLockPath = Path.Combine(directory, "automation.lock");
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, device TEXT NOT NULL, command_id TEXT NOT NULL, capture_id TEXT UNIQUE, data TEXT NOT NULL, UNIQUE(device,command_id)); DROP INDEX IF EXISTS single_active_session; CREATE UNIQUE INDEX IF NOT EXISTS active_device_session ON sessions(device) WHERE json_extract(data,'$.State') IN (0,1);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public KioskReceipt Receipt(string id, string device)
    {
        lock (gate)
        {
            var session = Get(id, device);
            if (session.Automation is { Finished: false })
                throw new InvalidOperationException("自動受付処理中です。重複発券を防ぐため、処理結果を確認してから手動印刷してください。");
            if (session.StartedAt.LocalDateTime.Date != clock().LocalDateTime.Date)
                throw new InvalidOperationException("受付済み証は当日の受付のみ印刷できます。");
            if (session.CaptureId is not { } captureId) throw new InvalidOperationException("顔認証XMLをまだ確認できていません。");
            return KioskReceipt.Create(session, captures.Get(captureId));
        }
    }
    public KioskReceptionOutput ReceptionOutput(string id, string device)
    {
        lock (gate)
        {
            var s = Get(id, device);
            var capture = s.CaptureId is { } captureId ? captures.Get(captureId) : null;
            int? Number(string key) => s.Variables.TryGetValue(key, out var value) && int.TryParse(value, out var number) ? number : null;
            bool? Flag(string key) => s.Variables.TryGetValue(key, out var value) && bool.TryParse(value, out var flag) ? flag : null;
            return new(s.Id, s.CaptureId, capture?.FileName, string.IsNullOrEmpty(s.PatientId) ? null : s.PatientId,
                string.IsNullOrEmpty(s.ReceptionNo) ? null : s.ReceptionNo,
                s.MonthDayAnswers?.Month ?? Number("month"), s.MonthDayAnswers?.Day ?? Number("day"),
                s.MonthDayAnswers?.SaysReserved ?? Flag("saysReserved"), s.MonthDayAnswers?.HasFever ?? Flag("hasFever"),
                s.Variables.GetValueOrDefault("clinicClass"), s.Category, s.State, new Dictionary<string, string>(s.Variables));
        }
    }
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
    internal IDisposable AcquireAutomationLease() => new FileStream(automationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public bool HasPendingAutomation(string captureId)
    {
        lock (gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sessions WHERE capture_id=$capture AND json_extract(data,'$.Automation.Finished')=0";
            command.Parameters.AddWithValue("$capture", captureId);
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
    }
    internal IReadOnlyList<KioskSession> AutomationCandidates()
    {
        lock (gate)
        {
            foreach (var active in Active()) _ = Get(active.Id, active.Device);
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM sessions WHERE json_extract(data,'$.Automation.Finished')=0";
            using var reader = command.ExecuteReader(); var result = new List<KioskSession>();
            while (reader.Read())
            {
                var session = JsonSerializer.Deserialize<KioskSession>(reader.GetString(0))!;
                if (session.Automation is { Finished: false }) result.Add(session);
            }
            return result;
        }
    }
    internal void SaveAutomation(string id, string device, KioskAutomationJob job)
    {
        lock (gate)
        {
            var session = Read(id, device); session.Automation = job;
            if (job.NeedsReview) ToStaff(session, "受付処理を職員が確認します。受付へお声掛けください。");
            else if (job.Finished)
            {
                session.Title = "受付が完了しました";
                session.Message = job.Print == KioskAutoActionState.Completed ? "発券された受付済み証をお取りください。" : "職員の案内をお待ちください。";
            }
            session.Version++; Save(session);
        }
    }
    public KioskSession ConfirmAutomationReview(string id, string device)
    {
        lock (gate)
        {
            var session = Read(id, device);
            if (session.Automation is not { NeedsReview: true } job) throw new InvalidOperationException("職員確認待ちの自動受付を選択してください。");
            session.State = KioskSessionState.Guidance;
            if (session.CaptureId is not { } captureId || !KioskAutomationEligibility.IsEligible(session, captures.Get(captureId), clock()))
                throw new InvalidOperationException("患者・予約の不整合が残っています。元の受付を確認してください。");
            KioskAutoActionState Resolve(KioskAutoActionState state) => state == KioskAutoActionState.Completed ? state : KioskAutoActionState.Skipped;
            job.Link = Resolve(job.Link); job.Arrival = Resolve(job.Arrival); job.Print = Resolve(job.Print);
            job.NeedsReview = false; job.Message = "職員確認済み／未完了操作は手動対応（自動再送なし）";
            session.Title = "受付結果を職員が確認しました"; session.Message = "職員の案内をお待ちください。";
            session.Version++; Save(session); return session;
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
    public KioskSession Start(string device, string commandId, KioskAnswers? answers = null)
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
                    if (existing.MonthDayOnly != (answers is null) || answers is not null && existing.Answers != answers)
                        throw new InvalidOperationException("同じ要求IDの入力内容が異なります。");
                    return existing;
                }
            }
            if (!available()) throw new InvalidOperationException("ReceptionAgentの監視を開始してください。");
            if (answers is not null && (string.IsNullOrWhiteSpace(answers.NameKana) || answers.NameKana.Length > 100 || PatientNameMatcher.NormalizeKana(answers.NameKana).Length == 0 ||
                !DateOnly.TryParseExact(answers.Birthdate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birth) || birth > DateOnly.FromDateTime(clock().LocalDateTime) ||
                answers.PatientId is { Length: > 0 } id && (id.Length > 50 || !id.All(char.IsAsciiDigit))))
                throw new ArgumentException("氏名カナ・生年月日・診察券番号を確認してください。");
            ExpireAll();
            // Query all active sessions, not the limited console history.
            using (var connection = Open())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT data FROM sessions WHERE json_extract(data,'$.State') IN (0,1)";
                var active = new List<KioskSession>();
                using (var reader = cmd.ExecuteReader()) while (reader.Read()) active.Add(JsonSerializer.Deserialize<KioskSession>(reader.GetString(0))!);
                if (active.Any(s => s.Device == device) || active.Count >= 2 || active.Count > 0 && (answers is not null || active.Any(s => !s.MonthDayOnly)))
                    throw new InvalidOperationException("別の受付がカード操作・照会中です。少しお待ちください。");
            }
            var settings = options(); settings.Validate();
            var session = new KioskSession { Device = device, CommandId = commandId, Answers = answers ?? new("", "", false), MonthDayOnly = answers is null,
                StartedAt = clock(), ExpiresAt = clock().AddSeconds(settings.FaceTimeoutSeconds), Options = settings };
            session.XmlWindowExpiresAt = session.ExpiresAt;
            if (session.MonthDayOnly) session.FlowPageId = settings.Flow.FirstPageId;
            Save(session, true); return session;
        }
    }
    public KioskOptions DisplayOptions() { var settings = options(); settings.Validate(); return settings; }
    public KioskSession AnswerPage(string id, string device, KioskFlowAnswer answer)
    {
        lock (gate)
        {
            var session = Get(id, device);
            if (answer is null || string.IsNullOrWhiteSpace(answer.PageId)) throw new ArgumentException("ページを指定してください。");
            if (session.FlowAnswers.TryGetValue(answer.PageId, out var previous))
            {
                if (previous != answer) throw new InvalidOperationException("回答済みのページは変更できません。");
                return session;
            }
            if (!session.MonthDayOnly || session.State != KioskSessionState.WaitingForXml || session.MonthDayAnswers is not null || session.FlowPageId != answer.PageId)
                throw new InvalidOperationException("現在表示しているページから回答してください。");
            var page = session.Options.Flow.Page(answer.PageId);
            if (page.Kind == KioskPageKind.Birthday)
            {
                if (answer.ChoiceId is not null || answer.Month is not (>= 1 and <= 12) || answer.Day is null || answer.Day < 1 || answer.Day > DateTime.DaysInMonth(2000, answer.Month.Value))
                    throw new ArgumentException("誕生月日を確認してください。");
                session.Variables["month"] = answer.Month.Value.ToString(CultureInfo.InvariantCulture);
                session.Variables["day"] = answer.Day.Value.ToString(CultureInfo.InvariantCulture);
            }
            else if (page.Kind == KioskPageKind.Choice)
            {
                var choice = page.Choices.SingleOrDefault(c => c.Id == answer.ChoiceId);
                if (choice is null || answer.Month is not null || answer.Day is not null) throw new ArgumentException("選択肢を確認してください。");
                session.Variables[page.Variable] = choice.Value;
            }
            else if (page.Kind != KioskPageKind.Card || answer.ChoiceId is not null || answer.Month is not null || answer.Day is not null)
                throw new ArgumentException("このページには回答できません。");
            session.UsesFlow = true;
            session.FlowAnswers[page.Id] = answer;
            var next = session.Options.Flow.Page(session.Options.Flow.Next(page, session.Variables));
            session.FlowPageId = next.Id;
            session.Title = next.Title; session.Message = next.Message; session.Version++;
            if (next.Kind == KioskPageKind.Guidance) ApplyFlowGuidance(session, next);
            Save(session);
            if (next.Kind == KioskPageKind.Lookup)
                return SubmitAnswers(id, device, new(int.Parse(session.Variables["month"], CultureInfo.InvariantCulture), int.Parse(session.Variables["day"], CultureInfo.InvariantCulture),
                    bool.Parse(session.Variables["hasFever"]), bool.Parse(session.Variables["saysReserved"])));
            return session;
        }
    }
    private static void ApplyFlowGuidance(KioskSession session, KioskFlowPage page)
    {
        session.FlowPageId = page.Id; session.Title = page.Title; session.Message = page.Message;
        session.State = page.NextStep == KioskNextStep.Finish && session.Category == ReceptionCategory.ReturningWithReservation
            ? KioskSessionState.Guidance : KioskSessionState.StaffHelp;
    }
    public KioskSession SubmitAnswers(string id, string device, KioskMonthDayAnswers answers)
    {
        lock (gate)
        {
            var session = Read(id, device);
            if (!session.MonthDayOnly) throw new InvalidOperationException("このセッションは月日入力方式ではありません。");
            if (answers is null || answers.HasFever is null || answers.SaysReserved is null || answers.Month is < 1 or > 12 || answers.Day < 1 || answers.Day > DateTime.DaysInMonth(2000, answers.Month))
                throw new ArgumentException("誕生月日を確認してください。");
            if (session.MonthDayAnswers is not null)
            {
                if (session.MonthDayAnswers != answers) throw new InvalidOperationException("送信済みの回答と異なります。職員へお声掛けください。");
                return Get(id, device);
            }
            session = Get(id, device);
            if (session.State != KioskSessionState.WaitingForXml) throw new InvalidOperationException("受付を再開してください。");
            session.MonthDayAnswers = answers; session.InputCompletedAt = clock(); session.Version++; Save(session);
            return Get(id, device);
        }
    }
    private void ExpireAll()
    {
        foreach (var session in Active()) if (clock() >= session.ExpiresAt) { session.State = KioskSessionState.Expired; session.Title = "時間切れ"; session.Message = "受付へお声掛けください。"; session.Version++; Save(session); }
    }
    private List<KioskSession> Active()
    {
        using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM sessions WHERE json_extract(data,'$.State') IN (0,1)";
        var active = new List<KioskSession>(); using (var reader = cmd.ExecuteReader()) while (reader.Read()) active.Add(JsonSerializer.Deserialize<KioskSession>(reader.GetString(0))!);
        return active;
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
                if (session.MonthDayOnly && session.MonthDayAnswers is null) return session;
                if (session.CaptureId is null || session.MonthDayOnly)
                {
                    var xmlDeadline = session.XmlWindowExpiresAt ?? session.ExpiresAt;
                    var birth = session.MonthDayOnly ? new DateOnly(2000, session.MonthDayAnswers!.Month, session.MonthDayAnswers.Day) :
                        DateOnly.ParseExact(session.Answers.Birthdate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var days = new[] { DateOnly.FromDateTime(session.StartedAt.LocalDateTime), DateOnly.FromDateTime(xmlDeadline.LocalDateTime) }.Distinct();
                    var matches = days.SelectMany(day => captures.List(limit: int.MaxValue, date: day)).Where(r =>
                        !r.Conflict && r.Face is not null && r.CapturedAt > session.StartedAt && r.CapturedAt <= xmlDeadline &&
                        r.GeneratedAt >= session.StartedAt.LocalDateTime && r.GeneratedAt <= xmlDeadline.LocalDateTime &&
                        (session.MonthDayOnly ? r.Face.Birthdate.Month == birth.Month && r.Face.Birthdate.Day == birth.Day :
                        r.Face.Birthdate == birth && PatientNameMatcher.NormalizeKanaForMatching(r.Face.NameKana) == PatientNameMatcher.NormalizeKanaForMatching(session.Answers.NameKana))).ToArray();
                    if (session.MonthDayOnly)
                    {
                        var others = Active().Where(s => s.ExpiresAt > clock() && s.Id != session.Id && s.MonthDayOnly && s.State is KioskSessionState.WaitingForXml or KioskSessionState.LookingUp &&
                            s.StartedAt <= session.ExpiresAt && s.ExpiresAt >= session.StartedAt).ToArray();
                        if (others.Any(s => s.MonthDayAnswers is null)) return session;
                        var conflicts = others.Where(s => s.MonthDayAnswers?.Month == birth.Month && s.MonthDayAnswers.Day == birth.Day).ToArray();
                        if (conflicts.Length > 0)
                        {
                            foreach (var other in conflicts) { ToStaff(other, "同じ誕生月日の受付が重なっています。職員へお声掛けください。"); other.Version++; Save(other); }
                            ToStaff(session, "同じ誕生月日の受付が重なっています。職員へお声掛けください。"); session.Version++; Save(session); return session;
                        }
                    }
                    if (matches.Length > 1) { session.State = KioskSessionState.StaffHelp; session.Title = "受付で確認します"; session.Message = "複数の読取結果があります。職員へお声掛けください。"; }
                    else if (matches.Length == 1 && session.CaptureId is null)
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
                            else if (session.MonthDayOnly) ToStaff(session, "読取結果は別の受付で使用されています。職員へお声掛けください。");
                        }
                    }
                }
                if (session.CaptureId is { } captureId && session.State == KioskSessionState.LookingUp)
                {
                    var capture = captures.Get(captureId); session.Category = capture.ReceptionCategory;
                    if (session.Category != ReceptionCategory.Pending && (capture.Stage is CaptureStage.Completed or CaptureStage.NeedsReview || session.Category == ReceptionCategory.NeedsReview))
                    {
                        bool review = session.Category == ReceptionCategory.NeedsReview || session.Answers.HasVisitedBefore && capture.ChartNumberFoundAtReception == false ||
                            !string.IsNullOrWhiteSpace(session.Answers.PatientId) && capture.Lookup?.Selected?.PatientId != session.Answers.PatientId;
                        if (review) { session.State = KioskSessionState.StaffHelp; session.Title = "受付で確認します"; session.Message = "患者情報の確認が必要です。受付へお声掛けください。"; }
                        else
                        {
                            session.ConfirmedName = !string.IsNullOrWhiteSpace(capture.Face?.PatientName) ? capture.Face.PatientName : capture.Lookup?.Selected?.Name ?? "";
                            session.PatientId = capture.Lookup?.Selected?.PatientId ?? ""; session.ReceptionNo = capture.ReceptionNo;
                            var rule = session.Options.Rules.Single(r => r.Category == session.Category);
                            session.Title = rule.Title; session.Message = rule.Message;
                            session.State = rule.NextStep == KioskNextStep.Finish ? KioskSessionState.Guidance : KioskSessionState.StaffHelp;
                            if (session.UsesFlow && session.FlowPageId is { } flowPage)
                            {
                                session.Variables["category"] = session.Category.ToString();
                                session.Variables["hasChart"] = capture.ChartNumberFoundAtReception == true ? "true" : "false";
                                session.Variables["actualReservation"] = capture.ReservationFoundAtReception == true ? "true" : "false";
                                var lookupPage = session.Options.Flow.Page(flowPage);
                                ApplyFlowGuidance(session, session.Options.Flow.Page(session.Options.Flow.Next(lookupPage, session.Variables)));
                            }
                            if (session.MonthDayAnswers?.HasFever == true)
                                ToStaff(session, "発熱があると回答されています。受付へお声掛けください。");
                            else if (session.MonthDayAnswers is { } input && capture.ReservationFoundAtReception != input.SaysReserved)
                                ToStaff(session, "予約の回答と照会結果の確認が必要です。受付へお声掛けください。");
                        }
                    }
                }
            }
            if (session.CaptureId is { } automaticCaptureId && session.Options.Automation.Enabled &&
                KioskAutomationEligibility.IsEligible(session, captures.Get(automaticCaptureId), clock()))
            {
                var config = receptionSettings();
                if (!Enum.IsDefined(config.ReceptionLinkMode)) throw new InvalidDataException("受付連携方式が不正です。");
                var automation = session.Options.Automation;
                session.Automation = new KioskAutomationJob
                {
                    Link = automation.AutoLink && config.ReceptionLinkMode != ReceptionLinkMode.None ? KioskAutoActionState.Pending : KioskAutoActionState.Skipped,
                    Arrival = automation.AutoArrival ? KioskAutoActionState.Pending : KioskAutoActionState.Skipped,
                    Print = automation.AutoPrint ? KioskAutoActionState.Pending : KioskAutoActionState.Skipped,
                    LinkMode = config.ReceptionLinkMode, ReceiptDirectory = config.DynamicsReceiptDirectory, PrinterName = automation.PrinterName
                };
                if (!session.Automation.Finished) { session.Title = "受付を処理しています"; session.Message = "そのままお待ちください。"; }
            }
            session.Version++; Save(session); return session;
        }
    }
    private static void ToStaff(KioskSession session, string message)
    { session.State = KioskSessionState.StaffHelp; session.Title = "受付で確認します"; session.Message = message; }
    public KioskSession End(string id, string device, bool cancel)
    {
        lock (gate)
        {
            var session = Read(id, device);
            if (session.Automation is { Finished: false }) throw new InvalidOperationException("自動受付処理中です。終了せず、そのままお待ちください。問題があれば職員へお声掛けください。");
            if (session.State is KioskSessionState.Cancelled or KioskSessionState.Closed) return session;
            if (!cancel && session.State is KioskSessionState.WaitingForXml or KioskSessionState.LookingUp) throw new InvalidOperationException("照会中は終了できません。");
            session.State = cancel ? KioskSessionState.Cancelled : KioskSessionState.Closed;
            session.Title = "終了しました"; session.Message = ""; session.Version++; Save(session); return session;
        }
    }
}
