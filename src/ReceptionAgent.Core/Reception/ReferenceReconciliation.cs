using ReceptionAgent.Face;
using ReceptionAgent.Oqs.ReferenceNumber;

namespace ReceptionAgent.Reception;

/// <summary>Persisted, low-frequency verification independent of reservation/arrival operations.</summary>
public sealed class ReferenceReconciliation(CaptureStore store, SingleReferenceRegistrationService registration,
    Func<FaceIdentity, CancellationToken, Task<FaceLookupResult>> verify, bool allowRegistration,
    Func<DateTimeOffset>? clock = null, bool useQualificationResults = false)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.Now;
    public static bool CanVerify(CaptureRecord record) => !record.Conflict && record.Face is not null &&
        record.Stage is CaptureStage.Completed or CaptureStage.NeedsReview &&
        (record.PendingRequest is null || record.PendingRequest.Action is "find" or "find_candidates" &&
            record.Responses.Any(r => r.RequestId == record.PendingRequest.RequestId));
    public async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await ProcessNextAsync(token);
            await Task.Delay(TimeSpan.FromSeconds(10), token); // At most one patient's COM verification per ten seconds.
        }
    }
    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        var now = Now;
        CaptureRecord? record;
        await store.WorkflowGate.WaitAsync(token);
        try
        {
            record = store.List(limit: int.MaxValue, date: DateOnly.FromDateTime(now.LocalDateTime))
                .Where(r => CanVerify(r) && (!useQualificationResults || !r.QualificationReconciliationCompleted) &&
                    (r.ReconciliationChecks < 24 || useQualificationResults && !r.QualificationReconciliationStarted) &&
                    now >= r.CapturedAt.AddMinutes(5) && (now >= r.ReconciliationNextAt || useQualificationResults && !r.QualificationReconciliationStarted))
                .OrderBy(r => r.ReconciliationNextAt).ThenBy(r => r.CapturedAt).FirstOrDefault();
            if (record is null) return false;
            if (useQualificationResults && !record.QualificationReconciliationStarted)
            {
                record.QualificationReconciliationStarted = true;
                record.ReconciliationChecks = 0;
                record.ReconciliationCandidateId = "";
                record.ReconciliationFirstMatchAt = default;
            }
            // Claim and schedule before COM/OQS. Restart or an exception cannot spin on a patient.
            record.ReconciliationChecks++;
            record.ReconciliationNextAt = now.AddMinutes(10);
            store.Save(record);
        }
        finally { store.WorkflowGate.Release(); }
        try
        {
            var face = FaceXmlParser.Parse(store.ReadXml(record.Id), record.Encoding);
            // This delegate always reads Dynamics; it must not trust XML ReferenceNumber or the old lookup.
            var result = await verify(face, token);
            bool finalChart = useQualificationResults && result.VerificationSource == PatientVerificationSource.QualificationResults;
            string candidate = (finalChart || result.InsuranceChecked && PatientInsuranceMatcher.IsUsable(face.Insurance)) &&
                result.Candidates.Count == 1 && result.Selected is { } patient && result.Candidates[0] == patient &&
                patient.Birthdate == face.Birthdate &&
                PatientNameMatcher.NormalizeKanaForMatching(patient.NameKana) == PatientNameMatcher.NormalizeKanaForMatching(face.NameKana)
                ? patient.PatientId : "";
            bool stable = candidate.Length > 0 && (finalChart || record.ReconciliationCandidateId == candidate &&
                record.ReconciliationFirstMatchAt != default && now >= record.ReconciliationFirstMatchAt.AddMinutes(5));
            bool completed = false;
            string status;
            if (candidate.Length == 0)
                status = result.Message;
            else if (!stable)
                status = "再検証候補 " + candidate + "／5分後に再確認";
            else
            {
                // Persist an identity discrepancy before a potentially slow/uncertain OQS submission.
                await UpdateAsync(record.Id, r =>
                {
                    r.VerifiedPatientId = candidate;
                    if (r.Lookup?.Selected is { } original && original.PatientId != candidate) r.ReconciliationIdentityMismatch = true;
                });
                var job = registration.Load(record.Id);
                string currentId = job?.Target.ReferenceNumber ?? face.ReferenceNumber ?? "";
                bool mismatch = currentId != candidate;
                status = mismatch ? "Dynamics確認済み " + candidate + "／照会番号の登録・訂正が必要" : "Dynamicsと照会番号が整合: " + candidate;
                if (allowRegistration && record.AutoRegisterReferenceNumber && mismatch)
                {
                    // Preserve the original capture/lookup and iCall results; only the reference registration target changes.
                    record = store.Get(record.Id); record.Face = face;
                    var registered = await registration.RegisterVerifiedAsync(record, result, record.RegistrationOqsRoot, token);
                    status = registered.Message;
                }
                else if (job?.State == ReferenceRegistrationState.Failed) status = "要確認：照会番号登録が失敗しています。";
                else if (job?.State is ReferenceRegistrationState.ResultWaiting or ReferenceRegistrationState.RequestSubmitted)
                    status = "照会番号登録結果待ち／再送しません";
                var finalJob = registration.Load(record.Id);
                completed = finalChart && (finalJob is { State: ReferenceRegistrationState.Completed } && finalJob.Target.ReferenceNumber == candidate ||
                    finalJob is null && face.ReferenceNumber == candidate);
                if (completed) status = "WKO確定カルテ番号と照会番号が整合: " + candidate + "／検証完了";
            }
            await UpdateAsync(record.Id, r =>
            {
                if (candidate.Length == 0 || r.ReconciliationCandidateId != candidate) r.ReconciliationFirstMatchAt = candidate.Length == 0 ? default : now;
                r.ReconciliationCandidateId = candidate;
                r.VerifiedPatientId = stable ? candidate : "";
                r.ReconciliationStatus = status;
                r.QualificationReconciliationCompleted = completed;
                if (stable && r.Lookup?.Selected is { } original && original.PatientId != candidate)
                    r.ReconciliationStatus += "／受付時カルテ番号と不一致・予約と受付は職員確認";
                r.ReconciliationNextAt = now.AddMinutes(stable ? 30 : candidate.Length > 0 ? 5 : 10);
                if (stable && allowRegistration && r.AutoRegisterReferenceNumber && registration.Load(r.Id) is not null)
                    r.AutomaticRegistrationAttempted = true;
                if (r.ReconciliationChecks >= 24 && !completed) r.ReconciliationStatus += "／未解決のまま本日の再検証上限に到達・職員確認";
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await UpdateAsync(record.Id, r =>
            {
                r.ReconciliationCandidateId = ""; r.ReconciliationFirstMatchAt = default; r.VerifiedPatientId = "";
                r.ReconciliationStatus = "再検証・登録確認待ち: " + ex.Message;
            });
        }
        return true;
    }
    private async Task UpdateAsync(string id, Action<CaptureRecord> update)
    {
        await store.WorkflowGate.WaitAsync();
        try { var current = store.Get(id); if (!current.Conflict) { update(current); store.Save(current); } }
        finally { store.WorkflowGate.Release(); }
    }
}
