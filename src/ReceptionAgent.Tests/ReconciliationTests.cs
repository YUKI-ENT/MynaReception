using System.Text;
using System.Xml.Linq;
using ReceptionAgent.Face;
using ReceptionAgent.Oqs.ReferenceNumber;
using ReceptionAgent.Reception;

internal static class ReconciliationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-reconcile-" + Guid.NewGuid().ToString("N"));
        string oqs = Path.Combine(root, "oqs"), jobs = Path.Combine(root, "jobs");
        Directory.CreateDirectory(Path.Combine(oqs, "req")); Directory.CreateDirectory(Path.Combine(oqs, "res"));
        var store = new CaptureStore(Path.Combine(root, "db"));
        var service = new SingleReferenceRegistrationService(jobs, TimeSpan.FromMilliseconds(150));
        var now = new DateTimeOffset(DateTime.Today.AddHours(10));
        string xml = "<XmlMsg><MessageHeader><MedicalInstitutionCode>0110012345</MedicalInstitutionCode><SegmentOfResult>1</SegmentOfResult></MessageHeader><MessageBody><ProcessingResultStatus>1</ProcessingResultStatus><ResultList><ResultOfQualificationConfirmation><NameKana>テスト</NameKana><Birthdate>20000101</Birthdate><ReferenceNumber>99</ReferenceNumber><InsurerNumber>12345678</InsurerNumber><InsuredIdentificationNumber>001</InsuredIdentificationNumber><InsuredBranchNumber>00</InsuredBranchNumber></ResultOfQualificationConfirmation></ResultList></MessageBody></XmlMsg>";
        var face = FaceXmlParser.Parse(Encoding.UTF8.GetBytes(xml), FaceXmlEncoding.Utf8);
        var patient = new FacePatientMatch("11", "試験患者", "テスト", face.Birthdate, ["110"]);
        var policies = new[] { new FaceInsuranceCandidate("110", face.Insurance!) };
        var verified = PatientInsuranceMatcher.Verify([patient], face.Insurance, policies);
        check(verified.Selected?.PatientId == patient.PatientId && verified.Selected.RawChartNumbers.SequenceEqual(patient.RawChartNumbers) && verified.InsuranceChecked, "reverification checks insurance even for a single name candidate");
        check(PatientInsuranceMatcher.Verify([patient], face.Insurance, []).Selected is null,
            "chart with unfinished insurance cannot verify");
        check(PatientInsuranceMatcher.Verify([patient, patient with { PatientId = "22", RawChartNumbers = ["220"] }], face.Insurance,
            [.. policies, new("220", face.Insurance!)]).Selected is null, "multiple matching insured patients never verify");
        var old = patient with { PatientId = "99", RawChartNumbers = ["990"] };
        var record = new CaptureRecord { FileName = "test.xml", ContentHash = "hash", Face = face,
            GeneratedAt = now.LocalDateTime, CapturedAt = now, Stage = CaptureStage.Completed,
            Lookup = new([old], old, false, "XML reference"), ReceptionNo = "7", ReservationPatientName = "旧照合",
            AutoRegisterReferenceNumber = true, RegistrationOqsRoot = oqs };
        store.Capture(record, Encoding.UTF8.GetBytes(xml));
        var failedFind = new CaptureRecord { Face = face, Stage = CaptureStage.NeedsReview,
            PendingRequest = new("ra-test-find", "find", "99") };
        check(!ReferenceReconciliation.CanVerify(failedFind), "unresolved request excluded from reconciliation");
        failedFind.Responses.Add(new("ra-test-find", false, "not_found", "not found", "99", null, null, now));
        check(ReferenceReconciliation.CanVerify(failedFind), "completed not_found reservation lookup still permits reference verification");
        failedFind.PendingRequest = new("ra-test-link", "link", "99");
        failedFind.Responses.Add(new("ra-test-link", false, "outcome_unknown", "uncertain", "99", null, null, now));
        check(!ReferenceReconciliation.CanVerify(failedFind), "pending write operation never treated as a completed read");
        int lookups = 0;
        bool available = false;
        Task<FaceLookupResult> Verify(FaceIdentity f, CancellationToken token)
        {
            lookups++;
            check(f.ReferenceNumber == "99", "verification receives original XML without adopting its ID");
            return Task.FromResult(available ? verified : PatientInsuranceMatcher.Verify([], f.Insurance, []));
        }
        var worker = new ReferenceReconciliation(store, service, Verify, true, () => now);
        check(!await worker.ProcessNextAsync(default) && lookups == 0, "reverification waits five minutes after capture");
        now = now.AddMinutes(5);
        check(await worker.ProcessNextAsync(default) && service.Load(record.Id) is null,
            "new patient missing from Dynamics stays deferred without registration");
        available = true; now = now.AddMinutes(10);
        await worker.ProcessNextAsync(default);
        check(store.Get(record.Id).ReconciliationCandidateId == "11" && service.Load(record.Id) is null,
            "first verified match waits for a second spaced observation");
        var restarted = new ReferenceReconciliation(new CaptureStore(Path.Combine(root, "db")), service, Verify, true, () => now);
        check(!await restarted.ProcessNextAsync(default), "restart preserves deferred schedule");
        now = now.AddMinutes(5);
        await restarted.ProcessNextAsync(default); // Intent persisted, synthetic server not yet replying.
        var pending = service.Load(record.Id)!;
        check(pending.Target.ReferenceNumber == "11" && pending.PreviousReferenceNumber == "99" && pending.CorrectionCount == 1,
            "stable Dynamics verification overrides incorrect XML reference with auditable correction");
        check(store.Get(record.Id).Lookup!.Selected!.PatientId == "99" && store.Get(record.Id).ReceptionNo == "7",
            "reference correction never rewrites reservation or original identification");
        int files = Directory.GetFiles(Path.Combine(oqs, "req")).Length;
        now = now.AddMinutes(10); await restarted.ProcessNextAsync(default);
        now = now.AddMinutes(5); await restarted.ProcessNextAsync(default);
        check(Directory.GetFiles(Path.Combine(oqs, "req")).Length == files && store.Get(record.Id).VerifiedPatientId == "11",
            "uncertain registration is not resent after revalidation");
        try { await new ReceptionWorkflow(store, Verify).QueueManualAsync(record.Id, "arrived", default); check(false, "wrong reservation blocked"); }
        catch (InvalidDataException) { check(true, "verified mismatch blocks original patient arrival operation"); }
        var secondPatient = patient with { PatientId = "22", RawChartNumbers = ["220"] };
        var secondVerified = new FaceLookupResult([secondPatient], secondPatient, true, "verified");
        try { await service.RegisterVerifiedAsync(store.Get(record.Id), secondVerified, oqs, default); check(false, "pending correction blocked"); }
        catch (InvalidDataException) { check(true, "pending registration cannot be replaced by another target"); }
        XDocument Reply(SingleReferenceRegistrationJob job) => new(new XElement("XmlMsg",
            new XElement("MessageHeader", new XElement("MedicalInstitutionCode", job.InstitutionCode),
                new XElement("ArbitraryFileIdentifier", job.Identifier), new XElement("SegmentOfResult", "1")),
            new XElement("MessageBody", new XElement("ReferenceNumber", job.Target.ReferenceNumber), new XElement("ProcessingResultStatus", "1"))));
        await File.WriteAllTextAsync(Path.Combine(oqs, "res", pending.ResponseFileName), Reply(pending).ToString());
        await service.RefreshResultsAsync(default);
        try { await service.RegisterVerifiedAsync(store.Get(record.Id), secondVerified, oqs, default); check(false, "oscillation blocked"); }
        catch (InvalidDataException) { check(true, "second correction requires staff instead of oscillating IDs"); }

        // A completed initial wrong registration is archived before a new correction intent is published.
        var initial = new CaptureRecord { Face = face with { ReferenceNumber = null }, GeneratedAt = DateTime.Now,
            Lookup = new([old], old, false, "initial") };
        try { await service.RegisterAsync(initial, oqs, default); } catch (TimeoutException) { }
        var initialJob = service.Load(initial.Id)!;
        await File.WriteAllTextAsync(Path.Combine(oqs, "res", initialJob.ResponseFileName), Reply(initialJob).ToString());
        await service.RefreshResultsAsync(default);
        try { await service.RegisterVerifiedAsync(initial, verified, oqs, default); } catch (TimeoutException) { }
        var corrected = service.Load(initial.Id)!;
        check(corrected.Target.ReferenceNumber == "11" && corrected.RequestFileName != initialJob.RequestFileName &&
            File.Exists(Path.Combine(jobs, "History", initial.Id + "-" + initialJob.Identifier + ".json")),
            "completed wrong registration archived with fresh correction request and identifier");
        var off = new CaptureRecord { FileName = "off.xml", ContentHash = "off", Face = face, GeneratedAt = now.LocalDateTime,
            CapturedAt = now.AddMinutes(-10), Stage = CaptureStage.NeedsReview, AutoRegisterReferenceNumber = false };
        store.Capture(off, Encoding.UTF8.GetBytes(xml));
        await worker.ProcessNextAsync(default); now = now.AddMinutes(5); await worker.ProcessNextAsync(default);
        check(service.Load(off.Id) is null, "automatic registration OFF still verifies but never sends");
        var fresh = new CaptureRecord { Face = face with { ReferenceNumber = null }, GeneratedAt = DateTime.Now,
            Lookup = new([], null, false, "PATIENT_NOT_FOUND") };
        try { await service.RegisterVerifiedAsync(fresh, verified, oqs, default); } catch (TimeoutException) { }
        check(service.Load(fresh.Id) is { Target.ReferenceNumber: "11", CorrectionCount: 0 },
            "newly created chart gets first reference registration despite original failed lookup");
        var historical = new CaptureRecord { Face = face, GeneratedAt = DateTime.Today.AddDays(-1), Lookup = initial.Lookup };
        try { await service.RegisterVerifiedAsync(historical, verified, oqs, default); check(false, "historical correction blocked"); }
        catch (InvalidDataException) { check(true, "previous-day XML never used for automatic correction"); }
    }
}
