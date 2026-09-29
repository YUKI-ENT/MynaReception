using System.Text;
using System.Text.Json;
using ReceptionAgent;
using ReceptionAgent.Face;
using ReceptionAgent.ICall;
using ReceptionAgent.Reception;

internal static class CaptureTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-capture-" + Guid.NewGuid().ToString("N"));
        var settings = new AgentSettings { FaceXmlDirectory = Path.Combine(root, "face"), FaceTrashDirectory = Path.Combine(root, "trash"), ICallRequestDirectory = Path.Combine(root, "req"), ICallResponseDirectory = Path.Combine(root, "res") };
        foreach (var dir in new[] { settings.FaceXmlDirectory, settings.FaceTrashDirectory, settings.ICallRequestDirectory, settings.ICallResponseDirectory }) Directory.CreateDirectory(dir);
        settings.ValidateMonitoring(); settings.Save(root);
        check(AgentSettings.Load(root).FaceTrashDirectory == settings.FaceTrashDirectory && !AgentSettings.Load(root).MarkArrived, "capture paths and operations OFF default persist");
        var store = new CaptureStore(Path.Combine(root, "db"));
        var selected = new FacePatientMatch("11", "試験太郎", "ﾃｽﾄ ﾀﾛｳ", new DateOnly(2024, 10, 21), ["110"]);
        Task<FaceLookupResult> Lookup(FaceIdentity face, CancellationToken token) => Task.FromResult(new FaceLookupResult([selected], selected, false, "一致"));
        var monitor = new FaceCaptureMonitor(store, settings, Lookup);
        string sample = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><XmlMsg><MessageHeader><SegmentOfResult>1</SegmentOfResult><MedicalInstitutionCode>0110012345</MedicalInstitutionCode></MessageHeader><MessageBody><ResultList><ResultOfQualificationConfirmation><Name>試験太郎</Name><NameKana>ﾃｽﾄ ﾀﾛｳ</NameKana><Birthdate>20241021</Birthdate><InsurerNumber>00123456</InsurerNumber><InsuredCardSymbol>TEST</InsuredCardSymbol><InsuredIdentificationNumber>001</InsuredIdentificationNumber><InsuredBranchNumber>01</InsuredBranchNumber></ResultOfQualificationConfirmation></ResultList><ProcessingResultStatus>1</ProcessingResultStatus></MessageBody></XmlMsg>";
        string Name(string prefix) => $"OQSsiquc01res_face_{prefix}_{DateTime.Now:yyyyMMddHHmmss}.xml";
        string fileName = Name("first"), source = Path.Combine(settings.FaceXmlDirectory, fileName), trash = Path.Combine(settings.FaceTrashDirectory, fileName);
        await File.WriteAllTextAsync(trash, sample);
        check(await monitor.CaptureAsync(source, DateTime.Now, default), "missing source recovered from trash");
        check(!await monitor.CaptureAsync(trash, DateTime.Now, default), "same file from trash is not captured twice");
        var record = store.List().Single();
        check(record.Face?.Insurance?.InsurerNumber == "00123456" && record.XmlPatientName == "試験太郎" && record.FileCreatedAt is not null, "SQLite retains patient insurance creation date and XML");
        var restoredStore = new CaptureStore(Path.Combine(root, "db"));
        check(restoredStore.ReadXml(record.Id).SequenceEqual(Encoding.UTF8.GetBytes(sample)), "original XML survives reopening SQLite");
        using (store.AcquireMonitorLease())
        {
            try { using var other = restoredStore.AcquireMonitorLease(); check(false, "second monitor prevented"); }
            catch (IOException) { check(true, "second monitor prevented"); }
        }
        var workflow = new ReceptionWorkflow(store, Lookup);
        await workflow.StepAsync(record, default); await workflow.StepAsync(record, default);
        check(record.Stage == CaptureStage.FindWaiting && !Directory.GetFiles(settings.ICallRequestDirectory).Any(), "request committed before external publication");
        record = restoredStore.List().Single();
        var request = record.PendingRequest!;
        var managerRequest = new iCallManager.Core.BridgeRequest(request.RequestId, request.Action, request.PatientId, request.ExpectedReceptionNo, request.ExpectedPatientName);
        check(request.Fingerprint() == managerRequest.Fingerprint(), "fingerprint identical to actual iCallManager contract");
        await workflow.StepAsync(record, default);
        check(record.Stage == CaptureStage.FindWaiting && record.PendingRequest!.RequestId == request.RequestId && File.Exists(Path.Combine(settings.ICallRequestDirectory, request.RequestId + ".json")), "timeout retains request ID for restart");
        async Task Reply(CaptureRecord r, bool success = true, string? code = null)
        {
            var req = r.PendingRequest!;
            var res = new iCallManager.Core.BridgeResponse(req.RequestId, success, code ?? (req.Action == "find" ? "found" : "invoked"), "synthetic response", req.PatientId, "7", "試験太郎", DateTimeOffset.Now, req.Fingerprint());
            await File.WriteAllTextAsync(Path.Combine(settings.ICallResponseDirectory, req.RequestId + ".json"), JsonSerializer.Serialize(res, iCallManager.Core.AppSettings.Json));
        }
        await Reply(record); record = store.List().Single();
        await new ReceptionWorkflow(restoredStore, Lookup).StepAsync(record, default);
        await workflow.StepAsync(record, default); await workflow.StepAsync(record, default);
        check(record.Stage == CaptureStage.Completed && record.ReceptionNo == "7" && record.Lookup?.Selected?.PatientId == "11", "reservation acquired after restart");
        check(Directory.GetFiles(settings.ICallRequestDirectory).Length == 1, "default configuration sends find only");
        // Move a file while a compatible writer still has it open. Capture must not block the rename.
        string moving = Path.Combine(settings.FaceXmlDirectory, Name("moving"));
        using (var writer = new FileStream(moving, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var bytes = Encoding.UTF8.GetBytes(sample);
            await writer.WriteAsync(bytes.AsMemory(0, 100)); await writer.FlushAsync();
            var capture = monitor.CaptureAsync(moving, DateTime.Now, default);
            await Task.Delay(350); await writer.WriteAsync(bytes.AsMemory(100)); await writer.FlushAsync();
            File.Move(moving, Path.Combine(settings.FaceTrashDirectory, Path.GetFileName(moving)));
            check(await capture, "partial write and concurrent move to trash recovered without exclusive lock");
        }
        var second = store.List().Single(r => r.Id != record.Id);
        second.MarkArrived = second.LinkReservation = true; store.Save(second);
        for (int i = 0; i < 9 && second.Stage != CaptureStage.Completed; i++)
        {
            if (second.PendingRequest is not null) await Reply(second);
            await workflow.StepAsync(second, default);
        }
        check(second.Stage == CaptureStage.Completed && second.Responses.Select(r => r.Code).SequenceEqual(new[] { "found", "invoked", "invoked" }), "configured arrival then link responses persist");
        check(second.ArrivalResult.StartsWith("invoked") && second.LinkResult.StartsWith("invoked"), "operation dispatch distinguished in results");
        var next = new CaptureRecord { FileName = Name("notfound"), ContentHash = "synthetic", GeneratedAt = DateTime.Now, Encoding = FaceXmlEncoding.Utf8,
            RequestDirectory = settings.ICallRequestDirectory, ResponseDirectory = settings.ICallResponseDirectory, MarkArrived = true, LinkReservation = true };
        store.Capture(next, Encoding.UTF8.GetBytes(sample));
        await workflow.StepAsync(next, default); await workflow.StepAsync(next, default); await Reply(next, false, "not_found"); await workflow.StepAsync(next, default);
        check(next.Stage == CaptureStage.NeedsReview && next.ArrivalResult == "未要求" && next.Responses.Count == 1, "no reservation is retained for review without operations");
        next.Stage = CaptureStage.Captured; next.PendingRequest = null;
        await new ReceptionWorkflow(store, (f, t) => Task.FromResult(new FaceLookupResult([selected, selected with { PatientId = "22" }], null, true, "特定できず"))).StepAsync(next, default);
        check(next.Stage == CaptureStage.NeedsReview && next.Lookup?.Candidates.Count == 2 && next.PendingRequest is null, "ambiguous patient stored and never sent to iCall");
        next.Stage = CaptureStage.Captured; next.GeneratedAt = DateTime.Today.AddDays(-1);
        await workflow.StepAsync(next, default);
        check(next.Stage == CaptureStage.NeedsReview && next.Status.Contains("当日"), "old pending record cannot operate today's reservations");
        await File.WriteAllTextAsync(trash, sample.Replace("TEST", "OTHER"));
        check(!await monitor.CaptureAsync(trash, DateTime.Now, default) && store.List().Single(r => r.Id == record.Id).Conflict, "same filename with changed content stops instead of overwriting");
        var badReq = new ICallRequest("ra-" + Guid.NewGuid().ToString("N") + "-find", "find", "11");
        var badRes = new ICallResponse(badReq.RequestId, true, "found", "test", "22", "1", "OTHER", DateTimeOffset.Now, badReq.Fingerprint());
        await File.WriteAllTextAsync(Path.Combine(settings.ICallResponseDirectory, badReq.RequestId + ".json"), JsonSerializer.Serialize(badRes, ICallFileClient.Json));
        try { await new ICallFileClient().SendAsync(badReq, settings.ICallRequestDirectory, settings.ICallResponseDirectory, TimeSpan.FromSeconds(1), default); check(false, "wrong patient response"); }
        catch (InvalidDataException) { check(true, "response for different patient rejected"); }
        check(FaceCaptureMonitor.TryTimestamp(fileName, out var timestamp) && timestamp.Date == DateTime.Today && !FaceCaptureMonitor.TryTimestamp("unrelated.xml", out _), "filename date parsed strictly");
        // Exercise both background loops with a synthetic file-based iCallManager responder.
        string liveRoot = Path.Combine(root, "live");
        var live = new AgentSettings { FaceXmlDirectory = Path.Combine(liveRoot, "face"), FaceTrashDirectory = Path.Combine(liveRoot, "trash"), ICallRequestDirectory = Path.Combine(liveRoot, "req"), ICallResponseDirectory = Path.Combine(liveRoot, "res") };
        foreach (var dir in new[] { live.FaceXmlDirectory, live.FaceTrashDirectory, live.ICallRequestDirectory, live.ICallResponseDirectory }) Directory.CreateDirectory(dir);
        var liveStore = new CaptureStore(Path.Combine(liveRoot, "db"));
        await File.WriteAllTextAsync(Path.Combine(live.FaceTrashDirectory, $"OQSsiquc01res_face_old_{DateTime.Today.AddDays(-1):yyyyMMdd}120000.xml"), sample);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = new FaceCaptureMonitor(liveStore, live, Lookup).RunAsync(stop.Token);
        await File.WriteAllTextAsync(Path.Combine(live.FaceTrashDirectory, Name("live")), sample);
        try
        {
            while (!liveStore.List().Any(r => r.Stage == CaptureStage.Completed))
            {
                stop.Token.ThrowIfCancellationRequested();
                foreach (string path in Directory.GetFiles(live.ICallRequestDirectory, "*.json"))
                {
                    var req = JsonSerializer.Deserialize<iCallManager.Core.BridgeRequest>(await File.ReadAllTextAsync(path), iCallManager.Core.AppSettings.Json)!;
                    string responsePath = Path.Combine(live.ICallResponseDirectory, req.RequestId + ".json");
                    if (File.Exists(responsePath)) continue;
                    var res = new iCallManager.Core.BridgeResponse(req.RequestId, true, "found", "test", req.PatientId, "8", "試験太郎", DateTimeOffset.Now, req.Fingerprint());
                    await File.WriteAllTextAsync(responsePath, JsonSerializer.Serialize(res, iCallManager.Core.AppSettings.Json));
                }
                await Task.Delay(100, stop.Token);
            }
            check(liveStore.List().Count == 1 && liveStore.List()[0].ReceptionNo == "8", "background trash scan to durable patient lookup to actual request/response round trip; old files excluded");
        }
        finally { stop.Cancel(); try { await run; } catch (OperationCanceledException) { } }
    }
}
