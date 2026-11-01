using System.Text.Json;
using ReceptionAgent;
using ReceptionAgent.Face;
using ReceptionAgent.ICall;
using ReceptionAgent.Reception;

internal static class WalkInAssignmentTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "walk-in-tests-" + Guid.NewGuid().ToString("N"));
        var store = new CaptureStore(Path.Combine(root, "db"));
        var config = new AgentSettings { OperationMode = ReceptionOperationMode.WalkIn,
            ICallRequestDirectory = Path.Combine(root, "req"), ICallResponseDirectory = Path.Combine(root, "res") };
        Directory.CreateDirectory(config.ICallRequestDirectory); Directory.CreateDirectory(config.ICallResponseDirectory);
        ReceptionWorkflow Worker() => new(store, (_, _) => throw new Exception("unexpected lookup"), currentSettings: () => config);
        CaptureRecord New(ReceptionOperationMode mode = ReceptionOperationMode.WalkIn)
        {
            var patient = new FacePatientMatch("00123", "試験太郎", "テストタロウ", new(2000, 1, 1), ["1230"]);
            var r = new CaptureRecord { FileName = Guid.NewGuid() + ".xml", ContentHash = Guid.NewGuid().ToString(),
                GeneratedAt = DateTime.Now, OperationMode = mode, Stage = CaptureStage.Completed,
                Lookup = new([patient], patient, false, "synthetic"), ChartNumberFoundAtReception = true,
                ReservationFoundAtReception = false, ReceptionClassifiedAt = DateTimeOffset.Now,
                RequestDirectory = config.ICallRequestDirectory, ResponseDirectory = config.ICallResponseDirectory };
            store.Capture(r, [1]); return r;
        }
        async Task Respond(CaptureRecord r, bool success, string code, string? patientId = "00123", string? receptionNo = "7", string? name = "試験太郎")
        {
            var req = store.Get(r.Id).PendingRequest!;
            // Use the actual manager contract to verify the assign wire fingerprint, too.
            var wire = JsonSerializer.Deserialize<iCallManager.Core.BridgeRequest>(JsonSerializer.Serialize(req, ICallFileClient.Json), ICallFileClient.Json)!;
            check(wire.Fingerprint() == req.Fingerprint(), "assignment fingerprint matches manager contract");
            var response = new iCallManager.Core.BridgeResponse(req.RequestId, success, code, "synthetic", patientId, receptionNo, name, DateTimeOffset.Now, req.Fingerprint());
            await File.WriteAllTextAsync(Path.Combine(config.ICallResponseDirectory, req.RequestId + ".json"), JsonSerializer.Serialize(response, ICallFileClient.Json));
            await Worker().StepByIdAsync(r.Id, default);
        }
        async Task Reject(CaptureRecord r, string reason)
        {
            store.Save(r);
            try { await Worker().QueueManualAsync(r.Id, "assign", default); check(false, "reject " + reason); }
            catch (InvalidDataException) { check(!store.Get(r.Id).AssignmentRequested, "assignment rejects " + reason); }
        }
        try
        {
            var manual = New(ReceptionOperationMode.Reservation);
            await Worker().QueueManualAsync(manual.Id, "assign", default);
            var queued = store.Get(manual.Id);
            check(queued.Stage == CaptureStage.AssignWaiting && queued.AssignmentRequested && queued.PendingRequest is { Action: "assign", PatientId: "00123", ReceptionNo: null, ExpectedReceptionNo: null },
                "manual assignment persists a stable request before external publication, without a slot constraint");
            // A short transport wait models a slow manager; recovery must retain the request ID.
            try { await new ICallFileClient().SendAsync(queued.PendingRequest!, config.ICallRequestDirectory, config.ICallResponseDirectory, TimeSpan.FromMilliseconds(10), default); }
            catch (TimeoutException) { }
            var requestPath = Path.Combine(config.ICallRequestDirectory, queued.PendingRequest!.RequestId + ".json");
            string original = await File.ReadAllTextAsync(requestPath);
            await Respond(manual, true, "assigned");
            await Worker().StepByIdAsync(manual.Id, default);
            var done = store.Get(manual.Id);
            check(done.Stage == CaptureStage.Completed && done.AssignedAt is not null && done.ReceptionNo == "7" && done.ReservationPatientName == "試験太郎" && done.PendingRequest is null,
                "assignment response recovers across workflow restart and stores the assigned reception");
            check(done.ReceptionCategory == ReceptionCategory.ReturningWithoutReservation && done.ArrivalResult == "未要求" && done.LinkResult == "未要求",
                "assignment preserves the original walk-in classification without arrival or link side effects");
            await Worker().StepByIdAsync(manual.Id, default);
            check(await File.ReadAllTextAsync(requestPath) == original && store.Get(manual.Id).Responses.Count == 1,
                "completed assignment never republishes or consumes its result twice");
            try { await Worker().QueueManualAsync(manual.Id, "assign", default); check(false, "repeat assignment"); }
            catch (InvalidDataException) { check(true, "completed assignment cannot be queued again"); }

            foreach (string code in new[] { "no_dummy_slot", "already_reserved", "outcome_unknown", "operations_disabled" })
            {
                var failed = New(); await Worker().QueueManualAsync(failed.Id, "assign", default);
                await Respond(failed, false, code);
                var result = store.Get(failed.Id);
                check(result.Stage == CaptureStage.NeedsReview && result.AssignmentResult.StartsWith(code) && !WalkInAssignment.CanQueue(result),
                    "failed assignment is preserved for review without requeue: " + code);
            }
            foreach (string reason in new[] { "new_patient", "multiple", "invalid_card", "old_date", "identity_changed", "conflict", "reserved", "unfinished" })
            {
                var r = New();
                switch (reason)
                {
                    case "new_patient": r.Lookup = new([], null, false, "no patient"); r.ChartNumberFoundAtReception = false; break;
                    case "multiple": r.Lookup = r.Lookup! with { Candidates = [r.Lookup!.Selected!, r.Lookup.Selected! with { PatientId = "99" }] }; break;
                    case "invalid_card": r.Lookup = r.Lookup! with { Selected = r.Lookup!.Selected! with { PatientId = "-" } }; break;
                    case "old_date": r.GeneratedAt = DateTime.Today.AddDays(-1); break;
                    case "identity_changed": r.VerifiedPatientId = "999"; break;
                    case "conflict": r.ReconciliationIdentityMismatch = true; break;
                    case "reserved": r.ReservationFoundAtReception = true; r.ReceptionNo = "8"; break;
                    case "unfinished": r.Stage = CaptureStage.FindWaiting; break;
                }
                await Reject(r, reason);
            }
            var mismatch = New(); await Worker().QueueManualAsync(mismatch.Id, "assign", default);
            await Respond(mismatch, true, "assigned", patientId: "999");
            check(store.Get(mismatch.Id).Stage == CaptureStage.NeedsReview && store.Get(mismatch.Id).AssignedAt is null,
                "assignment rejects response for another patient even with matching fingerprint");
            var incomplete = New(); await Worker().QueueManualAsync(incomplete.Id, "assign", default);
            await Respond(incomplete, true, "assigned", receptionNo: null);
            check(store.Get(incomplete.Id).Stage == CaptureStage.NeedsReview, "assignment rejects incomplete success response");
            var changed = New(); await Worker().QueueManualAsync(changed.Id, "assign", default);
            changed = store.Get(changed.Id); changed.VerifiedPatientId = "999"; store.Save(changed);
            await Worker().StepByIdAsync(changed.Id, default);
            check(store.Get(changed.Id).Stage == CaptureStage.NeedsReview && !File.Exists(Path.Combine(config.ICallRequestDirectory, "ra-" + changed.Id + "-assign.json")),
                "identity change after queue prevents assignment publication");

            foreach (var mode in new[] { ReceptionOperationMode.WalkIn, ReceptionOperationMode.MonitorOnly, ReceptionOperationMode.Reservation, ReceptionOperationMode.Kiosk })
            {
                var r = New(mode); r.Stage = CaptureStage.PatientIdentified; store.Save(r);
                await Worker().StepByIdAsync(r.Id, default);
                await Respond(r, false, "not_found", receptionNo: null, name: null);
                var result = store.Get(r.Id);
                check((result.Stage == CaptureStage.AssignWaiting && result.PendingRequest?.Action == "assign") == ReceptionModes.IncludesAssignment(mode),
                    "assignment levels include walk-in and Kiosk after not_found: " + mode);
            }
            foreach (var inheritedMode in new[] { ReceptionOperationMode.WalkIn, ReceptionOperationMode.Kiosk })
            {
                config.OperationMode = inheritedMode; config.AutoArrival = config.AutoLink = true;
                var inherited = New(inheritedMode);
                await Worker().QueueManualAsync(inherited.Id, "assign", default);
                await Respond(inherited, true, "assigned");
                await Worker().StepByIdAsync(inherited.Id, default);
                check(store.Get(inherited.Id).PendingRequest?.Action == "arrived", "assignment level inherits automatic arrival: " + inheritedMode);
                await Respond(inherited, true, "invoked");
                check(store.Get(inherited.Id).PendingRequest?.Action == "link", "assignment level inherits automatic link after arrival: " + inheritedMode);
                await Respond(inherited, true, "linked");
                check(store.Get(inherited.Id).Stage == CaptureStage.Completed && store.Get(inherited.Id).Responses.Select(r => r.Code).SequenceEqual(new[] { "assigned", "invoked", "linked" }),
                    "assignment, arrival, and link complete in order without changing original no-reservation classification: " + inheritedMode);
            }
            var stopped = New(); stopped.Stage = CaptureStage.PatientIdentified; store.Save(stopped);
            await Worker().StepByIdAsync(stopped.Id, default);
            config.OperationMode = ReceptionOperationMode.MonitorOnly;
            await Respond(stopped, false, "not_found", receptionNo: null, name: null);
            check(store.Get(stopped.Id).Stage == CaptureStage.Completed && !store.Get(stopped.Id).AssignmentRequested,
                "turning walk-in mode OFF before classification prevents automatic assignment");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
