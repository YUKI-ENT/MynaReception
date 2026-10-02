using System.Text;
using ReceptionAgent.Face;
using ReceptionAgent.Oqs.ReferenceNumber;
using ReceptionAgent.Reception;

internal static class QualificationResultTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var face = new FaceIdentity("ｶﾒﾀﾞ ｼﾞﾕﾘ", new(2000, 1, 1), "0110012345", null, "試験患者", "11");
        var row = new QualificationResultRow("ｶﾒﾀﾞ ｼﾞｭﾘ", "20000101", "110");
        var result = QualificationResultMatcher.Match(face, [row, row, row with { RawChartNumber = "117" }]);
        check(result.VerificationSource == PatientVerificationSource.QualificationResults && !result.InsuranceChecked &&
            result.Selected?.PatientId == "11" && result.Candidates.Count == 1 && result.Selected.RawChartNumbers.Length == 2,
            "WKO final chart groups duplicate records and chart branches without XML insurance");
        check(QualificationResultMatcher.Match(face, [row, row with { RawChartNumber = "220" }]).Selected is null,
            "conflicting WKO patient IDs never select the first or last accumulated row");
        check(QualificationResultMatcher.Match(face, [row with { Birthdate = "20000102" }, row with { NameKana = "カメダ ジロウ" }]).Candidates.Count == 0,
            "WKO requires exact birthday and normalized kana");
        check(QualificationResultMatcher.Match(face, [row with { Birthdate = "20000230" }]).Candidates.Count == 0,
            "WKO invalid calendar date excluded");
        check(QualificationResultMatcher.Match(face, [row with { RawChartNumber = "" }, row with { RawChartNumber = "0" }]).Selected is null,
            "WKO unassigned chart remains pending");
        check(QualificationResultMatcher.Match(face, [row, row with { RawChartNumber = "broken" }]).Selected is null,
            "invalid chart among matching WKO rows prevents false uniqueness");
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-wko-" + Guid.NewGuid().ToString("N"));
        var store = new CaptureStore(root);
        var now = DateTimeOffset.Now;
        string xml = "<XmlMsg><MessageHeader><MedicalInstitutionCode>0110012345</MedicalInstitutionCode><SegmentOfResult>1</SegmentOfResult></MessageHeader><MessageBody><ProcessingResultStatus>1</ProcessingResultStatus><ResultList><ResultOfQualificationConfirmation><NameKana>ｶﾒﾀﾞ ｼﾞﾕﾘ</NameKana><Birthdate>20000101</Birthdate><ReferenceNumber>11</ReferenceNumber></ResultOfQualificationConfirmation></ResultList></MessageBody></XmlMsg>";
        var record = new CaptureRecord { FileName = "wko.xml", ContentHash = "synthetic", Face = face, Encoding = FaceXmlEncoding.Utf8,
            GeneratedAt = now.LocalDateTime, CapturedAt = now.AddMinutes(-10), Stage = CaptureStage.Completed,
            ReconciliationChecks = 24, ReconciliationStatus = "旧検証上限", ReconciliationNextAt = now.AddHours(1) };
        store.Capture(record, Encoding.UTF8.GetBytes(xml));
        int reads = 0;
        Task<FaceLookupResult> Verify(FaceIdentity identity, CancellationToken token)
        { reads++; return Task.FromResult(QualificationResultMatcher.Match(identity, [row])); }
        var service = new SingleReferenceRegistrationService(Path.Combine(root, "jobs"));
        var worker = new ReferenceReconciliation(store, service, Verify, false, () => now, useQualificationResults: true);
        check(await worker.ProcessNextAsync(default), "WKO verification reopens old exhausted verification once");
        var done = store.Get(record.Id);
        check(done.QualificationReconciliationCompleted && done.ReconciliationChecks == 1 && done.VerifiedPatientId == "11" &&
            done.ReconciliationStatus.Contains("検証完了") && !done.ReconciliationStatus.Contains("上限"),
            "final matching XML reference completes immediately without insurance or false upper-limit warning");
        now = now.AddHours(1);
        check(!await new ReferenceReconciliation(new CaptureStore(root), service, Verify, false, () => now, useQualificationResults: true)
            .ProcessNextAsync(default) && reads == 1, "completed WKO validation survives restart without repeated COM checks");
        var noReferenceFace = face with { ReferenceNumber = null };
        var waiting = new CaptureRecord { FileName = "wko-wait.xml", ContentHash = "wait", Face = noReferenceFace,
            GeneratedAt = now.LocalDateTime, CapturedAt = now.AddMinutes(-10), Stage = CaptureStage.NeedsReview };
        store.Capture(waiting, Encoding.UTF8.GetBytes(xml.Replace("<ReferenceNumber>11</ReferenceNumber>", "")));
        await worker.ProcessNextAsync(default);
        check(store.Get(waiting.Id).VerifiedPatientId == "11" && !store.Get(waiting.Id).QualificationReconciliationCompleted &&
            service.Load(waiting.Id) is null, "WKO verification displays final chart without sending while automatic registration OFF");
    }
}
