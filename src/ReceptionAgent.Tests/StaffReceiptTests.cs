using ReceptionAgent;
using ReceptionAgent.Face;
using ReceptionAgent.Kiosk;
using ReceptionAgent.Reception;

internal static class StaffReceiptTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "staff-receipt-" + Guid.NewGuid().ToString("N"));
        var store = new CaptureStore(Path.Combine(root, "captures"));
        var options = new AgentSettings { OperationMode = ReceptionOperationMode.Kiosk };
        options.Save(root);
        check(AgentSettings.Load(root).OperationMode == ReceptionOperationMode.Kiosk, "legacy reception mode defaults to Kiosk");
        options.OperationMode = ReceptionOperationMode.Staff; options.Save(root);
        check(AgentSettings.Load(root).OperationMode == ReceptionOperationMode.Staff, "staff reception mode persists across restart");
        bool available = true;
        var printed = new List<KioskReceipt>();
        string? failId = null;
        void Print(KioskReceipt receipt, PosPrintOptions settings)
        {
            check(store.Get(receipt.SessionId).StaffReceiptState == StaffReceiptState.Running, "automatic receipt intent is durable before contacting printer");
            printed.Add(receipt);
            if (receipt.SessionId == failId) throw new IOException("synthetic uncertain spool result");
        }
        StaffReceiptService Service(CaptureStore s) => new(s, () => options, Print, () => available, _ => false);
        var service = Service(store);
        CaptureRecord Add(CaptureStage stage = CaptureStage.Completed, bool chart = true, bool reserved = true)
        {
            var patient = new FacePatientMatch("0011", "カルテ氏名", "カルテカナ", new(2000, 1, 1), ["110"]);
            var record = new CaptureRecord { FileName = Guid.NewGuid().ToString("N") + ".xml", ContentHash = "synthetic", OperationMode = ReceptionOperationMode.Staff,
                Stage = stage, GeneratedAt = DateTime.Now, Face = new("マイナ タロウ", new(2000, 1, 1), "0110012345", PatientName: "マイナ 太郎"),
                Lookup = chart ? new([patient], patient, false, "mock") : new([], null, false, "カルテなし"),
                ChartNumberFoundAtReception = chart, ReservationFoundAtReception = reserved, ReceptionNo = reserved ? "0037" : "" };
            store.Capture(record, [1]); return record;
        }
        var first = Add();
        options.OperationMode = ReceptionOperationMode.Kiosk; options.PosPrint.Enabled = true;
        await service.RunOnceAsync(default);
        check(printed.Count == 0, "Kiosk mode never prints solely because XML lookup completed");
        options.OperationMode = ReceptionOperationMode.Staff; options.PosPrint.Enabled = false;
        await service.RunOnceAsync(default);
        check(printed.Count == 0, "disabled POS setting suppresses staff automatic printing");
        options.PosPrint.Enabled = true;
        await service.RunOnceAsync(default); await service.RunOnceAsync(default);
        check(printed.Count == 1 && printed[0].PatientId == "0011" && printed[0].ReceptionNo == "0037" && printed[0].Name == "マイナ 太郎" && printed[0].NameKana == "マイナ タロウ", "staff mode automatically prints verified numbers and XML name/kana once");
        await Service(new CaptureStore(Path.Combine(root, "captures"))).RunOnceAsync(default);
        check(printed.Count == 1 && store.Get(first.Id).StaffReceiptState == StaffReceiptState.Completed, "completed automatic receipt survives service restart without duplication");
        var initial = Add(chart: false, reserved: false);
        await service.RunOnceAsync(default);
        check(printed.Last().SessionId == initial.Id && printed.Last().PatientId == "" && printed.Last().ReceptionNo == "", "new patient without reservation prints blank chart and reservation numbers");
        var initialReserved = Add(chart: false);
        await service.RunOnceAsync(default);
        check(printed.Last().SessionId == initialReserved.Id && printed.Last().PatientId == "" && printed.Last().ReceptionNo == "0037", "new patient with reservation prints reservation number and blank chart number");
        var timedOut = Add(CaptureStage.FindWaiting, reserved: false);
        timedOut.ReservationFoundAtReception = null; timedOut.RetryAfter = DateTimeOffset.Now.AddSeconds(10); timedOut.ICallTimeoutCount = 1; store.Save(timedOut);
        await service.RunOnceAsync(default);
        check(printed.Last().SessionId == timedOut.Id && printed.Last().ReceptionNo == "", "lookup timeout prints once with an unconfirmed reservation number blank");
        timedOut = store.Get(timedOut.Id); timedOut.ReceptionNo = "42"; timedOut.ReservationFoundAtReception = true; timedOut.Stage = CaptureStage.Completed; store.Save(timedOut);
        int count = printed.Count; await service.RunOnceAsync(default);
        check(printed.Count == count, "later reservation resolution never silently reprints an earlier blank-number receipt");
        var waiting = Add(CaptureStage.FindWaiting, reserved: false);
        var kioskRow = Add(); kioskRow.OperationMode = ReceptionOperationMode.Kiosk; store.Save(kioskRow);
        var priorDay = Add(); priorDay.GeneratedAt = DateTime.Today.AddDays(-1); store.Save(priorDay);
        var invalid = Add(); invalid.Face = null; store.Save(invalid);
        var conflict = Add(); store.Capture(new CaptureRecord { FileName = conflict.FileName, ContentHash = "changed" }, [2]);
        var kioskJob = Add(); kioskJob.KioskAutomaticSessionId = "kiosk-job"; store.Save(kioskJob);
        await service.RunOnceAsync(default);
        check(printed.Count == count && store.Get(waiting.Id).StaffReceiptState == StaffReceiptState.None, "pending lookup, original Kiosk capture, prior-day XML, invalid XML, conflicts and Kiosk-owned operations are not auto-printed");
        var ambiguous = Add(CaptureStage.NeedsReview, reserved: false);
        ambiguous.Lookup = new([new("11", "A", "A", new(2000, 1, 1), []), new("22", "B", "B", new(2000, 1, 1), [])], null, false, "複数候補"); store.Save(ambiguous);
        await service.RunOnceAsync(default);
        check(printed.Last().SessionId == ambiguous.Id && printed.Last().PatientId == "" && printed.Last().ReceptionNo == "", "unresolved lookup produces an XML slip without choosing a candidate ID");
        var outage = Add(); available = false; count = printed.Count;
        await service.RunOnceAsync(default); check(printed.Count == count, "folder outage suspends staff automatic printing");
        available = true; await service.RunOnceAsync(default);
        check(printed.Last().SessionId == outage.Id, "folder recovery resumes pending staff printing");
        var failed = Add(); failId = failed.Id;
        await service.RunOnceAsync(default); count = printed.Count;
        await service.RunOnceAsync(default); await Service(store).RunOnceAsync(default);
        check(printed.Count == count && store.Get(failed.Id).StaffReceiptState == StaffReceiptState.NeedsReview, "uncertain printer exception is saved for staff review and never automatically retried");
        var interrupted = Add(); interrupted.StaffReceiptState = StaffReceiptState.Running; store.Save(interrupted);
        await Service(store).RunOnceAsync(default);
        check(printed.Count == count && store.Get(interrupted.Id).StaffReceiptState == StaffReceiptState.NeedsReview, "interrupted spool submission is not repeated after restart");
        var manual = Add(); await service.RecordManualPrintAsync(manual.Id, default); await service.RunOnceAsync(default);
        check(printed.Count == count && store.Get(manual.Id).StaffReceiptState == StaffReceiptState.Completed, "explicit manual reprint records completion and suppresses later automatic printing");
        string faceDir = Path.Combine(root, "face"); Directory.CreateDirectory(faceDir);
        var captureMode = ReceptionOperationMode.Staff;
        var monitor = new FaceCaptureMonitor(store, new AgentSettings { OperationMode = ReceptionOperationMode.Kiosk, FaceTrashDirectory = faceDir },
            (_, _) => throw new Exception("capture should not perform lookup"), operationMode: () => captureMode);
        const string xml = "<XmlMsg><MessageHeader><SegmentOfResult>1</SegmentOfResult><MedicalInstitutionCode>0110012345</MedicalInstitutionCode></MessageHeader><MessageBody><ResultList><ResultOfQualificationConfirmation><Name>マイナ 太郎</Name><NameKana>マイナ タロウ</NameKana><Birthdate>20000101</Birthdate></ResultOfQualificationConfirmation></ResultList><ProcessingResultStatus>1</ProcessingResultStatus></MessageBody></XmlMsg>";
        string staffFile = Path.Combine(faceDir, "staff.xml"), kioskFile = Path.Combine(faceDir, "kiosk.xml");
        await File.WriteAllTextAsync(staffFile, xml); await monitor.CaptureAsync(staffFile, DateTime.Now, default);
        captureMode = ReceptionOperationMode.Kiosk;
        await File.WriteAllTextAsync(kioskFile, xml); await monitor.CaptureAsync(kioskFile, DateTime.Now, default);
        check(store.List().Single(r => r.FileName == "staff.xml").OperationMode == ReceptionOperationMode.Staff &&
            store.List().Single(r => r.FileName == "kiosk.xml").OperationMode == ReceptionOperationMode.Kiosk,
            "mode changes during monitoring affect newly captured XML without rewriting earlier captures");
    }
}
