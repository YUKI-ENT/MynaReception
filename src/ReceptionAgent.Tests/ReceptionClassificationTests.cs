using System.Text;
using System.Text.Json;
using ReceptionAgent.Face;
using ReceptionAgent.ICall;
using ReceptionAgent.Reception;

internal static class ReceptionClassificationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-classification-" + Guid.NewGuid().ToString("N"));
        string reqDir = Path.Combine(root, "request"), resDir = Path.Combine(root, "response");
        Directory.CreateDirectory(reqDir); Directory.CreateDirectory(resDir);
        var store = new CaptureStore(Path.Combine(root, "db"));
        string xml = "<XmlMsg><MessageHeader><MedicalInstitutionCode>0110012345</MedicalInstitutionCode><SegmentOfResult>1</SegmentOfResult></MessageHeader><MessageBody><ProcessingResultStatus>1</ProcessingResultStatus><ResultList><ResultOfQualificationConfirmation><Name>試験患者</Name><NameKana>テスト</NameKana><Birthdate>20000101</Birthdate></ResultOfQualificationConfirmation></ResultList></MessageBody></XmlMsg>";
        var patient = new FacePatientMatch("11", "試験患者", "テスト", new(2000, 1, 1), ["110"]);
        foreach (bool hasChart in new[] { true, false })
        foreach (bool hasReservation in new[] { true, false })
        {
            var record = new CaptureRecord { FileName = Guid.NewGuid().ToString("N") + ".xml", ContentHash = "synthetic",
                Encoding = FaceXmlEncoding.Utf8, GeneratedAt = DateTime.Now.AddSeconds(-20), RequestDirectory = reqDir, ResponseDirectory = resDir };
            store.Capture(record, Encoding.UTF8.GetBytes(xml));
            var workflow = new ReceptionWorkflow(store, (f, t) => Task.FromResult(hasChart ?
                new FaceLookupResult([patient], patient, false, "一致") : new FaceLookupResult([], null, false, "PATIENT_NOT_FOUND")));
            await workflow.StepAsync(record, default);
            if (hasChart) await workflow.StepAsync(record, default);
            check(record.ReceptionCategory == ReceptionCategory.Pending, "classification stays pending until reservation read returns");
            var request = record.PendingRequest!;
            var managerRequest = new iCallManager.Core.BridgeRequest(request.RequestId, request.Action, request.PatientId,
                request.ExpectedReceptionNo, request.ExpectedPatientName)
            { PatientName = request.PatientName, NameKana = request.NameKana, GivenName = request.GivenName, Birthdate = request.Birthdate };
            check(managerRequest.Fingerprint() == request.Fingerprint(), "candidate request fingerprint matches manager including optional search keys");
            var response = hasChart ? new ICallResponse(request.RequestId, hasReservation, hasReservation ? "found" : "not_found", "test", "11",
                hasReservation ? "7" : null, hasReservation ? "試験患者" : null, DateTimeOffset.Now, request.Fingerprint()) :
                new ICallResponse(request.RequestId, true, hasReservation ? "candidates_found" : "no_candidates", "test", null, null, null, DateTimeOffset.Now, request.Fingerprint())
                { Candidates = hasReservation ? [new("7", null, "試験患者 (2000年1月1日)", "試験患者", "2000-01-01", "full_name")] : [] };
            await File.WriteAllTextAsync(Path.Combine(resDir, request.RequestId + ".json"), JsonSerializer.Serialize(response, ICallFileClient.Json));
            await workflow.StepAsync(record, default);
            var expected = (hasChart, hasReservation) switch
            {
                (true, true) => ReceptionCategory.ReturningWithReservation,
                (true, false) => ReceptionCategory.ReturningWithoutReservation,
                (false, true) => ReceptionCategory.NewWithReservation,
                _ => ReceptionCategory.NewWithoutReservation
            };
            check(record.ReceptionCategory == expected && record.ChartNumberFoundAtReception == hasChart && record.ReservationFoundAtReception == hasReservation,
                "four reception outcomes persisted: " + expected);
            check(record.PendingRequest is null && record.ReceptionClassifiedAt is not null && ReceptionClassification.AcquisitionTime(record)?.TotalSeconds >= 20,
                "classification records XML-to-result duration and clears completed request");
            var timestamp = record.ReceptionClassifiedAt;
            if (record.Stage == CaptureStage.ReservationFound) await workflow.StepAsync(record, default);
            record.VerifiedPatientId = "11"; store.Save(record);
            check(store.Get(record.Id).ReceptionCategory == expected && store.Get(record.Id).ReceptionClassifiedAt == timestamp,
                "later chart registration/reverification never changes original reception category or stopwatch");
        }
        var ambiguous = new CaptureRecord { FileName = "ambiguous.xml", ContentHash = "ambiguous", Encoding = FaceXmlEncoding.Utf8,
            GeneratedAt = DateTime.Now, RequestDirectory = reqDir, ResponseDirectory = resDir };
        store.Capture(ambiguous, Encoding.UTF8.GetBytes(xml));
        var lookupWorkflow = new ReceptionWorkflow(store, (f, t) => Task.FromResult(new FaceLookupResult([], null, false, "PATIENT_NOT_FOUND")));
        await lookupWorkflow.StepAsync(ambiguous, default);
        var pending = ambiguous.PendingRequest!;
        var ambiguousResponse = new ICallResponse(pending.RequestId, true, "candidates_found", "test", null, null, null, DateTimeOffset.Now, pending.Fingerprint())
        { Candidates = [new("8", null, "別名 (2000年1月1日)", "別名", "2000-01-01", "birthdate_only")] };
        await File.WriteAllTextAsync(Path.Combine(resDir, pending.RequestId + ".json"), JsonSerializer.Serialize(ambiguousResponse, ICallFileClient.Json));
        await lookupWorkflow.StepAsync(ambiguous, default);
        check(ambiguous.ReceptionCategory == ReceptionCategory.NeedsReview && ambiguous.ReservationFoundAtReception is null,
            "birthday-only candidate never becomes no-reservation or confirmed reservation category");
        check(new CaptureRecord { Stage = CaptureStage.NeedsReview }.ReceptionCategory == ReceptionCategory.NeedsReview,
            "lookup/XML error never classified as new patient without reservation");
        var legacy = new CaptureRecord { Lookup = new([patient], patient, false, "一致"), GeneratedAt = DateTime.Now.AddSeconds(-10) };
        legacy.Responses.Add(new("ra-test-find", false, "not_found", "test", "11", null, null, DateTimeOffset.Now));
        check(legacy.ReceptionCategory == ReceptionCategory.ReturningWithoutReservation && ReceptionClassification.AcquisitionTime(legacy)?.TotalSeconds >= 10,
            "old records derive confirmed no-reservation category and historical response time");
        legacy.ReconciliationIdentityMismatch = true;
        check(legacy.ReceptionCategory == ReceptionCategory.NeedsReview, "confirmed identity mismatch overrides kiosk branch category");
    }
}
