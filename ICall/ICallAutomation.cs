using System.Runtime.InteropServices;
using System.Text;
using iCallManager.Core;
using UIAutomationClient;

namespace iCallManager.ICall;

public sealed class ICallAutomation(AppSettings settings) : IReservationAdapter
{
    private readonly AppSettings settings = settings;
    private readonly CUIAutomation automation = new();

    private sealed class Capture : IDisposable
    {
        public List<object> ComObjects { get; } = [];
        public Dictionary<int, IUIAutomationElement> Elements { get; } = [];
        public HashSet<int> ProcessIds { get; } = [];
        public UiNode Root { get; set; } = null!;
        public T Own<T>(T obj) where T : class { ComObjects.Add(obj); return obj; }
        public void Dispose()
        {
            foreach (var obj in Enumerable.Reverse(ComObjects))
                if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    private Capture ReadTree()
    {
        var capture = new Capture();
        try
        {
            var root = capture.Own(automation.GetRootElement());
            var condition = capture.Own(automation.CreateTrueCondition());
            var windows = capture.Own(root.FindAll(TreeScope.TreeScope_Children, condition));
            var windowElements = new List<IUIAutomationElement>();
            var windowInfo = new List<ICallWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                var window = capture.Own(windows.GetElement(i));
                windowElements.Add(window);
                windowInfo.Add(new(window.CurrentName ?? "", window.CurrentClassName ?? "", window.CurrentProcessId));
            }
            var walker = capture.Own(automation.ControlViewWalker);
            int count = 0;
            UiNode Visit(IUIAutomationElement element, int depth)
            {
                if (++count > 20000 || depth > 60)
                    throw new BridgeException("tree_limit", "画面構造が大きすぎます。対象画面を確認してください。");
                int role = 0; string value = "";
                if (element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern legacy)
                {
                    capture.Own(legacy); role = (int)legacy.CurrentRole;
                    if (element.CurrentControlType is 50004 or 50003) value = legacy.CurrentValue ?? "";
                }
                if (element.CurrentControlType is 50004 or 50003 &&
                    element.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern valuePattern)
                { capture.Own(valuePattern); value = valuePattern.CurrentValue ?? ""; }
                if (element.CurrentControlType == 50003 &&
                    element.GetCurrentPattern(UIA_PatternIds.UIA_SelectionPatternId) is IUIAutomationSelectionPattern selection)
                {
                    capture.Own(selection);
                    var selected = capture.Own(selection.GetCurrentSelection());
                    if (selected.Length == 1) value = capture.Own(selected.GetElement(0)).CurrentName ?? value;
                }
                var node = new UiNode
                {
                    Key = count, ProcessId = element.CurrentProcessId, Name = (element.CurrentName ?? "").Trim(),
                    AutomationId = element.CurrentAutomationId ?? "", HelpText = (element.CurrentHelpText ?? "").Trim(),
                    Framework = element.CurrentFrameworkId ?? "", ControlType = element.CurrentControlType,
                    Value = value, Role = role, Enabled = element.CurrentIsEnabled != 0
                };
                capture.Elements.Add(node.Key, element);
                var child = walker.GetFirstChildElement(element);
                while (child is not null)
                {
                    capture.Own(child);
                    node.Children.Add(Visit(child, depth + 1));
                    child = walker.GetNextSiblingElement(child);
                }
                return node;
            }
            // Some IE hosts expose a second generic top-level window with the
            // management title and place the actual Trident dialog beneath it.
            // Inspect relevant window contents before counting management pages.
            var trees = new Dictionary<int, UiNode>();
            for (int i = 0; i < windowInfo.Count; i++)
            {
                if (!windowInfo[i].Name.Contains(settings.WindowTitleContains, StringComparison.Ordinal) &&
                    !ICallWindowSelector.IsDialog(windowInfo[i])) continue;
                var tree = Visit(windowElements[i], 0);
                trees.Add(i, tree);
                windowInfo[i] = ICallWindowSelector.DescribeContent(windowInfo[i], tree, settings);
            }
            int mainIndex = ICallWindowSelector.MainWindowIndex(windowInfo, settings.WindowTitleContains);
            capture.Root = trees[mainIndex];
            capture.ProcessIds.UnionWith(new[] { capture.Root }.Concat(capture.Root.Descendants()).Select(n => n.ProcessId));
            // IE dialogs can be owned top-level windows rather than descendants of the main page.
            for (int i = 0; i < windows.Length; i++)
            {
                var window = windowElements[i];
                if (i != mainIndex && ICallWindowSelector.IncludeDialog(windowInfo[i], capture.ProcessIds) &&
                    !new[] { capture.Root }.Concat(capture.Root.Descendants()).Any(n =>
                        automation.CompareElements(capture.Elements[n.Key], window) != 0))
                    capture.Root.Children.Add(trees.TryGetValue(i, out var tree) ? tree : Visit(window, 0));
            }
            if (!capture.Root.Descendants().Any(n => n.Framework == settings.FrameworkId))
                throw new BridgeException("framework_not_found", "対象画面にIEモードの要素が見つかりません。");
            return capture;
        }
        catch (Exception ex)
        {
            OperationalLog.Write("uia_tree_failed", new { error = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}", code = (ex as BridgeException)?.Code, nodes = capture.Elements.Count });
            capture.Dispose(); throw;
        }
    }

    public IReadOnlyList<Reservation> Read()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var capture = ReadTree();
        double treeMs = watch.Elapsed.TotalMilliseconds;
        try
        {
            var rows = RowParser.Parse(capture.Root, settings).Select(r => r.Reservation).ToArray();
            OperationalLog.Write("uia_read", new { treeMs, totalMs = watch.Elapsed.TotalMilliseconds, nodes = capture.Elements.Count, rows = rows.Length });
            return rows;
        }
        catch (BridgeException ex)
        {
            // Preserve the exact tree that failed, rather than reading a potentially different page again.
            int changed = 0, unavailable = 0;
            foreach (var node in capture.Root.Descendants().Where(n => n.ControlType == 50020 || n.Role == 29).Take(100))
            {
                try { if ((capture.Elements[node.Key].CurrentName ?? "").Trim() != node.Name) changed++; }
                catch (COMException) { unavailable++; }
            }
            string? diagnostic = OperationalLog.SaveFailureTree($"at={DateTimeOffset.Now:O} code={ex.Code} treeMs={treeMs:F1} changedSample={changed} unavailableSample={unavailable}\r\n" + FormatTree(capture.Root));
            OperationalLog.Write("uia_parse_failed", new { ex.Code, treeMs, totalMs = watch.Elapsed.TotalMilliseconds, nodes = capture.Elements.Count,
                settings.ReceptionNoColumn, settings.PatientIdColumn, settings.PatientNameColumn,
                changedSample = changed, unavailableSample = unavailable, diagnostic });
            throw;
        }
    }

