using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ReceptionAgent.Face;
using ReceptionAgent.Kiosk;
using ReceptionAgent.Reception;
using ReceptionAgent.Web;

internal static class KioskTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-kiosk-" + Guid.NewGuid().ToString("N"));
        var captures = new CaptureStore(Path.Combine(root, "captures"));
        var options = new KioskOptions(); options.Save(root);
        check(KioskOptions.Load(root).Rules.Count == 4, "kiosk console settings persist four classification rules");
        var now = new DateTimeOffset(DateTime.Today.AddHours(10));
        bool available = false;
        var sessions = new KioskSessions(Path.Combine(root, "sessions"), captures, () => KioskOptions.Load(root), () => available, () => now);
        var answers = new KioskAnswers("テスト", "2000-01-01", true, "11");
        string Command() => Guid.NewGuid().ToString("N");
        void Reject(Action action, string label)
        {
            try { action(); check(false, label); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { check(true, label); }
        }
        Reject(() => sessions.Start("device", Command(), answers), "kiosk cannot arm reader when Agent monitoring is stopped");
        available = true;
        Reject(() => sessions.Start("device", Command(), answers with { Birthdate = "2000-02-30" }), "kiosk rejects invalid birthday");
        string command = Command(); var s = sessions.Start("device", command, answers);
        check(sessions.Start("device", command, answers).Id == s.Id, "duplicate start command returns original session");
        Reject(() => sessions.Start("device", command, answers with { NameKana = "別人" }), "same command with changed answers rejected");
        Reject(() => sessions.Start("other", Command(), answers), "single face-session slot enforced across devices");
        Reject(() => sessions.Get(s.Id, "other"), "session ownership enforced");
        CaptureRecord Add(string kana, DateTime generated, bool hasChart = true)
        {
            var capture = new CaptureRecord { FileName = Command() + ".xml", ContentHash = "mock", GeneratedAt = generated,
                CapturedAt = now, Face = new(kana, new(2000, 1, 1), "0110012345"),
                Lookup = hasChart ? new([new("11", "試験", kana, new(2000, 1, 1), ["110"])], new("11", "試験", kana, new(2000, 1, 1), ["110"]), false, "mock") : new([], null, false, "mock"),
                ChartNumberFoundAtReception = hasChart, ReservationFoundAtReception = true, ReceptionNo = "7", Stage = CaptureStage.Completed };
            captures.Capture(capture, [1]); return capture;
        }
        now = now.AddSeconds(2);
        Add("テスト", s.StartedAt.LocalDateTime.AddSeconds(-1)); Add("別人", now.LocalDateTime);
        check(sessions.Get(s.Id, "device").State == KioskSessionState.WaitingForXml, "old XML and unrelated patient never adopted");
        var matching = Add("テスト", now.LocalDateTime);
        var done = sessions.Get(s.Id, "device");
        check(done.CaptureId == matching.Id && done.Category == ReceptionCategory.ReturningWithReservation && done.State == KioskSessionState.StaffHelp,
            "matching XML produces configured guidance without invoking external operations");
        var reopened = new KioskSessions(Path.Combine(root, "sessions"), captures, () => options, () => available, () => now);
        check(reopened.Get(s.Id, "device").CaptureId == matching.Id, "session and adopted XML survive Agent restart");
        sessions.End(s.Id, "device", false);
        var next = sessions.Start("device", Command(), answers);
        check(sessions.Get(next.Id, "device").CaptureId is null, "next session cannot reuse previous capture");
        now = now.AddSeconds(61);
        check(sessions.Get(next.Id, "device").State == KioskSessionState.Expired, "face-session timeout releases reader slot");
        var shutdown = sessions.Start("device", Command(), answers);
        available = false;
        check(sessions.Get(shutdown.Id, "device").State == KioskSessionState.Unavailable, "Agent monitoring stop changes Kiosk to staff guidance");
        available = true;
        var visited = sessions.Start("device", Command(), answers with { PatientId = null });
        now = now.AddSeconds(2); Add("テスト", now.LocalDateTime, false);
        check(sessions.Get(visited.Id, "device").State == KioskSessionState.StaffHelp, "reported past visit with missing chart always routes to staff");
        options = KioskOptions.Load(root); options.Rules[0].Message = "<script>literal text</script>"; options.Save(root);
        check(sessions.Get(visited.Id, "device").Options.Rules[0].Message != options.Rules[0].Message, "mid-session settings edits do not replace captured configuration");

        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        await using var server = new KioskWebServer(sessions, () => available); await server.StartAsync(port);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(server.Url) };
        var page = await client.GetAsync("/"); string html = await page.Content.ReadAsStringAsync();
        check(page.IsSuccessStatusCode && html.Contains("お誕生日（月・日）") && !html.Contains("氏名カナ") && page.Headers.CacheControl?.NoStore == true,
            "real HTTP server serves embedded Kiosk HTML with no-store headers");
        using var emptyCurrent = JsonDocument.Parse(await client.GetStringAsync("/api/current"));
        check(emptyCurrent.RootElement.GetProperty("session").ValueKind == JsonValueKind.Null, "new browser gets parseable JSON for no active session");
        check((await client.GetStringAsync("/kiosk.js")).Contains("textContent") && (await client.GetStringAsync("/kiosk.css")).Contains("[hidden]"),
            "Kiosk assets served and messages rendered as text");
        var missingHeader = await client.PostAsJsonAsync("/api/sessions", new StartKioskRequest(Command(), answers));
        check(missingHeader.StatusCode == HttpStatusCode.Forbidden, "HTTP mutations require same-origin Kiosk request header");
        client.DefaultRequestHeaders.Add("X-Kiosk-Request", "1");
        var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = JsonContent.Create(new StartKioskRequest(Command(), answers)) };
        crossOrigin.Headers.Add("Origin", "http://untrusted.invalid");
        check((await client.SendAsync(crossOrigin)).StatusCode == HttpStatusCode.Forbidden, "cross-origin requests denied");
        var started = await client.PostAsJsonAsync("/api/sessions", new StartKioskRequest(Command(), answers));
        check(started.IsSuccessStatusCode, "HTTP starts durable Kiosk session with browser device cookie");
        using var json = JsonDocument.Parse(await started.Content.ReadAsStringAsync()); string httpId = json.RootElement.GetProperty("id").GetString()!;
        var view = await client.GetStringAsync("/api/sessions/" + httpId);
        check((await client.GetStringAsync("/api/current")).Contains(httpId), "browser can recover active session after lost start response or reload");
        check(!view.Contains("answers", StringComparison.OrdinalIgnoreCase) && !view.Contains("Device"), "patient-facing response omits input, owner secret and raw XML");
        using var outsider = new HttpClient { BaseAddress = client.BaseAddress }; await outsider.GetAsync("/");
        check((await outsider.GetAsync("/api/sessions/" + httpId)).StatusCode == HttpStatusCode.NotFound, "another browser cookie cannot retrieve a session");
        check((await client.PostAsync("/api/sessions/" + httpId + "/cancel", null)).IsSuccessStatusCode,
            "HTTP cancellation succeeds without external reception operations");
        now = now.AddSeconds(3);
        var early = await client.PostAsJsonAsync("/api/sessions", new StartKioskRequest(Command()));
        using var earlyJson = JsonDocument.Parse(await early.Content.ReadAsStringAsync());
        string earlyId = earlyJson.RootElement.GetProperty("id").GetString()!;
        check(early.IsSuccessStatusCode && earlyJson.RootElement.GetProperty("needsInput").GetBoolean(), "HTTP arms XML window before any identity input");
        now = now.AddSeconds(2); var earlyCapture = Add("別の氏名", now.LocalDateTime);
        check((await client.GetStringAsync("/api/sessions/" + earlyId)).Contains("\"needsInput\":true"), "XML before answers does not expose patient identity");
        var reply = await client.PostAsJsonAsync("/api/sessions/" + earlyId + "/answers", new KioskMonthDayAnswers(1, 1, false, true));
        check(reply.IsSuccessStatusCode && sessions.List().Single(s => s.Id == earlyId).CaptureId == earlyCapture.Id,
            "month/day answers adopt already processed XML without requiring name or birth year");
        var completed = sessions.List().Single(s => s.Id == earlyId);
        check(completed.PatientId == "11" && completed.ReceptionNo == "7" && completed.ConfirmedName.Length > 0,
            "session retains identity and reception number for later name confirmation and printing");
        check((await client.PostAsJsonAsync("/api/sessions/" + earlyId + "/answers", new KioskMonthDayAnswers(1, 1, false, true))).IsSuccessStatusCode,
            "same answers after lost response replay without a second association");
        check((await client.PostAsJsonAsync("/api/sessions/" + earlyId + "/answers", new KioskMonthDayAnswers(1, 1, true, true))).StatusCode == HttpStatusCode.Conflict,
            "changed answers cannot silently replace submitted answers");
        Reject(() => sessions.SubmitAnswers(earlyId, "other", new(1, 1, false, true)), "answer submission checks device ownership");

        now = now.AddSeconds(3); var invalid = sessions.Start("invalid", Command());
        Reject(() => sessions.SubmitAnswers(invalid.Id, "invalid", new(2, 30, false, true)), "invalid month/day rejected");
        Reject(() => sessions.SubmitAnswers(invalid.Id, "invalid", new(1, 1)), "unanswered fever and reservation never default to no");
        sessions.End(invalid.Id, "invalid", true);
        now = now.AddSeconds(3); var multiple = sessions.Start("multiple", Command());
        now = now.AddSeconds(2); Add("一人目", now.LocalDateTime); Add("二人目", now.LocalDateTime);
        check(sessions.SubmitAnswers(multiple.Id, "multiple", new(1, 1, false, true)).CaptureId is null && sessions.Get(multiple.Id, "multiple").State == KioskSessionState.StaffHelp,
            "multiple same-month/day XML results require staff instead of first match");

        now = now.AddSeconds(3); var a = sessions.Start("A", Command()); var b = sessions.Start("B", Command());
        Reject(() => sessions.Start("C", Command()), "month/day flow allows at most two simultaneous sessions");
        now = now.AddSeconds(2); Add("同じ誕生日", now.LocalDateTime);
        check(sessions.SubmitAnswers(a.Id, "A", new(1, 1, false, true)).CaptureId is null, "another unfinished input defers XML association");
        sessions.SubmitAnswers(b.Id, "B", new(1, 1, false, true));
        check(sessions.Get(a.Id, "A").State == KioskSessionState.StaffHelp && sessions.Get(b.Id, "B").State == KioskSessionState.StaffHelp,
            "same birthday on two simultaneous sessions routes both to staff without association");

        now = now.AddSeconds(3); var feverSession = sessions.Start("fever", Command());
        now = now.AddSeconds(2); Add("発熱患者", now.LocalDateTime);
        check(sessions.SubmitAnswers(feverSession.Id, "fever", new(1, 1, true, true)).Message.Contains("発熱"), "fever answer overrides ordinary category guidance");
        now = now.AddSeconds(3); var reservationMismatch = sessions.Start("mismatch", Command());
        now = now.AddSeconds(2); Add("予約患者", now.LocalDateTime);
        check(sessions.SubmitAnswers(reservationMismatch.Id, "mismatch", new(1, 1, false, false)).Message.Contains("予約の回答"),
            "reported reservation and actual reservation remain distinct and mismatch requires staff");
        now = now.AddSeconds(3); var lateDuplicate = sessions.Start("late", Command());
        now = now.AddSeconds(2);
        var pending = new CaptureRecord { FileName = Command()+".xml", ContentHash = "mock", GeneratedAt = now.LocalDateTime, CapturedAt = now,
            Face = new("テスト", new(1980, 2, 29), "0110012345"), Stage = CaptureStage.Captured };
        captures.Capture(pending, [1]);
        check(sessions.SubmitAnswers(lateDuplicate.Id, "late", new(2, 29, false, false)).State == KioskSessionState.LookingUp,
            "leap birthday matches XML with different birth year and continues background lookup");
        now = now.AddSeconds(2);
        var duplicate = new CaptureRecord { FileName = Command()+".xml", ContentHash = "mock", GeneratedAt = now.LocalDateTime, CapturedAt = now,
            Face = new("別人", new(2004, 2, 29), "0110012345"), Stage = CaptureStage.Captured };
        captures.Capture(duplicate, [1]);
        check(sessions.Get(lateDuplicate.Id, "late").State == KioskSessionState.StaffHelp,
            "same birthday XML arriving during lookup prevents normal guidance");
        now = now.AddSeconds(3); var left = sessions.Start("left", Command()); var right = sessions.Start("right", Command());
        now = now.AddSeconds(2); var leftXml = Add("左の患者", now.LocalDateTime);
        var rightXml = new CaptureRecord { FileName = Command()+".xml", ContentHash = "mock", GeneratedAt = now.LocalDateTime, CapturedAt = now,
            Face = new("右の患者", new(1990, 3, 2), "0110012345"), Stage = CaptureStage.Completed,
            ChartNumberFoundAtReception = false, ReservationFoundAtReception = false };
        captures.Capture(rightXml, [1]);
        sessions.SubmitAnswers(left.Id, "left", new(1, 1, false, true));
        sessions.SubmitAnswers(right.Id, "right", new(3, 2, false, false));
        check(sessions.Get(left.Id, "left").CaptureId == leftXml.Id && sessions.Get(right.Id, "right").CaptureId == rightXml.Id,
            "different birthdays on two concurrent sessions map to separate XML records");
    }
}
