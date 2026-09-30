using System.Text;
using System.Xml.Linq;
using ReceptionAgent.Face;
using ReceptionAgent.Oqs.ReferenceNumber;
using ReceptionAgent.Reception;

internal static class SingleRegistrationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-single-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "req")); Directory.CreateDirectory(Path.Combine(root, "res"));
        string jobs = Path.Combine(root, "jobs");
        var patient = new FacePatientMatch("24434", "試験太郎", "テストタロウ", new(2000, 1, 1), ["244340"]);
        var record = new CaptureRecord { Face = new("テストタロウ", new(2000, 1, 1), "0110012345",
            new("123456", "", "001", "02"), "試験太郎"), Lookup = new([patient], patient, false, "一致") };
        var service = new SingleReferenceRegistrationService(jobs, TimeSpan.FromMilliseconds(650));
        try { await service.RegisterAsync(record, root, default); check(false, "single timeout"); }
        catch (TimeoutException) { check(true, "single request timeout preserves history"); }
        var job = service.Load(record.Id)!;
        check(job.State == ReferenceRegistrationState.ResultWaiting && job.RequestFileName.StartsWith("OQSsiimm01req_"),
            "single protocol uses only siimm01");
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, "req", job.RequestFileName));
        string preview = ReferenceNumberRequestBuilder.Preview(bytes);
        check(preview.Contains("standalone=\"no\"") && preview.Contains("shift_jis", StringComparison.OrdinalIgnoreCase),
            "single Shift_JIS document declaration");
        using var stream = new MemoryStream(bytes);
        var request = XDocument.Load(stream);
        check(request.Descendants("ReferenceNumber").Single().Value == "24434" &&
            request.Descendants("InsurerNumber").Single().Value == "  123456" &&
            !request.Descendants("InsuredCardSymbol").Any() &&
            request.Descendants("InsuredBranchNumber").Single().Value == "02", "face insurance request preserves padding branch and optional symbol");
        XDocument Response(SingleReferenceRegistrationJob j, string status = "1") => new(new XElement("XmlMsg",
            new XElement("MessageHeader", new XElement("MedicalInstitutionCode", j.InstitutionCode),
                new XElement("ArbitraryFileIdentifier", j.Identifier), new XElement("SegmentOfResult", "1")),
            new XElement("MessageBody", new XElement("ReferenceNumber", j.Target.ReferenceNumber),
                new XElement("ProcessingResultStatus", status), new XElement("ProcessingResultCode", "TEST"),
                new XElement("ProcessingResultMessage", "模擬結果"))));
        // Simulate OQS consuming req; restarting must still only read its corresponding res.
        File.Delete(Path.Combine(root, "req", job.RequestFileName));
        await File.WriteAllTextAsync(Path.Combine(root, "res", job.ResponseFileName), Response(job).ToString(), Encoding.UTF8);
        var resumed = await new SingleReferenceRegistrationService(jobs).RegisterAsync(record, "unused-changed-root", default);
        check(resumed.State == ReferenceRegistrationState.Completed && resumed.ProcessingResultMessage == "模擬結果" &&
            !Directory.GetFiles(Path.Combine(root, "req")).Any(), "restart reads existing response using saved root without resubmission");
        check((await service.RegisterAsync(record, root, default)).State == ReferenceRegistrationState.Completed, "completed registration is idempotent");
        var error = Response(job, "2"); SingleReferenceRegistrationService.ParseResult(error, job);
        check(job.State == ReferenceRegistrationState.Failed, "single status 2 is failure");
        var headerError = Response(job); headerError.Root!.Element("MessageHeader")!.Add(new XElement("ErrorCode", "E001"));
        SingleReferenceRegistrationService.ParseResult(headerError, job);
        check(job.State == ReferenceRegistrationState.Failed && job.ErrorCode == "E001", "header error prevents success");
        foreach (string field in new[] { "MedicalInstitutionCode", "ArbitraryFileIdentifier", "ReferenceNumber", "ProcessingResultStatus" })
        {
            var wrong = Response(job); wrong.Descendants(field).Single().Value = "WRONG";
            try { SingleReferenceRegistrationService.ParseResult(wrong, job); check(false, "wrong " + field); }
            catch (InvalidDataException) { check(true, "reject wrong " + field); }
        }
        var duplicate = Response(job); duplicate.Root!.Element("MessageBody")!.Add(new XElement("ProcessingResultStatus", "1"));
        try { SingleReferenceRegistrationService.ParseResult(duplicate, job); check(false, "duplicate status"); }
        catch (InvalidDataException) { check(true, "ambiguous response fields rejected"); }
        record.Face = record.Face with { ReferenceNumber = "24434" };
        try { SingleReferenceRegistrationService.TargetFrom(record); check(false, "already registered"); }
        catch (InvalidDataException) { check(true, "reference already in face XML skips registration"); }
        record.Face = record.Face with { ReferenceNumber = null }; record.Lookup = record.Lookup with { Selected = null };
        try { SingleReferenceRegistrationService.TargetFrom(record); check(false, "ambiguous target"); }
        catch (InvalidDataException) { check(true, "unidentified patient cannot register"); }
        record.Lookup = new([patient, patient with { PatientId = "999" }], patient, false, "旧版の保険照合");
        try { SingleReferenceRegistrationService.TargetFrom(record); check(false, "legacy multiple candidates"); }
        catch (InvalidDataException) { check(true, "multiple candidates cannot register even with legacy selected patient"); }
        var optional = new ReferenceNumberRequestBuilder().Build("0110012345", "optional", new("24434", "123456", "", "001", ""), singlePatient: true);
        using var optionalStream = new MemoryStream(optional);
        check(!XDocument.Load(optionalStream).Descendants("InsuredBranchNumber").Any(), "single optional branch omitted without fabrication");
        var faceXml = Encoding.UTF8.GetBytes("<XmlMsg><MessageHeader><MedicalInstitutionCode>0110012345</MedicalInstitutionCode><SegmentOfResult>1</SegmentOfResult></MessageHeader><MessageBody><ProcessingResultStatus>1</ProcessingResultStatus><ResultList><ResultOfQualificationConfirmation><NameKana>テスト</NameKana><Birthdate>20000101</Birthdate><ReferenceNumber>24434</ReferenceNumber></ResultOfQualificationConfirmation></ResultList></MessageBody></XmlMsg>");
        check(FaceXmlParser.Parse(faceXml, FaceXmlEncoding.Utf8).ReferenceNumber == "24434", "face parser preserves existing reference number");
        check(record.Stage == CaptureStage.Captured && record.ReceptionNo == "", "registration is independent of reception state");
    }
}
