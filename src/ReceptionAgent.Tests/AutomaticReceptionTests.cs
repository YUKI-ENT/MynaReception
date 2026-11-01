using System.Text.Json;
using ReceptionAgent;
using ReceptionAgent.Face;
using ReceptionAgent.ICall;
using ReceptionAgent.Reception;

internal static class AutomaticReceptionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "auto-reception-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new CaptureStore(Path.Combine(root, "db"));
        var config = new AgentSettings { ICallRequestDirectory = Path.Combine(root, "req"), ICallResponseDirectory = Path.Combine(root, "res"),
            DynamicsReceiptDirectory = Path.Combine(root, "receipt") };
        Directory.CreateDirectory(config.ICallRequestDirectory); Directory.CreateDirectory(config.ICallResponseDirectory);
        Task<FaceLookupResult> Lookup(FaceIdentity face, CancellationToken token) => throw new Exception("unexpected lookup");
        ReceptionWorkflow Worker() => new(store, Lookup, currentSettings: () => config);
        CaptureRecord New(ReceptionOperationMode mode = ReceptionOperationMode.Kiosk)
        {
            var patient = new FacePatientMatch("00123", "試験太郎", "テストタロウ", new(2000, 1, 1), ["1230"]);
            var r = new CaptureRecord { FileName = Guid.NewGuid() + ".xml", ContentHash = Guid.NewGuid().ToString(),
                GeneratedAt = DateTime.Now, OperationMode = mode, Stage = CaptureStage.ReservationFound,
                Lookup = new([patient], patient, false, "synthetic"), ReceptionNo = "7", ReservationPatientName = "試験太郎",
                ChartNumberFoundAtReception = true, ReservationFoundAtReception = true,
                RequestDirectory = config.ICallRequestDirectory, ResponseDirectory = config.ICallResponseDirectory };
            store.Capture(r, [1]); return r;
        }
        async Task Reply(CaptureRecord r, bool success = true, string? code = null)
        {
            var req = r.PendingRequest!;
            var response = new iCallManager.Core.BridgeResponse(req.RequestId, success, code ?? (req.Action == "arrived" ? "invoked" : "linked"),
                "synthetic", req.PatientId, "7", "試験太郎", DateTimeOffset.Now, req.Fingerprint());
            await File.WriteAllTextAsync(Path.Combine(config.ICallResponseDirectory, req.RequestId + ".json"), JsonSerializer.Serialize(response, ICallFileClient.Json));
            await Worker().StepByIdAsync(r.Id, default);
        }
        try
        {
            check(!config.AutoArrival && !config.AutoLink, "global reception automation defaults OFF independently of legacy flags");
            foreach (var mode in Enum.GetValues<ReceptionOperationMode>())
            foreach (bool arrival in new[] { false, true })
            foreach (bool link in new[] { false, true })
            {
                config.AutoArrival = arrival; config.AutoLink = link;
                var r = New(mode); await Worker().StepByIdAsync(r.Id, default);
                var actions = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    r = store.Get(r.Id);
                    if (r.PendingRequest is not { } req) break;
                    check(req.PatientId == "00123" && req.ReceptionNo == "7" && req.ExpectedReceptionNo is null && req.ExpectedPatientName is null,
                        "global operation targets reception with verified chart as extra check and no name constraint");
                    actions.Add(req.Action); await Reply(r);
                }
                r = store.Get(r.Id);
                check(r.Stage == CaptureStage.Completed && actions.SequenceEqual((arrival && ReceptionModes.IncludesArrival(mode) ? new[] { "arrived" } : []).Concat(link && ReceptionModes.IncludesArrival(mode) ? new[] { "link" } : [])),
                    $"global switches execute arrival then link in {mode} ({arrival}/{link}), surviving workflow restart");
                await Worker().StepByIdAsync(r.Id, default);
                check(store.Get(r.Id).Responses.Count == actions.Count, "completed global operations are not repeated");
            }
            config.AutoArrival = config.AutoLink = true;
            var historical = New(); historical.Stage = CaptureStage.Completed; store.Save(historical);
            await Worker().StepByIdAsync(historical.Id, default);
            check(store.Get(historical.Id).PendingRequest is null && !store.Get(historical.Id).AutomaticOperationRequested, "enabling switches does not operate completed historical receptions");
            var committed = New(); await Worker().StepByIdAsync(committed.Id, default);
            string arrivalId = store.Get(committed.Id).PendingRequest!.RequestId;
            config.AutoArrival = config.AutoLink = false;
            await Reply(store.Get(committed.Id));
            check(store.Get(committed.Id).PendingRequest?.Action == "link" && arrivalId == "ra-" + committed.Id + "-arrived", "committed operation plan survives later switch changes with stable request IDs");
            await Reply(store.Get(committed.Id));
            var disabledNow = New(); await Worker().StepByIdAsync(disabledNow.Id, default);
            check(store.Get(disabledNow.Id).PendingRequest is null, "live setting changes apply at the next reservation confirmation without monitor restart");
            config.AutoArrival = config.AutoLink = true;
            var failed = New(); await Worker().StepByIdAsync(failed.Id, default);
            await Reply(store.Get(failed.Id), false, "reservation_changed");
            check(store.Get(failed.Id).Stage == CaptureStage.NeedsReview && store.Get(failed.Id).Responses.Count == 1 &&
                !File.Exists(Path.Combine(config.ICallRequestDirectory, "ra-" + failed.Id + "-link.json")), "failed arrival halts automatic link for staff review");
            config.ReceptionLinkMode = ReceptionLinkMode.DirectOutput;
            var direct = New(); await Worker().StepByIdAsync(direct.Id, default); await Reply(store.Get(direct.Id));
            check(store.Get(direct.Id).Stage == CaptureStage.Completed && File.ReadAllText(Path.Combine(config.DynamicsReceiptDirectory, "receipt.txt"), System.Text.Encoding.GetEncoding(932)).Contains("00123"),
                "global direct link emits Dynamics receipt after arrival without iCall link");
            // Recover an interrupted direct outbox publication using the same capture ID.
            var resumed = store.Get(direct.Id); resumed.Stage = CaptureStage.LinkReady; resumed.DynamicsReceiptDirectory = ""; store.Save(resumed);
            await Worker().StepByIdAsync(resumed.Id, default);
            check(store.Get(resumed.Id).Stage == CaptureStage.Completed && !File.Exists(Path.Combine(config.ICallRequestDirectory, "ra-" + direct.Id + "-link.json")), "direct output recovery reuses durable outbox identity without iCall request");
            config.ReceptionLinkMode = ReceptionLinkMode.None; config.AutoArrival = false;
            var disabled = New(); await Worker().StepByIdAsync(disabled.Id, default);
            check(store.Get(disabled.Id).Stage == CaptureStage.Completed && store.Get(disabled.Id).PendingRequest is null, "disabled link mode does not queue automatic link");
            config.ReceptionLinkMode = ReceptionLinkMode.ICall; config.AutoArrival = true;
            foreach (string reason in new[] { "mismatch", "no_booking", "old_date" })
            {
                var r = New();
                if (reason == "mismatch") r.VerifiedPatientId = "other";
                if (reason == "no_booking") { r.ReservationFoundAtReception = false; r.ReceptionNo = ""; }
                if (reason == "old_date") r.GeneratedAt = DateTime.Today.AddDays(-1);
                store.Save(r); await Worker().StepByIdAsync(r.Id, default);
                check(store.Get(r.Id).Stage == CaptureStage.NeedsReview && store.Get(r.Id).PendingRequest is null, "global automation blocks " + reason);
            }
            config.Save(root);
            check(AgentSettings.Load(root).AutoArrival && AgentSettings.Load(root).AutoLink, "main automation switches persist in settings");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
