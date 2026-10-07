using System.Text.Json;
using ReceptionAgent;
using ReceptionAgent.Face;
using ReceptionAgent.ICall;
using ReceptionAgent.Kiosk;
using ReceptionAgent.Reception;

internal static class KioskAutomationTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "kiosk-auto-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Now = new(DateTime.Today.AddHours(10));
        public bool Available = true;
        public CaptureStore Store;
        public KioskSessions Sessions;
        public KioskSession Session;
        public CaptureRecord Capture;
        public AgentSettings Settings;
        public KioskOptions Options;
        public readonly List<string> Events = [];
        public Fixture(KioskAutomationOptions automation, ReceptionLinkMode mode = ReceptionLinkMode.ICall,
            bool fever = false, bool saysReserved = true, bool hasChart = true, bool actualReservation = true, string? clinic = null)
        {
            Settings = new() { ReceptionLinkMode = mode, DynamicsReceiptDirectory = Path.Combine(Root, "receipt"),
                ICallRequestDirectory = Path.Combine(Root, "req"), ICallResponseDirectory = Path.Combine(Root, "res") };
            Directory.CreateDirectory(Settings.ICallRequestDirectory); Directory.CreateDirectory(Settings.ICallResponseDirectory);
            Store = new(Path.Combine(Root, "store")); Options = new() { Automation = automation };
            if (clinic is not null)
            {
                Options.Flow.Pages.Add(new() { Id = "clinic", Kind = KioskPageKind.Choice, Title = "外来区分", Variable = "clinicClass", NextPageId = "reserved",
                    Choices = [new() { Id = "general", Label = "一般診療", Value = "general" }, new() { Id = "vaccination", Label = "予防接種", Value = "vaccination" }] });
                Options.Flow.Page("birthday").NextPageId = "clinic";
            }
            Sessions = Reopen();
            Session = Sessions.Start(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            Sessions.AnswerPage(Session.Id, Session.Device, new("card"));
            Sessions.AnswerPage(Session.Id, Session.Device, new("birthday", Month: 1, Day: 1));
            if (clinic is not null) Sessions.AnswerPage(Session.Id, Session.Device, new("clinic", clinic));
            Sessions.AnswerPage(Session.Id, Session.Device, new("reserved", saysReserved ? "yes" : "no"));
            Now = Now.AddSeconds(2);
            var patient = new FacePatientMatch("11", "受付 太郎", "ウケツケ タロウ", new(1980, 1, 1), ["110"]);
            Capture = new() { FileName = "synthetic.xml", ContentHash = "test", GeneratedAt = Now.LocalDateTime, CapturedAt = Now,
                Face = new(patient.NameKana, patient.Birthdate, "0110012345", PatientName: patient.Name),
                Lookup = new(hasChart ? [patient] : [], hasChart ? patient : null, false, "mock"),
                ChartNumberFoundAtReception = hasChart, ReservationFoundAtReception = actualReservation,
                ReceptionNo = actualReservation ? "001" : "", ReservationPatientName = actualReservation ? patient.Name : "",
                Stage = CaptureStage.Completed, RequestDirectory = Settings.ICallRequestDirectory, ResponseDirectory = Settings.ICallResponseDirectory };
            Store.Capture(Capture, []);
            Session = Sessions.AnswerPage(Session.Id, Session.Device, new("fever", fever ? "yes" : "no"));
        }
        public KioskSessions Reopen() => new(Path.Combine(Root, "sessions"), Store, () => Options, () => Available, () => Now, () => Settings);
        public KioskAutomationService Worker(Action<KioskReceipt, string>? printer = null) => new(Sessions, Store,
            (_, _) => throw new Exception("Unexpected lookup"), printer ?? ((_, _) => Events.Add("print")), () => Available, () => Now);
        public KioskSession Current => Sessions.Get(Session.Id, Session.Device);
        public async Task Drain(KioskAutomationService? service = null, bool linkSuccess = true, int passes = 8)
        {
            service ??= Worker(); using var cancellation = new CancellationTokenSource();
            var responder = Task.Run(async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    foreach (var path in Directory.GetFiles(Settings.ICallRequestDirectory, "*.json"))
                    {
                        var request = JsonSerializer.Deserialize<ICallRequest>(File.ReadAllText(path), ICallFileClient.Json)!;
                        string responsePath = Path.Combine(Settings.ICallResponseDirectory, request.RequestId + ".json");
                        if (File.Exists(responsePath)) continue;
                        bool success = request.Action != "link" || linkSuccess;
                        var response = new ICallResponse(request.RequestId, success, success ? request.Action == "link" ? "linked" : "invoked" : "operation_failed",
                            "synthetic", request.PatientId, request.ExpectedReceptionNo, request.ExpectedPatientName, Now, request.Fingerprint());
                        File.WriteAllText(responsePath + ".tmp", JsonSerializer.Serialize(response, ICallFileClient.Json));
                        File.Move(responsePath + ".tmp", responsePath); Events.Add(request.Action);
                    }
                    try { await Task.Delay(10, cancellation.Token); } catch (OperationCanceledException) { }
                }
            });
            try { for (int i = 0; i < passes; i++) await service.RunOnceAsync(default); }
            finally { cancellation.Cancel(); await responder; }
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
    private static KioskAutomationOptions All() => new() { AutoLink = true, AutoArrival = true, AutoPrint = true, PrinterName = "Synthetic 58mm" };
    public static async Task Run(Action<bool, string> check)
    {
        using (var f = new Fixture(new()))
        {
            await f.Drain(); check(f.Current.Automation is null && f.Events.Count == 0, "Kiosk automation defaults OFF");
        }
        using (var f = new Fixture(All()))
        {
            check(f.Current.Automation is { Finished: false } && f.Sessions.HasPendingAutomation(f.Capture.Id), "eligible Kiosk commits automatic job");
            try { f.Sessions.End(f.Session.Id, f.Session.Device, false); check(false, "early close"); }
            catch (InvalidOperationException) { check(true, "automatic job cannot close while processing"); }
            try { f.Sessions.Receipt(f.Session.Id, f.Session.Device); check(false, "manual print during auto"); }
            catch (InvalidOperationException) { check(true, "pending automation blocks duplicate manual receipt printing"); }
            await f.Drain();
            check(f.Events.SequenceEqual(new[] { "link", "arrived", "print" }) && f.Current.Automation is { Finished: true, NeedsReview: false },
                "Kiosk automatically links then arrives then prints once");
            var saved = f.Store.Get(f.Capture.Id);
            check(saved.KioskAutomaticSessionId == f.Session.Id && !saved.ManualOperationRequested, "Kiosk operation has explicit durable provenance");
            f.Sessions = f.Reopen(); await f.Drain();
            check(f.Events.Count == 3, "completed Kiosk automation not repeated after restart");
        }
        using (var f = new Fixture(All(), ReceptionLinkMode.DirectOutput))
        {
            await f.Drain();
            check(File.Exists(Path.Combine(f.Settings.DynamicsReceiptDirectory, "receipt.txt")) && f.Events.SequenceEqual(new[] { "arrived", "print" }),
                "automatic direct output skips iCall link but confirms arrival and prints");
        }
        using (var f = new Fixture(All(), ReceptionLinkMode.None))
        {
            await f.Drain(); check(f.Current.Automation!.Link == KioskAutoActionState.Skipped && f.Events.SequenceEqual(new[] { "arrived", "print" }),
                "no-link setting suppresses automatic link independently");
        }
        using (var f = new Fixture(new() { AutoPrint = true, PrinterName = "Synthetic 58mm" }))
        {
            await f.Drain(); check(f.Events.SequenceEqual(new[] { "print" }), "automatic printing can be enabled independently");
        }
        foreach (var excluded in new (bool Fever, bool Reserved, bool Chart, bool Actual)[] { (true, true, true, true), (false, false, true, true), (false, true, false, true), (false, false, true, false) })
        {
            using var f = new Fixture(All(), fever: excluded.Fever, saysReserved: excluded.Reserved, hasChart: excluded.Chart, actualReservation: excluded.Actual);
            await f.Drain(); check(f.Current.Automation is null && f.Events.Count == 0, "fever, reservation mismatch, new patient or no reservation cannot automate");
        }
        using (var f = new Fixture(new() { AutoArrival = true, RequiredAnswers = new() { ["clinicClass"] = "general" } }, clinic: "vaccination"))
        {
            await f.Drain(); check(f.Current.Automation is null && f.Events.Count == 0, "additional answer conditions restrict automatic eligibility");
        }
        using (var f = new Fixture(new() { AutoPrint = true, PrinterName = "Synthetic 58mm", RequiredAnswers = new() { ["clinicClass"] = "general" } }, clinic: "general"))
        {
            await f.Drain(); check(f.Events.SequenceEqual(new[] { "print" }), "matching extra answer condition allows automatic processing");
        }
        using (var f = new Fixture(All()))
        {
            await f.Drain(linkSuccess: false);
            check(f.Current.State == KioskSessionState.StaffHelp && f.Current.Automation!.NeedsReview && f.Events.SequenceEqual(new[] { "link" }),
                "failed link stops arrival and printing and routes to staff");
        }
        using (var f = new Fixture(new() { AutoPrint = true, PrinterName = "Synthetic 58mm" }))
        {
            var interrupted = f.Worker((_, _) => { f.Events.Add("print-uncertain"); throw new OperationCanceledException(); });
            try { await interrupted.RunOnceAsync(default); } catch (OperationCanceledException) { }
            f.Sessions = f.Reopen(); await f.Drain();
            check(f.Current.Automation!.NeedsReview && f.Events.SequenceEqual(new[] { "print-uncertain" }), "interrupted print never resubmits automatically after restart");
            f.Sessions.ConfirmAutomationReview(f.Session.Id, f.Session.Device);
            check(f.Sessions.Receipt(f.Session.Id, f.Session.Device).PatientId == "11" && f.Current.Automation!.Print == KioskAutoActionState.Skipped,
                "staff review permits explicit manual reprint without reporting uncertain print as successful");
            await f.Drain(); check(f.Events.Count == 1, "staff resolution never automatically repeats skipped actions");
        }
        using (var f = new Fixture(All()))
        {
            f.Available = false; await f.Drain(); check(f.Events.Count == 0, "monitor stop suspends automatic actions");
            f.Available = true;
            var record = f.Store.Get(f.Capture.Id); record.ReconciliationIdentityMismatch = true; f.Store.Save(record);
            await f.Drain(); check(f.Events.Count == 0 && f.Current.Automation!.NeedsReview, "changed identity blocks queued automatic actions");
        }
        using (var f = new Fixture(All()))
        {
            f.Now = f.Now.AddDays(1); await f.Drain(); check(f.Events.Count == 0 && f.Current.Automation!.NeedsReview, "prior day automatic job never uses today's reservation list");
        }
        using (var f = new Fixture(new() { AutoLink = true }))
        {
            f.Settings.ReceptionLinkMode = ReceptionLinkMode.None;
            await f.Drain(); check(f.Events.SequenceEqual(new[] { "link" }), "queued automatic job preserves selected link mode snapshot");
        }
        using (var f = new Fixture(new() { AutoLink = true }))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var worker = f.Worker();
            var pending = worker.RunOnceAsync(cancellation.Token);
            while (!Directory.GetFiles(f.Settings.ICallRequestDirectory, "*.json").Any())
                await Task.Delay(10, cancellation.Token);
            cancellation.Cancel(); try { await pending; } catch (OperationCanceledException) { }
            f.Sessions = f.Reopen(); await f.Drain();
            check(f.Events.SequenceEqual(new[] { "link" }) && Directory.GetFiles(f.Settings.ICallRequestDirectory, "*.json").Length == 1 &&
                f.Current.Automation is { Finished: true, NeedsReview: false }, "interrupted iCall automation reuses durable request ID after restart");
        }
        using (var f = new Fixture(new() { AutoPrint = true, PrinterName = "Synthetic 58mm" }))
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            await using var server = new ReceptionAgent.Web.KioskWebServer(f.Sessions, () => f.Available);
            await server.StartAsync(port);
            using var client = new HttpClient { BaseAddress = new Uri(server.Url) };
            client.DefaultRequestHeaders.Add("Cookie", "kiosk-device=" + f.Session.Device);
            using var before = JsonDocument.Parse(await client.GetStringAsync("api/sessions/" + f.Session.Id));
            check(before.RootElement.GetProperty("waiting").GetBoolean() && !before.RootElement.GetProperty("canCancel").GetBoolean(),
                "Kiosk HTTP keeps polling while automation runs and disables cancellation");
            using var health = JsonDocument.Parse(await client.GetStringAsync("api/health"));
            check(!health.RootElement.GetProperty("testMode").GetBoolean(), "health reports automatic reception enabled");
            await f.Drain();
            using var after = JsonDocument.Parse(await client.GetStringAsync("api/sessions/" + f.Session.Id));
            check(!after.RootElement.GetProperty("waiting").GetBoolean() && after.RootElement.GetProperty("title").GetString() == "受付が完了しました",
                "Kiosk HTTP releases waiting screen only after automatic job finishes");
        }
        var options = new KioskOptions { Automation = All() };
        string settingsRoot = Path.Combine(Path.GetTempPath(), "auto-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            options.Save(settingsRoot); check(KioskOptions.Load(settingsRoot).Automation.AutoPrint && KioskOptions.Load(settingsRoot).Automation.PrinterName == "Synthetic 58mm", "automatic settings persist printer and switches");
            options.Automation.RequiredAnswers["unknown"] = "unknown";
            try { options.Save(settingsRoot); check(false, "invalid condition"); } catch (ArgumentException) { check(true, "unknown automatic condition rejected"); }
        }
        finally { Directory.Delete(settingsRoot, true); }
    }
}
