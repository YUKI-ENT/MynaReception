namespace iCallManager.Core;

public static class ReceptionTarget
{
    public static Reservation Select(IReadOnlyList<Reservation> rows, string receptionNo, string? patientId = null)
    {
        var matches = rows.Where(r => r.ReceptionNo == receptionNo).ToArray();
        if (matches.Length == 0)
            throw new BridgeException("not_found", "現在の一覧に指定した受付番号がありません。");
        if (matches.Length != 1)
            throw new BridgeException("ambiguous_reception", "同じ受付番号の行が複数あります。操作は行いません。");
        var row = matches[0];
        if (!string.IsNullOrWhiteSpace(patientId) && row.PatientId != patientId)
            throw new BridgeException("identity_changed", "指定した受付番号の診察券番号が一致しません。");
        return row;
    }

    public static Reservation Verify(IReadOnlyList<Reservation> rows, Reservation expected)
    {
        var row = Select(rows, expected.ReceptionNo);
        if (string.IsNullOrWhiteSpace(expected.InternalId) || row.InternalId != expected.InternalId ||
            row.PatientId != expected.PatientId)
            throw new BridgeException("identity_changed", "操作直前に対象受付の内部予約IDまたは診察券番号が変わりました。");
        return row;
    }
}