    public void Invoke(Reservation expected, string action, Func<bool> mayInvoke)
    {
        if (action is not ("arrived" or "link")) throw new BridgeException("unsupported_action", "操作対象外です。");
        if (action == "link" && expected.IsUnassignedDummy) throw new BridgeException("action_unavailable", "患者未割当のダミー枠は連携できません。");
        using var capture = ReadTree();
        var parsed = RowParser.Parse(capture.Root, settings);
        var current = ReceptionTarget.Verify(parsed.Select(r => r.Reservation).ToArray(), expected);
        var row = parsed.Single(r => r.Reservation == current);
        if (action == "link" && row.Reservation.IsUnassignedDummy)
            throw new BridgeException("action_unavailable", "患者未割当のダミー枠は連携できません。");
        var buttonNode = action == "arrived" ? row.Arrival : row.Link;
        if (action == "link" && buttonNode?.Name == "〆") return;
        if (action == "link" && buttonNode?.Name != "")
            throw new BridgeException("link_state_unverified", "連携ボタンの状態を確認できません。");
        if (buttonNode is null || !buttonNode.Enabled)
            throw new BridgeException("action_unavailable", "対象ボタンが操作できません。");
        var button = capture.Elements[buttonNode.Key];
        // Verify the live identity cells again. UIA trees are not atomic snapshots.
        var liveRow = capture.Elements[row.Container.Key];
        if (liveRow.CurrentIsEnabled == 0 || button.CurrentIsEnabled == 0 ||
            (button.CurrentAutomationId ?? "") != buttonNode.AutomationId ||
            (button.CurrentName ?? "").Trim() != buttonNode.Name ||
            (button.CurrentHelpText ?? "").Trim() != buttonNode.HelpText)
            throw new BridgeException("identity_changed", "ボタンの状態が変わりました。再確認してください。");
        foreach (var node in row.Cells.SelectMany(c => new[] { c }.Concat(c.Descendants()))
            .Where(n => n.ControlType == 50020 || n.Role == 29))
            if ((capture.Elements[node.Key].CurrentName ?? "").Trim() != node.Name)
                throw new BridgeException("identity_changed", "患者行の内容が変わりました。再確認してください。");
        if (action == "link")
        {
            bool attempted = false;
            try { IEAssignmentDom.ExecuteLink(HtmlServer(capture, buttonNode.Key), expected, mayInvoke, () => attempted = true); }
            catch (Exception ex) when (attempted)
            {
                string cause = ex is BridgeException bridge ? $"{bridge.Code}: {bridge.Message}" : $"{ex.GetType().Name} HRESULT=0x{ex.HResult:X8}";
                OperationalLog.Write("link_dom_dispatch_failed", new { expected.ReceptionNo, expected.InternalId, cause });
                throw new BridgeException("outcome_unknown", "連携イベント送信後にエラーが発生しました。再押下せず確認してください。 " + cause);
            }
            return;
        }
        if (button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
            throw new BridgeException("invoke_unavailable", "InvokePatternがありません。");
        capture.Own(invoke);
        if (!mayInvoke()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
        invoke.Invoke();
    }

    private static void VerifyCells(Capture capture, ParsedRow row)
    {
        foreach (var node in row.Cells.SelectMany(c => new[] { c }.Concat(c.Descendants()))
            .Where(n => n.ControlType == 50020 || n.Role == 29))
            if ((capture.Elements[node.Key].CurrentName ?? "").Trim() != node.Name)
                throw new BridgeException("dummy_slot_changed", "操作直前に対象行の内容が変わりました。");
    }

    private static void InvokeButton(Capture capture, UiNode node, Func<bool> mayOperate)
    {
        var element = capture.Elements[node.Key];
        if (element.CurrentIsEnabled == 0 || (element.CurrentName ?? "").Trim() != node.Name)
            throw new BridgeException("assignment_button_unavailable", "操作直前にボタンの状態が変わりました。");
        if (element.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
            throw new BridgeException("invoke_unavailable", "ボタンのInvokePatternがありません。");
        capture.Own(invoke);
        if (!mayOperate()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
        OperationalLog.Write("assignment_button_dispatch", new { button = node.Name, method = "InvokePattern.Invoke" });
        invoke.Invoke();
    }

    public Reservation AssignDummy(Reservation expected, string patientId, Func<bool> mayOperate) =>
        DummyAssignment.Run(new AssignmentSession(this), expected, patientId, mayOperate);

    private nint AssignmentHtmlServer(Capture capture, UiNode scope)
        => HtmlServer(capture, DummyAssignmentParser.Input(scope).Key);

    private nint HtmlServer(Capture capture, int elementKey)
    {
        // Find the native HTML server through a verified input/button only.
        // Never select a server from an arbitrary browser window.
        var element = capture.Elements[elementKey];
        var walker = capture.Own(automation.RawViewWalker);
        for (int depth = 0; element is not null && depth < 60; depth++)
        {
            nint hwnd = element.CurrentNativeWindowHandle;
            if (hwnd != 0 && IEAssignmentDom.NativeClassName(hwnd) == "Internet Explorer_Server")
                return hwnd;
            element = walker.GetParentElement(element);
            if (element is not null) capture.Own(element);
        }
        throw new BridgeException("assignment_dom_unavailable", "割当入力欄のHTMLサーバーを取得できません。[dom-v1]");
    }

    private sealed class AssignmentSession(ICallAutomation owner) : IDummyAssignmentSession
    {
        public bool CommitAttempted { get; private set; }
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private Reservation expected = null!;
        private T Until<T>(string phase, Func<Capture, T> read, bool retryComReads = false)
        {
            BridgeException? last = null;
            UiNode? lastTree = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                try
                {
                    T ReadCapture()
                    {
                        using var capture = owner.ReadTree();
                        lastTree = capture.Root;
                        return read(capture);
                    }
                    return retryComReads ? AssignmentConfirmation.Read(ReadCapture, phase) : ReadCapture();
                }
                catch (BridgeException ex) when (ex.Code is "assignment_popup_not_unique" or "assignment_input_not_unique" or
                    "assignment_button_unavailable" or "patient_search_unverified" or "patient_search_empty" or
                    "patient_input_pending" or "row_layout_unverified" or "assignment_dom_loading")
                { last = ex; }
                Thread.Sleep(200);
            }
            string? diagnostic = lastTree is null ? null : OperationalLog.SaveAssignmentFailureTree(
                $"at={DateTimeOffset.Now:O} phase={phase} code={last?.Code}\r\n" + FormatTree(lastTree));
            OperationalLog.Write("assignment_wait_timeout", new { phase, code = last?.Code, diagnostic });
            string diagnosticInfo = diagnostic is null ? "" : $" 診断ファイル: {Path.GetFileName(diagnostic)}";
            throw new BridgeException("assignment_timeout", $"患者割当の画面更新待ちがタイムアウトしました。[phase={phase}] " + last?.Message + diagnosticInfo);
        }
        public void Open(Reservation target, Func<bool> mayOperate)
        {
            expected = target;
            using var capture = owner.ReadTree();
            if (DummyAssignmentParser.HasPopup(capture.Root))
                throw new BridgeException("assignment_popup_busy", "既にダミー割当画面が開いています。職員が閉じてから再要求してください。");
            var matches = RowParser.Parse(capture.Root, owner.settings).Where(r =>
                r.Reservation.ReceptionNo == target.ReceptionNo && r.Reservation.InternalId == target.InternalId).ToArray();
            if (matches.Length != 1 || !matches[0].Reservation.IsUnassignedDummy || !matches[0].Reservation.CanAssignDummy ||
                matches[0].Reservation.WaitingOrder != target.WaitingOrder || matches[0].Assignment is null)
                throw new BridgeException("dummy_slot_changed", "対象ダミー枠が変わりました。割当は行いません。");
            // Confirm the selected cells again before opening its popup.
            VerifyCells(capture, matches[0]);
            InvokeButton(capture, matches[0].Assignment!, mayOperate);
        }
        public AssignmentPatient Search(string patientId, Func<bool> mayOperate)
        {
            Until("patient_search_submit_dom", capture =>
            {
                var scope = DummyAssignmentParser.Scope(capture.Root);
                DummyAssignmentParser.VerifySlot(capture.Root, scope, expected);
                DummyAssignmentParser.VerifySearchMode(scope);
                DummyAssignmentParser.Button(scope, "患者検索");
                IEAssignmentDom.Execute(owner.AssignmentHtmlServer(capture, scope), expected.InternalId, patientId, "search", mayOperate);
                return true;
            });
            return Until("patient_search_result", capture =>
            {
                var scope = DummyAssignmentParser.Scope(capture.Root);
                DummyAssignmentParser.VerifySlot(capture.Root, scope, expected);
                // The query may be cleared by the search navigation. Identity
                // comes from the result fields, never from the query alone.
                return DummyAssignmentParser.Patient(scope, patientId);
            });
        }
        public void Commit(AssignmentPatient patient, Func<bool> mayOperate)
        {
            using var capture = owner.ReadTree();
            string phase = "commit_patient_verification";
            try
            {
                var scope = DummyAssignmentParser.Scope(capture.Root);
                DummyAssignmentParser.VerifySlot(capture.Root, scope, expected);
                var current = DummyAssignmentParser.Patient(scope, patient.PatientId);
                if (current != patient)
                    throw new BridgeException("patient_search_unverified", "操作直前に検索結果が変わりました。");
                phase = "commit_slot_verification";
                var rows = RowParser.Parse(capture.Root, owner.settings);
                var slot = rows.Where(r => r.Reservation.ReceptionNo == expected.ReceptionNo).ToArray();
                if (slot.Length != 1 || !slot[0].Reservation.IsUnassignedDummy ||
                    slot[0].Reservation.InternalId != expected.InternalId || rows.Any(r => r.Reservation.PatientId == patient.PatientId))
                    throw new BridgeException("dummy_slot_changed", "枠または患者の予約状態が変わりました。");
                VerifyCells(capture, slot[0]);
                phase = "commit_button_dom";
                DummyAssignmentParser.Button(scope, "割当する");
                nint server = owner.AssignmentHtmlServer(capture, scope);
                IEAssignmentDom.Execute(server, expected.InternalId, patient.PatientId, "commit", mayOperate, () => CommitAttempted = true);
            }
            catch (BridgeException ex)
            {
                string? diagnostic = OperationalLog.SaveAssignmentFailureTree(
                    $"at={DateTimeOffset.Now:O} phase={phase} code={ex.Code} commitAttempted={CommitAttempted}\r\n" + FormatTree(capture.Root));
                OperationalLog.Write("assignment_commit_failed", new { phase, ex.Code, ex.Message, CommitAttempted, diagnostic });
                string file = diagnostic is null ? "" : $" 診断ファイル: {Path.GetFileName(diagnostic)}";
                throw new BridgeException(ex.Code, $"[phase={phase} commitAttempted={CommitAttempted}] {ex.Message}{file}");
            }
        }
        public Reservation AwaitAssigned(Reservation target, string patientId)
        {
            // Give confirmation its own budget after search and dispatch. This path
            // contains no input or button operations, so a fresh capture is safe.
            clock.Restart();
            return Until("assigned_list", capture =>
            {
                var rows = RowParser.Parse(capture.Root, owner.settings).Select(r => r.Reservation).ToArray();
                var patients = rows.Where(r => r.PatientId == patientId).ToArray();
                if (patients.Length != 1 || patients[0].ReceptionNo != target.ReceptionNo || !patients[0].HasPatientIdentity ||
                    patients[0].InternalId != target.InternalId)
                    throw new BridgeException("patient_search_unverified", "割当後の一覧を確認しています。");
                return patients[0];
            }, retryComReads: true);
        }
    }

    public string Diagnose()
    {
        using var capture = ReadTree();
        string structure = FormatTree(capture.Root);
        if (!DummyAssignmentParser.HasPopup(capture.Root))
        {
            try
            {
                var target = RowParser.Parse(capture.Root, settings).FirstOrDefault(r =>
                    r.Link is not null && !r.Reservation.IsUnassignedDummy && r.Reservation.InternalId.Length > 0);
                if (target is null) return "連携HTML診断の対象ボタンがありません。\r\n\r\n" + structure;
                return $"診断対象: 受付番号={target.Reservation.ReceptionNo} 内部予約ID={target.Reservation.InternalId}\r\n" +
                    IEAssignmentDom.InspectLink(HtmlServer(capture, target.Link!.Key), target.Reservation.InternalId) + "\r\n" + structure;
            }
            catch (Exception ex)
            {
                string cause = ex is BridgeException bridge ? bridge.Code + ": " + bridge.Message : ex.GetType().Name + $" HRESULT=0x{ex.HResult:X8}";
                return "連携HTML診断を取得できませんでした。" + cause + "\r\n\r\n" + structure;
            }
        }
        try
        {
            var scope = DummyAssignmentParser.Scope(capture.Root);
            var matches = new List<Reservation>();
            foreach (var row in RowParser.Parse(capture.Root, settings))
            {
                try { DummyAssignmentParser.VerifySlot(capture.Root, scope, row.Reservation); matches.Add(row.Reservation); }
                catch (BridgeException ex) when (ex.Code == "dummy_slot_changed") { }
            }
            if (matches.Count != 1)
                throw new BridgeException("assignment_popup_not_unique", "HTML診断の対象予約枠を一意に特定できません。");
            return IEAssignmentDom.Inspect(AssignmentHtmlServer(capture, scope), matches[0].InternalId) + "\r\n" + structure;
        }
        catch (Exception ex)
        {
            string cause = ex is BridgeException bridge ? bridge.Code + ": " + bridge.Message : ex.GetType().Name + $" HRESULT=0x{ex.HResult:X8}";
            return "割当HTML診断を取得できませんでした。" + cause + "\r\n\r\n" + structure;
        }
    }
    private static string FormatTree(UiNode root)
    {
        var output = new StringBuilder("画面構造診断（患者情報を含みます。外部への共有前に匿名化してください）\r\n");
        void Dump(UiNode n, int depth)
        {
            output.AppendLine($"{new string(' ', depth * 2)}#{n.Key} type={n.ControlType} role={n.Role} enabled={n.Enabled} framework={n.Framework} name=[{n.Name}] id=[{n.AutomationId}] help=[{n.HelpText}] value=[{n.Value}]");
            foreach (var c in n.Children) Dump(c, depth + 1);
        }
        Dump(root, 0);
        return output.ToString();
    }

    public void Dispose() { if (Marshal.IsComObject(automation)) Marshal.ReleaseComObject(automation); }
}
