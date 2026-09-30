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
                    if (record.Lookup.Selected is null) throw new InvalidDataException("患者を特定できません: " + record.Lookup.Message);
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
                case CaptureStage.ArrivedWaiting:
                case CaptureStage.LinkWaiting:
                    var request = record.PendingRequest ?? throw new InvalidDataException("保存済み要求がありません。");
                    if (request.Action is "arrived" or "link" && !record.ManualOperationRequested)
                        throw new InvalidDataException("旧設定による自動操作要求を停止しました。iCallManager側の処理結果を確認してください。");
                    // The request was committed in the preceding step, before any external write.
                    var response = await new ICallFileClient().SendAsync(request, record.RequestDirectory, record.ResponseDirectory, TimeSpan.FromSeconds(request.Action == "link" ? 20 : 2), token);
                    record.Responses.Add(response);
                    if (request.Action == "arrived") record.ArrivalResult = response.Code + ": " + response.Message;
                    if (request.Action == "link") record.LinkResult = response.Code + ": " + response.Message;
                    if (!response.Success) throw new InvalidDataException("iCall " + response.Code + ": " + response.Message);
                    if (request.Action == "find")
                    {
                        if (response.Code != "found" || string.IsNullOrWhiteSpace(response.ReceptionNo) || string.IsNullOrWhiteSpace(response.PatientName))
                            throw new InvalidDataException("予約取得応答の内容が不正です。");
                        record.ReceptionNo = response.ReceptionNo; record.ReservationPatientName = response.PatientName;
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
}
