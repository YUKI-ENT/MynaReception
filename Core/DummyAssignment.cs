namespace iCallManager.Core;

public sealed record AssignmentPatient(string PatientId, string PatientName);

public interface IDummyAssignmentSession
{
    void Open(Reservation expected, Func<bool> mayOperate);
    AssignmentPatient Search(string patientId, Func<bool> mayOperate);
    void Commit(AssignmentPatient patient, Func<bool> mayOperate);
    Reservation AwaitAssigned(Reservation expected, string patientId);
}

public static class DummyAssignment
{
    public static Reservation Select(IReadOnlyList<Reservation> rows, BridgeRequest request)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.PatientId, @"\A[0-9]{1,50}\z"))
            throw new BridgeException("invalid_request", "patientIdには枝番なしカルテ番号を半角数字で指定してください。");
        var existing = rows.Where(r => r.PatientId == request.PatientId).ToArray();
        if (existing.Length > 1) throw new BridgeException("ambiguous_patient", "同じカルテ番号が複数の予約にあります。");
        if (existing.Length == 1) throw new BridgeException("already_reserved", "この患者は既に予約枠に登録されています。findで取得してください。");
        var candidates = rows.Where(r => r.IsUnassignedDummy && r.CanAssignDummy).ToArray();
        if (!string.IsNullOrWhiteSpace(request.ExpectedReceptionNo))
            candidates = candidates.Where(r => r.ReceptionNo == request.ExpectedReceptionNo).ToArray();
        if (candidates.Length == 0) throw new BridgeException("no_dummy_slot", "操作可能な未割当ダミー枠がありません。");
        if (request.ExpectedReceptionNo is not null)
        {
            if (candidates.Length != 1) throw new BridgeException("ambiguous_dummy_slot", "指定した受付番号が一意ではありません。");
            return candidates[0];
        }
        if (candidates.Any(r => r.WaitingOrder is null or <= 0))
            throw new BridgeException("dummy_order_unverified", "ダミー枠の待ち順を確認できません。受付番号を指定してください。");
        int earliest = candidates.Min(r => r.WaitingOrder!.Value);
        var first = candidates.Where(r => r.WaitingOrder == earliest).ToArray();
        if (first.Length != 1) throw new BridgeException("ambiguous_dummy_slot", "最初のダミー枠を一意に選べません。");
        return first[0];
    }

    public static Reservation Run(IDummyAssignmentSession session, Reservation expected, string patientId, Func<bool> mayOperate)
    {
        if (!expected.IsUnassignedDummy || !expected.CanAssignDummy)
            throw new BridgeException("dummy_slot_changed", "対象枠は未割当ダミーではありません。");
        if (!mayOperate()) throw new BridgeException("operations_disabled", "実操作が無効です。");
        session.Open(expected, mayOperate);
        var patient = session.Search(patientId, mayOperate);
        if (patient.PatientId != patientId || string.IsNullOrWhiteSpace(patient.PatientName) || patient.PatientName == "-")
            throw new BridgeException("patient_search_unverified", "患者検索結果の診察券番号を確認できません。割当は行いません。");
        if (!mayOperate()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
        try
        {
            session.Commit(patient, mayOperate);
            var result = session.AwaitAssigned(expected, patientId);
            if (result.ReceptionNo != expected.ReceptionNo || result.PatientId != patientId || !result.HasPatientIdentity ||
                expected.InternalId != "" && result.InternalId != expected.InternalId)
                throw new InvalidDataException("割当結果が対象枠と一致しません。");
            return result;
        }
        catch (Exception ex)
        {
            throw new BridgeException("outcome_unknown", "割当の完了を確認できません。再実行せずiCall画面を確認してください。（" + ex.GetType().Name + "）");
        }
    }
}
