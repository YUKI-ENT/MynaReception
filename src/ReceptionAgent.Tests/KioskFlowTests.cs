using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ReceptionAgent.Face;
using ReceptionAgent.Kiosk;
using ReceptionAgent.Reception;
using ReceptionAgent.Web;

internal static class KioskFlowTests
{
    public static async Task Preview()
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-flow-preview-" + Guid.NewGuid().ToString("N"));
        var captures = new CaptureStore(Path.Combine(root, "captures"));
        var sessions = new KioskSessions(Path.Combine(root, "sessions"), captures, () => new KioskOptions { FaceTimeoutSeconds = 300 }, () => true);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var server = new KioskWebServer(sessions, () => true); await server.StartAsync(port);
        Console.WriteLine($"Synthetic flow preview: {server.Url} PID={Environment.ProcessId} (birthday 1/1; stops in 5 minutes)");
        var seeded = new HashSet<string>();
        var until = DateTimeOffset.Now.AddMinutes(5);
        while (DateTimeOffset.Now < until)
        {
            foreach (var session in sessions.List().Where(s => s.State == KioskSessionState.WaitingForXml && s.Variables.ContainsKey("month")))
            {
                if (!seeded.Add(session.Id)) continue;
                var now = DateTimeOffset.Now;
                var patient = new FacePatientMatch("00011", "試験患者", "テスト", new(2000, 1, 1), ["110"]);
                var capture = new CaptureRecord { FileName = Guid.NewGuid().ToString("N") + ".xml", ContentHash = "synthetic", GeneratedAt = now.LocalDateTime, CapturedAt = now,
                    Face = new("テスト", new(2000, 1, 1), "0110012345", PatientName: "試験患者"), Lookup = new([patient], patient, false, "mock"),
                    ChartNumberFoundAtReception = true, ReservationFoundAtReception = true, ReceptionNo = "1", Stage = CaptureStage.Completed };
                captures.Capture(capture, [1]);
            }
            await Task.Delay(250);
        }
    }
    public static async Task Run(Action<bool, string> check)
    {
        KioskFlow Copy() => JsonSerializer.Deserialize<KioskFlow>(JsonSerializer.Serialize(new KioskFlow()))!;
        void Reject(Action action, string label)
        { try { action(); check(false, label); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { check(true, label); } }
        new KioskFlow().Validate();
        var broken = Copy(); broken.Page("card").NextPageId = "absent";
        Reject(broken.Validate, "flow rejects missing page references");
        broken = Copy(); broken.Page("birthday").NextPageId = "card";
        Reject(broken.Validate, "flow rejects cycles");
        broken = Copy(); broken.Page("birthday").NextPageId = "lookup";
        Reject(broken.Validate, "flow cannot bypass required fever and reservation answers");
        broken = Copy(); broken.Page("reserved").Variable = "category";
        Reject(broken.Validate, "patient questions cannot overwrite system classification");
        broken = Copy(); broken.Page("card").Routes.Add(new() { Variable = "hasFever", Value = "true", NextPageId = "feverHelp" });
        Reject(broken.Validate, "flow rejects conditions on unanswered variables");
        broken = Copy(); broken.Page("feverHelp").NextStep = KioskNextStep.Finish;
        Reject(broken.Validate, "flow cannot finish before lookup");

        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-flow-" + Guid.NewGuid().ToString("N"));
        var options = new KioskOptions(); options.Save(root);
        var captures = new CaptureStore(Path.Combine(root, "captures"));
        var now = new DateTimeOffset(DateTime.Today.AddHours(10));
        var sessions = new KioskSessions(Path.Combine(root, "sessions"), captures, () => KioskOptions.Load(root), () => true, () => now);
        KioskSession Start(string device) { now = now.AddSeconds(5); return sessions.Start(device, Guid.NewGuid().ToString("N")); }
        KioskSession Answer(KioskSession s, KioskFlowAnswer a) => sessions.AnswerPage(s.Id, s.Device, a);
        KioskSession Questions(KioskSession s, string reserved, string fever)
        {
            s = Answer(s, new("card")); s = Answer(s, new("birthday", Month: 1, Day: 1));
            s = Answer(s, new("reserved", reserved)); return Answer(s, new("fever", fever));
        }
        CaptureRecord Add(bool chart = true, bool reserved = true)
        {
            now = now.AddSeconds(2);
            var patient = new FacePatientMatch("11", "試験患者", "テスト", new(2000, 1, 1), ["110"]);
            var capture = new CaptureRecord { FileName = Guid.NewGuid().ToString("N") + ".xml", ContentHash = "synthetic",
                GeneratedAt = now.LocalDateTime, CapturedAt = now, Face = new("テスト", new(2000, 1, 1), "0110012345") { PatientName = "試験患者" },
                Lookup = chart ? new([patient], patient, false, "mock") : new([], null, false, "mock"),
                ChartNumberFoundAtReception = chart, ReservationFoundAtReception = reserved, ReceptionNo = reserved ? "1" : "", Stage = CaptureStage.Completed };
            captures.Capture(capture, [1]); return capture;
        }
        var s = Start("flow");
        Reject(() => Answer(s, new("fever", "no")), "flow rejects skipping current page");
        s = Answer(s, new("card"));
        check(s.FlowPageId == "birthday" && s.Variables.Count == 0, "card completion advances once without identity data");
        check(Answer(s, new("card")).FlowPageId == "birthday", "lost page response replay is idempotent");
        Reject(() => Answer(s, new("birthday", Month: 2, Day: 30)), "flow rejects impossible birthday");
        s = Answer(s, new("birthday", Month: 1, Day: 1));
        Reject(() => Answer(s, new("birthday", Month: 2, Day: 1)), "flow rejects changing accepted page answer");
        s = Answer(s, new("reserved", "yes"));
        Reject(() => Answer(s, new("fever", "injected")), "flow rejects forged choice ID");
        Reject(() => sessions.AnswerPage(s.Id, "outsider", new("fever", "no")), "flow answers enforce session ownership");
        var reopened = new KioskSessions(Path.Combine(root, "sessions"), captures, () => KioskOptions.Load(root), () => true, () => now);
        check(reopened.Get(s.Id, s.Device).FlowPageId == "fever" && reopened.Get(s.Id, s.Device).Variables["saysReserved"] == "true",
            "page and variables survive restart");
        Add(); s = Answer(s, new("fever", "no"));
        check(s.State == KioskSessionState.Guidance && s.FlowPageId == "reservedDone" && s.MonthDayAnswers == new KioskMonthDayAnswers(1, 1, false, true),
            "default flow maps answers and confirmed reservation to returning guidance");
        check(!s.Message.Contains("カードリーダー") && s.Variables["category"] == "ReturningWithReservation", "lookup completion does not repeat reader instructions");
        var noBooking = Questions(Start("noBooking"), "no", "no");
        check(noBooking.State == KioskSessionState.StaffHelp && noBooking.FlowPageId == "noReservation" && noBooking.CaptureId is null,
            "no reservation routes to manual booking without automatic lookup or assignment");
        var fever = Questions(Start("fever"), "yes", "yes");
        check(fever.State == KioskSessionState.StaffHelp && fever.FlowPageId == "feverHelp", "fever routes to staff before lookup");
        var newcomer = Start("new"); Add(chart: false); newcomer = Questions(newcomer, "yes", "no");
        check(newcomer.State == KioskSessionState.StaffHelp && newcomer.FlowPageId == "newPatient" && newcomer.Message.Contains("医療証"),
            "new patient candidates route to questionnaire and certificate checks");
        var mismatch = Start("mismatch"); Add(reserved: false); mismatch = Questions(mismatch, "yes", "no");
        check(mismatch.State == KioskSessionState.StaffHelp && mismatch.Message.Contains("予約の回答"), "confirmed lookup mismatch overrides configurable guidance");

        options = KioskOptions.Load(root);
        options.Flow.Page("fever").NextPageId = "certificate";
        options.Flow.Pages.Add(new() { Id = "certificate", Kind = KioskPageKind.Choice, Title = "医療証をお持ちですか？", Variable = "certificate", NextPageId = "lookup",
            Choices = [new() { Id = "yes", Label = "持っています", Value = "yes" }, new() { Id = "no", Label = "持っていません", Value = "no" }] });
        options.Flow.Page("lookup").Routes.Insert(0, new() { Variable = "certificate", Value = "yes", NextPageId = "newPatient" });
        options.Save(root);
        var custom = Questions(Start("custom"), "yes", "no");
        check(custom.FlowPageId == "certificate", "custom question is loaded from saved settings");
        Add(); custom = Answer(custom, new("certificate", "yes"));
        check(custom.FlowPageId == "newPatient" && custom.State == KioskSessionState.StaffHelp && custom.Variables["certificate"] == "yes",
            "custom answer variable selects configured page before category fallback");
        check(s.Options.Flow.Pages.Count == 9, "new settings never alter an in-progress or historical session flow");

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var server = new KioskWebServer(sessions, () => true); await server.StartAsync(port);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(server.Url) };
        await client.GetAsync("/"); client.DefaultRequestHeaders.Add("X-Kiosk-Request", "1");
        check((await client.GetStringAsync("/api/display")).Contains("マイナ受付開始"), "display endpoint loads editable welcome text");
        var startResponse = await client.PostAsJsonAsync("/api/sessions", new StartKioskRequest(Guid.NewGuid().ToString("N")));
        using var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        string id = startJson.RootElement.GetProperty("id").GetString()!;
        check(startJson.RootElement.GetProperty("page").GetProperty("kind").GetString() == "Card", "HTTP exposes only current configurable page");
        var answerResponse = await client.PostAsJsonAsync($"/api/sessions/{id}/page", new KioskFlowAnswer("card"));
        using var responseJson = JsonDocument.Parse(await answerResponse.Content.ReadAsStringAsync());
        check(answerResponse.IsSuccessStatusCode && responseJson.RootElement.GetProperty("page").GetProperty("kind").GetString() == "Birthday",
            "HTTP page answer advances persisted state");
        using var outsider = new HttpClient { BaseAddress = client.BaseAddress }; await outsider.GetAsync("/"); outsider.DefaultRequestHeaders.Add("X-Kiosk-Request", "1");
        check((await outsider.PostAsJsonAsync($"/api/sessions/{id}/page", new KioskFlowAnswer("birthday", Month: 1, Day: 1))).StatusCode == HttpStatusCode.NotFound,
            "HTTP page answer cannot modify another browser session");
        sessions.End(id, sessions.List().Single(x => x.Id == id).Device, true);
    }
}
