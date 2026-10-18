using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

/// <summary>A verified reception slip, separate from iCall arrival and ticket operations.</summary>
public sealed record KioskReceipt(string SessionId, string PatientId, string Name, string NameKana,
    string ReceptionNo, DateTimeOffset ReceivedAt)
{
    public string XmlName { get; init; } = "";
    public string XmlNameKana { get; init; } = "";

    public static bool CanPrintStaff(CaptureRecord capture) => !capture.Conflict && !capture.ReconciliationIdentityMismatch &&
        capture.Face is not null && (!string.IsNullOrWhiteSpace(capture.Face.PatientName) || !string.IsNullOrWhiteSpace(capture.XmlPatientName)) &&
        (capture.Stage is CaptureStage.Completed or CaptureStage.NeedsReview ||
            capture.RetryAfter != default && capture.Stage is CaptureStage.Captured or CaptureStage.FindWaiting or CaptureStage.CandidateFindWaiting);

    public static KioskReceipt CreateStaff(CaptureRecord capture)
    {
        if (!CanPrintStaff(capture)) throw new InvalidOperationException("XMLの氏名を取得し、患者・予約の照会が終了した受付を選択してください。");
        var patient = capture.Lookup is { Selected: not null, Candidates.Count: 1 } ? capture.Lookup.Selected : null;
        string id = patient is not null && (capture.VerifiedPatientId.Length == 0 || capture.VerifiedPatientId == patient.PatientId) ? patient.PatientId : "";
        string number = capture.ReservationFoundAtReception == true ? capture.ReceptionNo : "";
        return new KioskReceipt(capture.Id, id, patient?.Name ?? capture.ReservationPatientName, patient?.NameKana ?? "", number, capture.CapturedAt).WithXmlIdentity(capture);
    }

    public KioskReceipt WithXmlIdentity(CaptureRecord capture)
    {
        string xmlName = !string.IsNullOrWhiteSpace(capture.Face?.PatientName) ? capture.Face.PatientName : capture.XmlPatientName;
        string xmlKana = capture.Face?.NameKana ?? "";
        return this with { XmlName = xmlName, XmlNameKana = xmlKana,
            Name = !string.IsNullOrWhiteSpace(xmlName) ? xmlName : Name,
            NameKana = !string.IsNullOrWhiteSpace(xmlKana) ? xmlKana : NameKana };
    }

    public static KioskReceipt Create(KioskSession session, CaptureRecord capture)
    {
        bool finishedRoute = session.UsesFlow && session.FlowPageId is { } pageId
            ? session.Options.Flow.Page(pageId).Kind == KioskPageKind.Guidance && session.Options.Flow.Page(pageId).NextStep == KioskNextStep.Finish
            : session.Options.Rules.Any(r => r.Category == session.Category && r.NextStep == KioskNextStep.Finish);
        if (session.CaptureId != capture.Id || session.Category != ReceptionCategory.ReturningWithReservation || !finishedRoute ||
            session.State is not (KioskSessionState.Guidance or KioskSessionState.Closed) ||
            capture.ReceptionCategory != ReceptionCategory.ReturningWithReservation || capture.Conflict ||
            capture.Stage == CaptureStage.NeedsReview || capture.ReconciliationIdentityMismatch || session.MonthDayAnswers?.HasFever == true ||
            string.IsNullOrWhiteSpace(session.PatientId) || string.IsNullOrWhiteSpace(session.ReceptionNo) ||
            session.PatientId != capture.Lookup?.Selected?.PatientId || session.ReceptionNo != capture.ReceptionNo)
            throw new InvalidOperationException("本人・カルテ番号・予約を確認できた再診受付のみ印刷できます。職員確認が必要な場合は先に確認してください。");
        var receipt = new KioskReceipt(session.Id, session.PatientId, session.ConfirmedName,
            capture.Lookup?.Selected?.NameKana ?? "", session.ReceptionNo, session.StartedAt).WithXmlIdentity(capture);
        if (string.IsNullOrWhiteSpace(receipt.Name) || string.IsNullOrWhiteSpace(receipt.NameKana))
            throw new InvalidOperationException("氏名・ふりがなを確認できないため印刷できません。");
        return receipt;
    }
}
