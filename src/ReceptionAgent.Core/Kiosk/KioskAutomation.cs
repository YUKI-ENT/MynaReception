using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

public sealed class KioskAutomationOptions
{
    public bool AutoPrint { get; set; }
    public bool AutoLink { get; set; }
    public bool AutoArrival { get; set; }
    public string PrinterName { get; set; } = "";
    public Dictionary<string, string> RequiredAnswers { get; set; } = [];
    public bool Enabled => AutoPrint || AutoLink || AutoArrival;
    public void Validate(KioskFlow flow)
    {
        if (AutoPrint && string.IsNullOrWhiteSpace(PrinterName)) throw new ArgumentException("自動発券にはプリンタを指定してください。");
        if (RequiredAnswers is null || RequiredAnswers.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) ||
            !flow.Pages.Any(p => p.Kind == KioskPageKind.Choice && p.Variable == pair.Key && p.Choices.Any(c => c.Value == pair.Value))))
            throw new ArgumentException("自動受付条件は質問の保存キーと選択肢の保存値で指定してください。");
    }
}

public enum KioskAutoActionState { Skipped, Pending, Running, Completed, NeedsReview }
public sealed class KioskAutomationJob
{
    public KioskAutoActionState Link { get; set; }
    public KioskAutoActionState Arrival { get; set; }
    public KioskAutoActionState Print { get; set; }
    public ReceptionLinkMode LinkMode { get; set; }
    public string ReceiptDirectory { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string Message { get; set; } = "自動受付待ち";
    public bool NeedsReview { get; set; }
    public bool Finished => NeedsReview || new[] { Link, Arrival, Print }.All(s => s is KioskAutoActionState.Skipped or KioskAutoActionState.Completed);
}

public static class KioskAutomationEligibility
{
    public static bool IsEligible(KioskSession session, CaptureRecord capture, DateTimeOffset now)
    {
        if (session.State != KioskSessionState.Guidance || !session.MonthDayOnly || session.MonthDayAnswers is not { HasFever: false, SaysReserved: true } ||
            session.StartedAt.LocalDateTime.Date != now.LocalDateTime.Date || capture.GeneratedAt.Date != now.LocalDateTime.Date ||
            capture.Lookup?.Selected is null || capture.Lookup.Candidates.Count != 1 ||
            capture.VerifiedPatientId.Length > 0 && capture.VerifiedPatientId != session.PatientId ||
            capture.ReservationPatientName.Length == 0 || capture.Stage is not (CaptureStage.Completed or CaptureStage.ArrivedWaiting or CaptureStage.LinkWaiting) ||
            session.Options.Automation.RequiredAnswers.Any(p => session.Variables.GetValueOrDefault(p.Key) != p.Value)) return false;
        try { _ = KioskReceipt.Create(session, capture); return true; }
        catch (InvalidOperationException) { return false; }
    }
}
