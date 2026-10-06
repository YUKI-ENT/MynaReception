using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

/// <summary>A verified reception slip, separate from iCall arrival and ticket operations.</summary>
public sealed record KioskReceipt(string SessionId, string PatientId, string Name, string NameKana,
    string ReceptionNo, DateTimeOffset ReceivedAt)
{
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
        string name = session.ConfirmedName;
        string kana = capture.Lookup?.Selected?.NameKana ?? capture.Face?.NameKana ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(kana))
            throw new InvalidOperationException("氏名・ふりがなを確認できないため印刷できません。");
        return new(session.Id, session.PatientId, name, kana, session.ReceptionNo, session.StartedAt);
    }
}
