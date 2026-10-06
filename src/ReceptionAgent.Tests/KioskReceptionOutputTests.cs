using ReceptionAgent.Face;
using ReceptionAgent.Kiosk;
using ReceptionAgent.Reception;

internal static class KioskReceptionOutputTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "Kiosk-output-" + Guid.NewGuid().ToString("N"));
        var store = new CaptureStore(Path.Combine(root, "captures"));
        var settings = new KioskOptions();
        var clinic = new KioskFlowPage { Id = "clinic", Kind = KioskPageKind.Choice, Title = "外来区分", Variable = "clinicClass", NextPageId = "reserved",
            Choices = [new() { Id = "general", Label = "一般診療", Value = "general" }, new() { Id = "vaccination", Label = "予防接種", Value = "vaccination" }] };
        settings.Flow.Page("birthday").NextPageId = clinic.Id; settings.Flow.Pages.Add(clinic); settings.Save(root);
        var now = new DateTimeOffset(DateTime.Today.AddHours(10));
        var sessions = new KioskSessions(Path.Combine(root, "sessions"), store, () => KioskOptions.Load(root), () => true, () => now);
        var s = sessions.Start("device", Guid.NewGuid().ToString("N"));
        var pending = sessions.ReceptionOutput(s.Id, s.Device);
        check(pending.CaptureId is null && pending.PatientId is null && pending.ClinicClass is null && pending.HasFever is null, "pending output distinguishes unanswered and unidentified values");
        s = sessions.AnswerPage(s.Id, s.Device, new("card"));
        s = sessions.AnswerPage(s.Id, s.Device, new("birthday", Month: 1, Day: 1));
        s = sessions.AnswerPage(s.Id, s.Device, new("clinic", "vaccination"));
        s = sessions.AnswerPage(s.Id, s.Device, new("reserved", "yes"));
        now = now.AddSeconds(2);
        var patient = new FacePatientMatch("00011", "試験患者", "テスト", new(2000, 1, 1), ["110"]);
        var capture = new CaptureRecord { FileName = "synthetic-face.xml", ContentHash = "synthetic", GeneratedAt = now.LocalDateTime, CapturedAt = now,
            Face = new("テスト", new(2000, 1, 1), "0110012345", PatientName: "試験患者"), Lookup = new([patient], patient, false, "mock"),
            ChartNumberFoundAtReception = true, ReservationFoundAtReception = true, ReceptionNo = "1", Stage = CaptureStage.Completed };
        byte[] original = [1, 2, 3]; store.Capture(capture, original);
        sessions.AnswerPage(s.Id, s.Device, new("fever", "no"));
        var output = sessions.ReceptionOutput(s.Id, s.Device);
        check(output.CaptureId == capture.Id && output.FaceXmlFileName == capture.FileName && output.PatientId == "00011" && output.ReceptionNo == "1" &&
            output.HasFever == false && output.SaysReserved == true && output.ClinicClass == "vaccination" && output.BirthMonth == 1 && output.BirthDay == 1,
            "reception output joins original XML with verified chart booking and all patient answers");
        check(store.ReadXml(capture.Id).SequenceEqual(original), "output preserves source XML bytes");
        var receipt = sessions.Receipt(s.Id, s.Device);
        check(receipt.PatientId == "00011" && receipt.ReceptionNo == "1" && receipt.Name == "試験患者" && receipt.NameKana == "テスト", "receipt uses linked verified chart name kana and reservation");
        var receiptSession = sessions.Get(s.Id, s.Device);
        receiptSession.State = KioskSessionState.StaffHelp;
        try { KioskReceipt.Create(receiptSession, capture); check(false, "staff receipt"); }
        catch (InvalidOperationException) { check(true, "staff help session cannot produce reception receipt"); }
        receiptSession.State = KioskSessionState.Cancelled;
        try { KioskReceipt.Create(receiptSession, capture); check(false, "cancelled receipt"); }
        catch (InvalidOperationException) { check(true, "cancelled session cannot produce reception receipt"); }
        receiptSession.State = KioskSessionState.Guidance;
        receiptSession.MonthDayAnswers = receiptSession.MonthDayAnswers! with { HasFever = true };
        try { KioskReceipt.Create(receiptSession, capture); check(false, "fever receipt"); }
        catch (InvalidOperationException) { check(true, "fever staff routing does not produce reception receipt"); }
        receiptSession.MonthDayAnswers = receiptSession.MonthDayAnswers with { HasFever = false };
        receiptSession.PatientId = "other";
        try { KioskReceipt.Create(receiptSession, capture); check(false, "mismatched receipt"); }
        catch (InvalidOperationException) { check(true, "receipt rejects chart number mismatch with source capture"); }
        now = now.AddDays(1);
        try { sessions.Receipt(s.Id, s.Device); check(false, "old receipt"); }
        catch (InvalidOperationException) { check(true, "prior day receipt cannot reuse booking number for today's reception"); }
        now = now.AddDays(-1);
        ((Dictionary<string, string>)output.Answers)["clinicClass"] = "changed";
        check(sessions.ReceptionOutput(s.Id, s.Device).ClinicClass == "vaccination", "handoff answer snapshot cannot mutate stored session");
        var reopened = new KioskSessions(Path.Combine(root, "sessions"), store, () => KioskOptions.Load(root), () => true, () => now);
        check(reopened.ReceptionOutput(s.Id, s.Device).ClinicClass == "vaccination", "outpatient answer and linked output survive restart");
        try { sessions.ReceptionOutput(s.Id, "other"); check(false, "output ownership"); }
        catch (KeyNotFoundException) { check(true, "reception output enforces session ownership"); }
    }
}
