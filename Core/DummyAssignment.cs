namespace iCallManager.Core;

public sealed record AssignmentPatient(string PatientId, string PatientName);

public interface IDummyAssignmentSession
{
    // Older adapters cannot prove whether a failing Commit dispatched an action.
    bool CommitAttempted => true;
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
        // RowParser preserves the reservation list's UI tree order. Select the
        // first available row as displayed, independently of its numeric columns.
        return candidates[0];
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
        Exception? dispatchFailure = null;
        string phase = "commit";
        try
        {
            try { session.Commit(patient, mayOperate); }
            catch (Exception ex) when (session.CommitAttempted)
            {
                // A closed dialog can invalidate COM objects after iCall has accepted
                // the action. Never dispatch again; establish the outcome by reading.
                dispatchFailure = ex;
                OperationalLog.Write("assignment_commit_dispatch_error", new { cause = Describe(ex), ex.StackTrace });
            }
            phase = "assigned_list";
            var result = session.AwaitAssigned(expected, patientId);
            if (result.ReceptionNo != expected.ReceptionNo || result.PatientId != patientId || !result.HasPatientIdentity ||
                expected.InternalId != "" && result.InternalId != expected.InternalId)
                throw new InvalidDataException("割当結果が対象枠と一致しません。");
            if (dispatchFailure is not null)
                OperationalLog.Write("assignment_confirmed_after_dispatch_error", new { phase });
            return result;
        }
        catch (BridgeException ex) when (!session.CommitAttempted)
        {
            OperationalLog.Write("assignment_commit_rejected", new { ex.Code, ex.Message });
            throw;
        }
        catch (Exception ex)
        {
            string cause = $"[phase={phase}] {Describe(ex)}";
            if (dispatchFailure is not null) cause += " [dispatch] " + Describe(dispatchFailure);
            OperationalLog.Write("assignment_outcome_unknown", new { cause, ex.StackTrace });
            throw new BridgeException("outcome_unknown", "割当の完了を確認できません。再実行せずiCall画面を確認してください。 " + cause);
        }
    }

    private static string Describe(Exception ex) => ex is BridgeException bridge
        ? $"[cause={bridge.Code}] {bridge.Message}"
        : $"{ex.GetType().Name} HRESULT=0x{ex.HResult:X8}";
}
