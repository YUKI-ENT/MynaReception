using ReceptionAgent.Face;
using ReceptionAgent.ICall;

namespace ReceptionAgent.Reception;

public sealed class ReceptionWorkflow(CaptureStore store, Func<FaceIdentity, CancellationToken, Task<FaceLookupResult>> lookup)
{
    public async Task StepAsync(CaptureRecord record, CancellationToken token)
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
                    record.Lookup = await lookup(record.Face, token);
                    if (record.Lookup.Selected is null) throw new InvalidDataException("患者を特定できません: " + record.Lookup.Message);
                    record.Stage = CaptureStage.PatientIdentified; record.Status = "患者ID取得済み";
                    break;
                case CaptureStage.PatientIdentified:
                    Prepare(record, "find", CaptureStage.FindWaiting); break;
                case CaptureStage.ReservationFound:
                    if (record.MarkArrived) Prepare(record, "arrived", CaptureStage.ArrivedWaiting);
                    else { record.Stage = CaptureStage.LinkReady; record.Status = "来院確認は設定OFF"; }
                    break;
                case CaptureStage.LinkReady:
                    if (record.LinkReservation) Prepare(record, "link", CaptureStage.LinkWaiting);
                    else Complete(record);
                    break;
                case CaptureStage.FindWaiting:
                case CaptureStage.ArrivedWaiting:
                case CaptureStage.LinkWaiting:
                    var request = record.PendingRequest ?? throw new InvalidDataException("保存済み要求がありません。");
                    // The request was committed in the preceding step, before any external write.
                    var response = await new ICallFileClient().SendAsync(request, record.RequestDirectory, record.ResponseDirectory, TimeSpan.FromSeconds(2), token);
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
                        if (response.Code != "invoked") throw new InvalidDataException("操作応答の内容を確認してください。");
                        if (request.Action == "arrived") { record.Stage = CaptureStage.LinkReady; record.Status = "来院確認の操作送信済み"; }
                        else Complete(record);
                    }
                    record.PendingRequest = null;
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
    { record.Stage = CaptureStage.Completed; record.Status = "予約番号取得完了" + (record.MarkArrived || record.LinkReservation ? "／設定したボタンの操作送信済み" : ""); }
}
