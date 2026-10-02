using ReceptionAgent.Face;
using ReceptionAgent.ICall;

namespace ReceptionAgent.Reception;

public sealed class ReceptionWorkflow(CaptureStore store, Func<FaceIdentity, CancellationToken, Task<FaceLookupResult>> lookup)
{
    public async Task StepAsync(CaptureRecord record, CancellationToken token)
    {
        await store.WorkflowGate.WaitAsync(token);
        try { await StepCoreAsync(record, token); }
        finally { store.WorkflowGate.Release(); }
    }
    public async Task StepByIdAsync(string id, CancellationToken token)
    {
        await store.WorkflowGate.WaitAsync(token);
        try { await StepCoreAsync(store.Get(id), token); }
        finally { store.WorkflowGate.Release(); }
    }
    public async Task QueueManualAsync(string id, string action, CancellationToken token)
    {
        if (action is not ("arrived" or "link")) throw new ArgumentException("操作が不正です。");
        await store.WorkflowGate.WaitAsync(token);
        try
        {
            var record = store.Get(id);
            if (record.ReconciliationIdentityMismatch || record.VerifiedPatientId.Length > 0 && record.VerifiedPatientId != record.Lookup?.Selected?.PatientId)
                throw new InvalidDataException("Dynamics再検証のカルテ番号が受付時と異なります。職員が予約・受付を確認してください。");
            if (record.Conflict || record.GeneratedAt.Date != DateTime.Today || record.Lookup?.Selected is null ||
                string.IsNullOrWhiteSpace(record.ReceptionNo) || string.IsNullOrWhiteSpace(record.ReservationPatientName))
                throw new InvalidDataException("当日分の患者・予約が特定済みの行を選択してください。");
            if (record.PendingRequest is not null)
            {
                if (record.PendingRequest.Action == action && record.Stage is CaptureStage.ArrivedWaiting or CaptureStage.LinkWaiting) return;
                throw new InvalidDataException("別の要求が未完了です。結果を確認してください。");
            }
            if (record.Stage != CaptureStage.Completed || record.Responses.Any(r => r.RequestId == "ra-" + record.Id + "-" + action))
                throw new InvalidDataException("未完了、または操作結果が記録済みです。重複要求は行いません。");
            Prepare(record, action, action == "arrived" ? CaptureStage.ArrivedWaiting : CaptureStage.LinkWaiting);
            record.RetryAfter = default; record.ManualOperationRequested = true;
            store.Save(record);
        }
        finally { store.WorkflowGate.Release(); }
    }
    private async Task StepCoreAsync(CaptureRecord record, CancellationToken token)
    {
        if (record.Conflict || record.Stage is CaptureStage.Completed or CaptureStage.NeedsReview) return;
        try
        {
            if (record.GeneratedAt.Date != DateTime.Today)
                throw new InvalidDataException("当日分ではありません。未完了の要求はiCallManager側の結果も確認してください。自動送信を停止しました。");
            switch (record.Stage)
            {
                case CaptureStage.Captured:
                    record.Face = FaceXmlParser.Parse(store.ReadXml(record.Id), record.Encoding);
                    record.XmlPatientName = record.Face.PatientName ?? "";
                    store.Save(record);
                    var referencePatient = FaceReferencePatient.Resolve(record.Face);
                    record.Lookup = referencePatient ?? await lookup(record.Face, token);
                    record.PatientIdentifiedByDynamics = referencePatient is null && record.Lookup.Selected is not null;
                    if (record.Lookup.Selected is null)
                    {
                        if (record.Lookup.Candidates.Count != 0) throw new InvalidDataException("患者を特定できません: " + record.Lookup.Message);
                        record.ChartNumberFoundAtReception = false;
                        record.PendingRequest = new("ra-" + record.Id + "-find_candidates", "find_candidates", "")
                        { PatientName = string.IsNullOrWhiteSpace(record.Face.PatientName) ? null : record.Face.PatientName,
                            NameKana = record.Face.NameKana, Birthdate = record.Face.Birthdate.ToString("yyyy-MM-dd") };
                        record.Stage = CaptureStage.CandidateFindWaiting; record.Status = "カルテ一致なし／氏名・生年月日で予約照会中";
                        break;
                    }
                    record.ChartNumberFoundAtReception = true;
                    record.Stage = CaptureStage.PatientIdentified; record.Status = "患者ID取得済み";
                    break;
                case CaptureStage.PatientIdentified:
                    Prepare(record, "find", CaptureStage.FindWaiting); break;
                case CaptureStage.ReservationFound:
                    Complete(record);
                    break;
                case CaptureStage.LinkReady:
                    Complete(record);
                    break;
                case CaptureStage.FindWaiting:
                case CaptureStage.CandidateFindWaiting:
                case CaptureStage.ArrivedWaiting:
                case CaptureStage.LinkWaiting:
                    var request = record.PendingRequest ?? throw new InvalidDataException("保存済み要求がありません。");
                    if (request.Action is "arrived" or "link" && !record.ManualOperationRequested)
                        throw new InvalidDataException("旧設定による自動操作要求を停止しました。iCallManager側の処理結果を確認してください。");
                    // The request was committed in the preceding step, before any external write.
                    var response = await new ICallFileClient().SendAsync(request, record.RequestDirectory, record.ResponseDirectory, TimeSpan.FromSeconds(request.Action == "link" ? 20 : 2), token);
                    record.Responses.Add(response);
                    if (request.Action == "find_candidates")
                    {
                        ApplyCandidateResponse(record, response);
                        record.PendingRequest = null;
                        break;
                    }
                    if (request.Action == "find" && !response.Success && response.Code == "not_found")
                    {
                        record.ReservationFoundAtReception = false; record.ReceptionClassifiedAt = DateTimeOffset.Now;
                        record.Stage = CaptureStage.Completed; record.Status = "カルテ番号取得済み／現在のiCall一覧に予約なし";
                        record.PendingRequest = null;
                        break;
                    }
                    if (request.Action == "arrived") record.ArrivalResult = response.Code + ": " + response.Message;
                    if (request.Action == "link") record.LinkResult = response.Code + ": " + response.Message;
                    if (!response.Success) throw new InvalidDataException("iCall " + response.Code + ": " + response.Message);
                    if (request.Action == "find")
                    {
                        if (response.Code != "found" || string.IsNullOrWhiteSpace(response.ReceptionNo) || string.IsNullOrWhiteSpace(response.PatientName))
                            throw new InvalidDataException("予約取得応答の内容が不正です。");
                        record.ReceptionNo = response.ReceptionNo; record.ReservationPatientName = response.PatientName;
                        record.ReservationFoundAtReception = true; record.ReceptionClassifiedAt = DateTimeOffset.Now;
                        record.Stage = CaptureStage.ReservationFound; record.Status = "予約番号取得済み";
                    }
                    else
                    {
                        if (request.Action == "link" ? response.Code is not ("linked" or "already_linked" or "invoked") : response.Code != "invoked")
                            throw new InvalidDataException("操作応答の内容を確認してください。");
                        Complete(record);
                    }
                    record.PendingRequest = null; record.ManualOperationRequested = false;
                    break;
            }
            record.RetryAfter = default;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or System.Text.DecoderFallbackException)
        { record.Stage = CaptureStage.NeedsReview; record.Status = ex.Message; }
        catch (Exception ex)
        { record.Status = "再試行待ち: " + ex.Message; record.RetryAfter = DateTimeOffset.Now.AddSeconds(10); }
        store.Save(record);
    }
    private static void Prepare(CaptureRecord record, string action, CaptureStage stage)
    {
        record.PendingRequest = new("ra-" + record.Id + "-" + action, action, record.Lookup!.Selected!.PatientId,
            action == "find" ? null : record.ReceptionNo, action == "find" ? null : record.ReservationPatientName);
        record.Stage = stage; record.Status = "iCall " + action + " 応答待ち";
    }
    private static void Complete(CaptureRecord record)
    { record.Stage = CaptureStage.Completed; record.Status = "予約番号取得完了／来院確認・連携は手動操作"; }
    private static void ApplyCandidateResponse(CaptureRecord record, ICallResponse response)
    {
        if (!response.Success || response.Candidates is null || response.Code is not ("candidates_found" or "no_candidates"))
            throw new InvalidDataException("予約候補の照会に失敗: " + response.Code + "／" + response.Message);
        if (response.Code == "no_candidates" && response.Candidates.Count == 0)
        {
            record.ReservationFoundAtReception = false;
            record.Status = "カルテ一致なし／現在の一覧に氏名・生年月日の予約候補なし";
        }
        else if (response.Code == "candidates_found" && response.Candidates.Count == 1 && response.Candidates[0] is { NameMatch: "full_name", PatientId: null } candidate &&
            !string.IsNullOrWhiteSpace(candidate.ReceptionNo) && candidate.Birthdate == record.Face?.Birthdate.ToString("yyyy-MM-dd"))
        {
            record.ReceptionNo = candidate.ReceptionNo; record.ReservationPatientName = candidate.PatientName;
            record.ReservationFoundAtReception = true;
            record.Status = "カルテ一致なし／氏名・生年月日が一致する予約候補あり・本人確認が必要";
        }
        else throw new InvalidDataException("予約候補の本人確認が必要（複数候補・氏名不一致・カルテ番号の不整合）。");
        record.ReceptionClassifiedAt = DateTimeOffset.Now;
        record.Stage = CaptureStage.Completed;
    }
}
