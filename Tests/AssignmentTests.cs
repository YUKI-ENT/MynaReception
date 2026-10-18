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
            "select top displayed dummy rather than smallest reception number");
        var lower = dummy with { ReceptionNo = "100", InternalId = "90000", WaitingOrder = 2 };
        check(DummyAssignment.Select([lower, dummy], request) == lower,
            "display order takes priority over numeric waiting order");
        check(DummyAssignment.Select([dummy with { CanAssignDummy = false }, lower, dummy], request) == lower,
            "skip unavailable top row and select first operable dummy");
        check(DummyAssignment.Select([dummy with { PatientName = "氏名あり" }, lower], request) == lower,
            "named unassigned patient is skipped before selecting a dummy");
        check(DummyAssignment.Select([dummy, lower with { WaitingOrder = dummy.WaitingOrder }], request) == dummy,
            "equal numeric waiting orders still select top displayed dummy");
        var noOrder = dummy with { WaitingOrder = null };
        check(DummyAssignment.Select([noOrder, lower], request) == noOrder,
            "top displayed dummy does not require numeric waiting order");
        check(DummyAssignment.Select([dummy, dummy with { ReceptionNo = "100", WaitingOrder = 2 }], request with { ExpectedReceptionNo = "100" }).ReceptionNo == "100",
            "optional target reception number selects specified dummy");
        void Reject(Action action, string code, string message)
        {
            try { action(); check(false, message); }
            catch (BridgeException ex) { check(ex.Code == code, message); }
        }
        var mainWindow = new ICallWindow("[ テスト医院 ] - 管理画面 - Internet Explorer", "IEFrame", 1528);
        var inheritedTitleDialog = mainWindow with { ClassName = "Internet Explorer_TridentDlgFrame" };
        var titledDialog = new ICallWindow("順番待ち予約ダミー割当 - 管理画面", "OtherClass", 1528);
        var closedModalHost = new ICallWindow("[ テスト医院 ] - 管理画面", "Alternate Modal Top Most", 1528);
        var edgeList = new ICallWindow("[ テスト医院 ] - 管理画面", "Chrome_WidgetWin_1", 7984)
            { ContainsReservationList = true };
        check(ICallWindowSelector.MainWindowIndex([closedModalHost, edgeList], "- 管理画面") == 1,
            "actual post-assignment empty Alternate Modal Top Most host does not count as a second management page");
        var describedClosedHost = ICallWindowSelector.DescribeContent(closedModalHost, new UiNode());
        check(!describedClosedHost.ContainsAssignmentDialog && !describedClosedHost.ContainsReservationList &&
            ICallWindowSelector.MainWindowIndex([describedClosedHost, edgeList], "- 管理画面") == 1,
            "closed host stays identifiable even after assignment title and content disappear");
        check(ICallWindowSelector.IncludeDialog(closedModalHost, new HashSet<int> { 7984, 1528 }),
            "known IE host remains scoped to captured management processes");
        check(!ICallWindowSelector.IncludeDialog(closedModalHost, new HashSet<int> { 9999 }),
            "empty modal host in another process is never attached");
        Reject(() => ICallWindowSelector.MainWindowIndex([closedModalHost, edgeList, edgeList], "- 管理画面"),
            "window_not_unique", "closed host exclusion still rejects two real management lists");
        Reject(() => ICallWindowSelector.MainWindowIndex([closedModalHost with { ContainsReservationList = true }, edgeList], "- 管理画面"),
            "window_not_unique", "Alternate Modal host containing an actual reservation list is never silently discarded");
        Reject(() => ICallWindowSelector.MainWindowIndex([closedModalHost with { ClassName = "UnknownClass" }, edgeList], "- 管理画面"),
            "window_not_unique", "unknown title-matching window is not ignored solely because its list is empty");
        check(ICallWindowSelector.MainWindowIndex([inheritedTitleDialog, mainWindow, titledDialog], "- 管理画面") == 1,
            "IE dialogs sharing management title are excluded from main window count");
        check(ICallWindowSelector.IncludeDialog(inheritedTitleDialog, new HashSet<int> { 1528 }) &&
            ICallWindowSelector.IncludeDialog(titledDialog, new HashSet<int> { 1528 }),
            "same-process dialogs are captured even when their title matches management page");
        check(!ICallWindowSelector.IncludeDialog(titledDialog with { ProcessId = 9999 }, new HashSet<int> { 1528 }),
            "dialogs in another process are not attached");
        Reject(() => ICallWindowSelector.MainWindowIndex([mainWindow, mainWindow, inheritedTitleDialog], "- 管理画面"),
            "window_not_unique", "two real management windows remain ambiguous");
        Reject(() => ICallWindowSelector.MainWindowIndex([inheritedTitleDialog], "- 管理画面"),
            "window_not_unique", "dialog alone is never selected as management page");
        Reject(() => DummyAssignment.Select([], request), "no_dummy_slot", "no dummy stops before popup operation");
        Reject(() => DummyAssignment.Select([dummy with { CanAssignDummy = false }], request), "no_dummy_slot", "disabled assignment never selected");
        Reject(() => DummyAssignment.Select([dummy with { PatientName = "氏名あり" }], request), "no_dummy_slot", "missing chart number on named patient is not a dummy");
        Reject(() => DummyAssignment.Select([dummy, dummy], request with { ExpectedReceptionNo = dummy.ReceptionNo }),
            "ambiguous_dummy_slot", "duplicate explicit reception number is rejected");
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
        var wrappedPopup = ICallWindowSelector.DescribeContent(mainWindow with { ClassName = "" }, popup);
        var mainWithList = ICallWindowSelector.DescribeContent(mainWindow, fixture);
        check(ICallWindowSelector.MainWindowIndex([wrappedPopup, mainWithList], "- 管理画面") == 1,
            "generic IE wrapper sharing management title is recognized by assignment contents");
        check(ICallWindowSelector.IncludeDialog(wrappedPopup, new HashSet<int> { 1528 }),
            "generic popup wrapper is attached for chart number input");
        var mainWithOwnedPopup = new UiNode();
        mainWithOwnedPopup.Children.AddRange([fixture, popup]);
        var mainWithBoth = ICallWindowSelector.DescribeContent(mainWindow, mainWithOwnedPopup);
        check(mainWithBoth.ContainsReservationList && mainWithBoth.ContainsAssignmentDialog &&
            ICallWindowSelector.MainWindowIndex([wrappedPopup, mainWithBoth], "- 管理画面") == 1,
            "management page remains main when owned popup is already in its subtree");
        var rowHeader = new UiNode { Role = 28 };
        rowHeader.Children.AddRange([Cell("受付番号"), Cell("診察券番号"), Cell("患者名")]);
        var genericList = new UiNode { Role = 24 };
        genericList.Children.Add(rowHeader);
        var genericMain = new UiNode(); genericMain.Children.AddRange([genericList, popup]);
        check(ICallWindowSelector.MainWindowIndex([wrappedPopup,
            ICallWindowSelector.DescribeContent(mainWindow, genericMain)], "- 管理画面") == 1,
            "verified generic reservation table with popup is still a management page");
        Reject(() => ICallWindowSelector.MainWindowIndex([mainWithList, mainWithList, wrappedPopup], "- 管理画面"),
            "window_not_unique", "content detection does not select arbitrarily between two real lists");
        try { ICallWindowSelector.MainWindowIndex([mainWindow, mainWindow], "- 管理画面"); }
        catch (BridgeException ex)
        {
            check(ex.Message.Contains("[window-detect-v3]") && ex.Message.Contains("class=IEFrame") &&
                ex.Message.Contains("pid=1528"), "window ambiguity response includes detector revision and candidate details");
        }
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

        // Replay the actual IE pane/cell hierarchy supplied after the timeout.
        var emptyPopup = DiagnosticFixture.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ie-dummy-popup-empty-anonymized.txt"));
        var emptyScope = DummyAssignmentParser.Scope(emptyPopup);
        check(emptyScope.Key == 8 && emptyScope.ControlType == 50033 && DummyAssignmentParser.HasPopup(emptyScope),
            "actual IE popup resolves innermost Pane rather than outer window");
        DummyAssignmentParser.VerifySlot(emptyPopup, emptyScope, dummy);
        DummyAssignmentParser.VerifySearchMode(emptyScope);
        check(DummyAssignmentParser.Input(emptyScope).Value == "" &&
            DummyAssignmentParser.Button(emptyScope, "患者検索").Key == 25,
            "actual nested query table exposes empty input and correct search button");
        Reject(() => DummyAssignmentParser.Patient(emptyScope, "00022"), "patient_search_empty",
            "actual empty result is not mistaken for unknown labels or a verified patient");
        emptyScope.Descendants().Single(n => n.Key == 35).Children[0] =
            new() { Key = 36, ControlType = 50020, Name = "00022" };
        emptyScope.Descendants().Single(n => n.Key == 39).Children[0] =
            new() { Key = 40, ControlType = 50020, Name = "模擬患者" };
        check(DummyAssignmentParser.Input(emptyScope).Value == "" &&
            DummyAssignmentParser.Patient(emptyScope, "00022") == new AssignmentPatient("00022", "模擬患者"),
            "populated result verifies patient even if search navigation clears query input");
        Reject(() => DummyAssignmentParser.Patient(emptyScope, "999"), "patient_search_unverified",
            "actual result hierarchy still rejects another chart number");
        emptyScope.Descendants().Single(n => n.Key == 21).Children[1] =
            new() { Key = 23, ControlType = 50004, AutomationId = "findword", Value = "999", Enabled = true };
        Reject(() => DummyAssignmentParser.Patient(emptyScope, "00022"), "patient_input_mismatch",
            "a changed nonempty query cannot commit the previous search result");

        var assigned = new Reservation(dummy.ReceptionNo, "00022", "模擬患者", dummy.InternalId, true, true);
        var session = new FakeSession { Patient = new("00022", "模擬患者"), Result = assigned };
        check(DummyAssignment.Run(session, dummy, "00022", () => true) == assigned && session.Commits == 1, "workflow commits matching card and confirms assigned slot");
        var wrongSession = new FakeSession { Patient = new("999", "別患者"), Result = assigned };
        Reject(() => DummyAssignment.Run(wrongSession, dummy, "00022", () => true), "patient_search_unverified", "wrong search patient stops before commit");
        check(wrongSession.Commits == 0, "mismatching card never commits");
        var uncertainSession = new FakeSession { Patient = session.Patient, Result = assigned with { ReceptionNo = "999" } };
        Reject(() => DummyAssignment.Run(uncertainSession, dummy, "00022", () => true), "outcome_unknown", "post-commit wrong slot is uncertain");
        var throwingSession = new FakeSession { Patient = session.Patient, Result = assigned, ThrowOnCommit = true };
        check(DummyAssignment.Run(throwingSession, dummy, "00022", () => true) == assigned && throwingSession.Commits == 1,
            "post-dispatch exception recovers from verified list without committing twice");
        var disconnectedSession = new FakeSession { Patient = session.Patient, Result = assigned,
            CommitFailure = new System.Runtime.InteropServices.COMException("dialog closed", unchecked((int)0x80010108)) };
        check(DummyAssignment.Run(disconnectedSession, dummy, "00022", () => true) == assigned && disconnectedSession.Commits == 1,
            "actual disconnected COM dispatch failure still confirms successful assignment");
        disconnectedSession.Result = assigned with { InternalId = "different-slot" };
        Reject(() => DummyAssignment.Run(disconnectedSession, dummy, "00022", () => true), "outcome_unknown",
            "dispatch exception cannot recover from another internal reservation ID");
        var disabled = new FakeSession { Patient = session.Patient, Result = assigned };
        Reject(() => DummyAssignment.Run(disabled, dummy, "00022", () => false), "operations_disabled", "disabled workflow has no side effects");
        check(disabled.Opens == 0 && disabled.Commits == 0, "disabled assignment never opens popup");
        var preflightFailure = new FakeSession { Patient = session.Patient, Result = assigned, CommitWasAttempted = false,
            CommitFailure = new BridgeException("assignment_button_unavailable", "HTML button not found") };
        Reject(() => DummyAssignment.Run(preflightFailure, dummy, "00022", () => true), "assignment_button_unavailable",
            "proven pre-click failure preserves its original code instead of outcome_unknown");
        var dispatchedFailure = new FakeSession { Patient = session.Patient, Result = assigned,
            CommitFailure = new BridgeException("assignment_dom_failed", "[phase=commit] HRESULT=0x80004005"),
            AwaitFailure = new BridgeException("assignment_timeout", "confirmation unavailable") };
        try { DummyAssignment.Run(dispatchedFailure, dummy, "00022", () => true); check(false, "post-dispatch error preserves diagnostic cause"); }
        catch (BridgeException ex)
        { check(ex.Code == "outcome_unknown" && ex.Message.Contains("assignment_dom_failed") && ex.Message.Contains("0x80004005"),
            "post-dispatch error preserves diagnostic cause"); }
        var resultFailure = new FakeSession { Patient = session.Patient, Result = assigned,
            AwaitFailure = new BridgeException("assignment_timeout", "[phase=assigned_list] assignment-last-failure.txt") };
        try { DummyAssignment.Run(resultFailure, dummy, "00022", () => true); check(false, "post-click timeout preserves result stage and diagnostic filename"); }
        catch (BridgeException ex)
        { check(ex.Code == "outcome_unknown" && ex.Message.Contains("phase=assigned_list") && ex.Message.Contains("assignment-last-failure.txt"),
            "post-click timeout preserves result stage and diagnostic filename"); }

        int readAttempts = 0, delays = 0;
        var recoveredRead = AssignmentConfirmation.Read(() =>
        {
            readAttempts++;
            if (readAttempts == 1) throw new System.Runtime.InteropServices.COMException("closing");
            if (readAttempts == 2) throw new System.Runtime.InteropServices.InvalidComObjectException();
            return assigned;
        }, "assigned_list", () => delays++);
        check(recoveredRead == assigned && readAttempts == 3 && delays == 2,
            "COM and disconnected wrapper reads acquire fresh data before confirmation");
        readAttempts = 0;
        try
        {
            AssignmentConfirmation.Read<Reservation>(() => { readAttempts++; throw new System.Runtime.InteropServices.COMException("persistent"); },
                "assigned_list", () => { });
            check(false, "persistent COM confirmation failure is bounded");
        }
        catch (System.Runtime.InteropServices.COMException)
        { check(readAttempts == 5, "persistent COM confirmation failure stops after five read attempts"); }
        readAttempts = 0;
        Reject(() => AssignmentConfirmation.Read<Reservation>(() =>
        { readAttempts++; throw new BridgeException("window_not_unique", "two real management windows"); }, "assigned_list", () => { }),
            "window_not_unique", "ambiguous window is not treated as a transient COM read");
        check(readAttempts == 1, "confirmation retries are restricted to COM lifetime errors");

        var adapter = new AssignAdapter { Rows = [dummy], Assigned = assigned };
        var closedHostAdapter = new AssignAdapter { Rows = [dummy], Assigned = assigned };
        closedHostAdapter.BeforeRead = () => ICallWindowSelector.MainWindowIndex(
            closedHostAdapter.Assignments == 0 ? [edgeList] : [closedModalHost, edgeList], "- 管理画面");
        using (var closedHostService = new ReservationService(() => closedHostAdapter) { OperationsEnabled = true })
        {
            var closedHostResult = await closedHostService.ExecuteAsync(request);
            check(closedHostResult.Success && closedHostResult.Code == "assigned" &&
                closedHostResult.Reservation == assigned && closedHostAdapter.Assignments == 1 && closedHostService.Snapshot.IsCurrent,
                "assign response verifies success after empty IE modal host remains, without another allocation");
        }
        var transientAdapter = new AssignAdapter { Rows = [dummy], Assigned = assigned };
        int confirmationReads = 0;
        transientAdapter.BeforeRead = () =>
        {
            if (transientAdapter.Assignments > 0 && ++confirmationReads == 1)
                throw new System.Runtime.InteropServices.COMException("list refreshing");
        };
        using (var transientService = new ReservationService(() => transientAdapter) { OperationsEnabled = true })
        {
            var transientResult = await transientService.ExecuteAsync(request);
            check(transientResult.Success && transientResult.Reservation == assigned && confirmationReads == 2 &&
                transientAdapter.Assignments == 1 && transientService.Snapshot.IsCurrent,
                "independent response verification recovers a COM read and publishes a fresh snapshot without reallocating");
        }
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
        public bool CommitWasAttempted { get; set; } = true;
        public bool CommitAttempted => CommitWasAttempted;
        public Exception? CommitFailure { get; set; }
        public BridgeException? AwaitFailure { get; set; }
        public void Open(Reservation expected, Func<bool> mayOperate) => Opens++;
        public AssignmentPatient Search(string patientId, Func<bool> mayOperate) => Patient;
        public void Commit(AssignmentPatient patient, Func<bool> mayOperate)
        { Commits++; if (CommitFailure is not null) throw CommitFailure; if (ThrowOnCommit) throw new InvalidOperationException(); }
        public Reservation AwaitAssigned(Reservation expected, string patientId)
        { if (AwaitFailure is not null) throw AwaitFailure; return Result; }
    }
    private sealed class AssignAdapter : IReservationAdapter
    {
        public IReadOnlyList<Reservation> Rows { get; set; } = [];
        public Reservation Assigned { get; set; } = new("", "", "", "", false, false);
        public int Assignments { get; private set; }
        public bool UpdateRows { get; set; } = true;
        public Action? BeforeRead { get; set; }
        public IReadOnlyList<Reservation> Read() { BeforeRead?.Invoke(); return Rows; }
        public void Invoke(Reservation expected, string action, Func<bool> mayInvoke) => throw new NotSupportedException();
        public Reservation AssignDummy(Reservation expected, string patientId, Func<bool> mayOperate)
        { Assignments++; if (UpdateRows) Rows = [Assigned]; return Assigned; }
        public string Diagnose() => "synthetic";
        public void Dispose() { }
    }
}
