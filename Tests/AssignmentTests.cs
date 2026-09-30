using System.Text.Json;
using iCallManager.Bridge;
using iCallManager.Core;
using iCallManager.ICall;

internal static class AssignmentTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var fixture = DiagnosticFixture.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ie-flat-anonymized.txt"));
        var parsed = RowParser.Parse(fixture, new());
        var dummy = parsed[0].Reservation;
        var request = new BridgeRequest("assign-test", "assign", "00022");
        check(dummy.IsUnassignedDummy && dummy.CanAssignDummy && parsed[0].Assignment?.Name == "患者割当", "real flat fixture exposes assignable dummy button");
        check(dummy.WaitingOrder == 1 && dummy.InternalId == "90001", "dummy has verified wait order and internal reservation ID");
        check(DummyAssignment.Select([dummy, dummy with { ReceptionNo = "100", InternalId = "90000", WaitingOrder = 2 }], request) == dummy,
            "select earliest wait order rather than smallest reception number");
        check(DummyAssignment.Select([dummy, dummy with { ReceptionNo = "100", WaitingOrder = 2 }], request with { ExpectedReceptionNo = "100" }).ReceptionNo == "100",
            "optional target reception number selects specified dummy");
        void Reject(Action action, string code, string message)
        {
            try { action(); check(false, message); }
            catch (BridgeException ex) { check(ex.Code == code, message); }
        }
        Reject(() => DummyAssignment.Select([], request), "no_dummy_slot", "no dummy stops before popup operation");
        Reject(() => DummyAssignment.Select([dummy with { CanAssignDummy = false }], request), "no_dummy_slot", "disabled assignment never selected");
        Reject(() => DummyAssignment.Select([dummy with { PatientName = "氏名あり" }], request), "no_dummy_slot", "missing chart number on named patient is not a dummy");
        Reject(() => DummyAssignment.Select([dummy, dummy with { ReceptionNo = "103" }], request), "ambiguous_dummy_slot", "ambiguous earliest dummy rejected");
        Reject(() => DummyAssignment.Select([dummy with { WaitingOrder = null }], request), "dummy_order_unverified", "unknown order requires explicit target");
        Reject(() => DummyAssignment.Select([dummy], request with { PatientId = "-" }), "invalid_request", "placeholder chart number cannot be assigned");
        Reject(() => DummyAssignment.Select([dummy], request with { ExpectedReceptionNo = "999" }), "no_dummy_slot", "missing explicit target never falls back to another dummy");

        UiNode Cell(string text) => new() { Role = 29, Name = text };
        UiNode Popup(string patientId = "00022", string name = "模擬患者", string slot = "101番")
        {
            var frame = new UiNode { ControlType = 50032, Name = "順番 ダミー割当" };
            var query = new UiNode { Role = 24 };
            query.Children.Add(new() { Key = 11, ControlType = 50003, Value = "診察券番号", Enabled = true });
            query.Children.Add(new() { Key = 12, ControlType = 50004, AutomationId = "findword", Value = "00022", Enabled = true });
            query.Children.Add(new() { Key = 13, ControlType = 50000, Name = "患者検索", Enabled = true });
            var summary = new UiNode { Role = 24 };
            summary.Children.AddRange([Cell("割当予約番号"), Cell(slot), Cell("診察券"), Cell(patientId), Cell("おなまえ"), Cell(name)]);
            frame.Children.AddRange([query, summary, new() { Key = 14, ControlType = 50000, Name = "割当する", Enabled = true }]);
            var url = new UiNode { ControlType = 50032, Name = "http://example.invalid/admin?vid=fmdmytrn&rsv_sn=90001&mb_sn=1" };
            url.Children.Add(frame);
            return url;
        }
        var popup = Popup();
        var scope = DummyAssignmentParser.Scope(popup);
        DummyAssignmentParser.VerifySlot(popup, scope, dummy); DummyAssignmentParser.VerifySearchMode(scope);
        check(DummyAssignmentParser.Input(scope).AutomationId == "findword" &&
            DummyAssignmentParser.Patient(scope, "00022").PatientName == "模擬患者", "popup reads input mode selected card and name from summary");
        Reject(() => DummyAssignmentParser.Patient(DummyAssignmentParser.Scope(Popup("999")), "00022"), "patient_search_unverified", "stale search result cannot be committed");
        var wrongSlot = Popup(slot: "999番");
        Reject(() => DummyAssignmentParser.VerifySlot(wrongSlot, DummyAssignmentParser.Scope(wrongSlot), dummy), "dummy_slot_changed", "popup must match selected reception number");
        Reject(() => DummyAssignmentParser.VerifySlot(popup, scope, dummy with { InternalId = "999" }), "dummy_slot_changed", "popup URL must match internal reservation ID");
        var wrongMode = Popup();
        var modeScope = DummyAssignmentParser.Scope(wrongMode);
        modeScope.Children[0].Children[0] = new() { ControlType = 50003, Value = "氏名" };
        Reject(() => DummyAssignmentParser.VerifySearchMode(modeScope), "patient_search_mode_unverified", "name search mode cannot silently receive chart number");
        var twice = new UiNode(); twice.Children.AddRange([Popup(), Popup()]);
        Reject(() => DummyAssignmentParser.Scope(twice), "assignment_popup_not_unique", "multiple assignment popups rejected");
        var duplicateInput = Popup();
        var duplicateScope = DummyAssignmentParser.Scope(duplicateInput);
        duplicateScope.Children.Add(new() { ControlType = 50004, AutomationId = "findword", Enabled = true });
        Reject(() => DummyAssignmentParser.Input(duplicateScope), "assignment_input_not_unique", "ambiguous edit input rejected");
        var doubleResult = Popup();
        DummyAssignmentParser.Scope(doubleResult).Children[1].Children.AddRange([Cell("診察券"), Cell("00022")]);
        Reject(() => DummyAssignmentParser.Patient(DummyAssignmentParser.Scope(doubleResult), "00022"), "patient_search_unverified", "duplicate patient summary fields rejected");

        var assigned = new Reservation(dummy.ReceptionNo, "00022", "模擬患者", dummy.InternalId, true, true);
        var session = new FakeSession { Patient = new("00022", "模擬患者"), Result = assigned };
        check(DummyAssignment.Run(session, dummy, "00022", () => true) == assigned && session.Commits == 1, "workflow commits matching card and confirms assigned slot");
        var wrongSession = new FakeSession { Patient = new("999", "別患者"), Result = assigned };
        Reject(() => DummyAssignment.Run(wrongSession, dummy, "00022", () => true), "patient_search_unverified", "wrong search patient stops before commit");
        check(wrongSession.Commits == 0, "mismatching card never commits");
        var uncertainSession = new FakeSession { Patient = session.Patient, Result = assigned with { ReceptionNo = "999" } };
        Reject(() => DummyAssignment.Run(uncertainSession, dummy, "00022", () => true), "outcome_unknown", "post-commit wrong slot is uncertain");
        var throwingSession = new FakeSession { Patient = session.Patient, Result = assigned, ThrowOnCommit = true };
        Reject(() => DummyAssignment.Run(throwingSession, dummy, "00022", () => true), "outcome_unknown", "commit dispatch failure must not be retried");
        var disabled = new FakeSession { Patient = session.Patient, Result = assigned };
        Reject(() => DummyAssignment.Run(disabled, dummy, "00022", () => false), "operations_disabled", "disabled workflow has no side effects");
        check(disabled.Opens == 0 && disabled.Commits == 0, "disabled assignment never opens popup");

        var adapter = new AssignAdapter { Rows = [dummy], Assigned = assigned };
        using var service = new ReservationService(() => adapter);
        check((await service.ExecuteAsync(request)).Code == "operations_disabled" && adapter.Assignments == 0, "assign API starts read-only");
        service.OperationsEnabled = true;
        check((await service.ExecuteAsync(request with { PatientId = "ABC" })).Code == "invalid_request", "API rejects non-numeric chart number");
        var success = await service.ExecuteAsync(request);
        check(success.Success && success.Code == "assigned" && success.Reservation?.ReceptionNo == dummy.ReceptionNo && service.Snapshot.IsCurrent,
            "assign API returns success only after independent list verification");
        check((await service.ExecuteAsync(request with { RequestId = "another" })).Code == "already_reserved" && adapter.Assignments == 1,
            "new request ID for already-reserved patient cannot allocate a second dummy");
        adapter.Rows = [];
        check((await service.ExecuteAsync(request)).Code == "no_dummy_slot", "assign API reports exhausted dummy slots");
        adapter.Rows = [dummy]; adapter.UpdateRows = false;
        check((await service.ExecuteAsync(request)).Code == "outcome_unknown" && !service.Snapshot.IsCurrent,
            "unverified list never returns assigned success");

        adapter.UpdateRows = true; adapter.Rows = [dummy];
        string root = Path.Combine(Path.GetTempPath(), "icall-assign-" + Guid.NewGuid().ToString("N"));
        using var bridge = new FileBridge(Path.Combine(root, "bridge"), Path.Combine(root, "state"), service.ExecuteAsync, _ => { });
        var bridgeRequest = request with { RequestId = "bridge-assign" };
        string reqPath = Path.Combine(root, "bridge", "request", bridgeRequest.RequestId + ".json");
        string resPath = Path.Combine(root, "bridge", "response", bridgeRequest.RequestId + ".json");
        File.WriteAllText(reqPath, JsonSerializer.Serialize(bridgeRequest, AppSettings.Json)); await bridge.PollAsync();
        var response = JsonSerializer.Deserialize<BridgeResponse>(File.ReadAllText(resPath), AppSettings.Json)!;
        check(response.Success && response.Code == "assigned" && response.ReceptionNo == dummy.ReceptionNo &&
            response.PatientId == "00022" && response.RequestFingerprint == bridgeRequest.Fingerprint(), "file API returns assigned reception number and request identity");
        int before = adapter.Assignments;
        File.WriteAllText(reqPath, JsonSerializer.Serialize(bridgeRequest, AppSettings.Json)); await bridge.PollAsync();
        check(adapter.Assignments == before, "same assign request replays saved response without another allocation");
    }
    private sealed class FakeSession : IDummyAssignmentSession
    {
        public AssignmentPatient Patient { get; set; } = new("", "");
        public Reservation Result { get; set; } = new("", "", "", "", false, false);
        public int Opens { get; private set; }
        public int Commits { get; private set; }
        public bool ThrowOnCommit { get; set; }
        public void Open(Reservation expected, Func<bool> mayOperate) => Opens++;
        public AssignmentPatient Search(string patientId, Func<bool> mayOperate) => Patient;
        public void Commit(AssignmentPatient patient, Func<bool> mayOperate) { Commits++; if (ThrowOnCommit) throw new InvalidOperationException(); }
        public Reservation AwaitAssigned(Reservation expected, string patientId) => Result;
    }
    private sealed class AssignAdapter : IReservationAdapter
    {
        public IReadOnlyList<Reservation> Rows { get; set; } = [];
        public Reservation Assigned { get; set; } = new("", "", "", "", false, false);
        public int Assignments { get; private set; }
        public bool UpdateRows { get; set; } = true;
        public IReadOnlyList<Reservation> Read() => Rows;
        public void Invoke(Reservation expected, string action, Func<bool> mayInvoke) => throw new NotSupportedException();
        public Reservation AssignDummy(Reservation expected, string patientId, Func<bool> mayOperate)
        { Assignments++; if (UpdateRows) Rows = [Assigned]; return Assigned; }
        public string Diagnose() => "synthetic";
        public void Dispose() { }
    }
}
