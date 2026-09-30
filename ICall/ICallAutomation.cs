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
            var matches = new List<IUIAutomationElement>();
            for (int i = 0; i < windows.Length; i++)
            {
                var window = capture.Own(windows.GetElement(i));
                if ((window.CurrentName ?? "").Contains(settings.WindowTitleContains, StringComparison.Ordinal)) matches.Add(window);
            }
            if (matches.Count != 1)
                throw new BridgeException("window_not_unique", $"iCall管理画面が{matches.Count}件あります。ログイン済み管理画面を1つ表示してください。");
            var walker = capture.Own(automation.ControlViewWalker);
            int count = 0;
            UiNode Visit(IUIAutomationElement element, int depth)
            {
                if (++count > 20000 || depth > 60)
                    throw new BridgeException("tree_limit", "画面構造が大きすぎます。対象画面を確認してください。");
                capture.ProcessIds.Add(element.CurrentProcessId);
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
                    Key = count, Name = (element.CurrentName ?? "").Trim(),
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
            capture.Root = Visit(matches[0], 0);
            // IE dialogs can be owned top-level windows rather than descendants of the main page.
            for (int i = 0; i < windows.Length; i++)
            {
                var window = capture.Own(windows.GetElement(i));
                if (capture.ProcessIds.Contains(window.CurrentProcessId) &&
                    (window.CurrentName ?? "").Contains("ダミー割当", StringComparison.Ordinal) &&
                    !(window.CurrentName ?? "").Contains(settings.WindowTitleContains, StringComparison.Ordinal) &&
                    !capture.Root.Descendants().Any(n => n.ControlType == 50032 && n.Name == (window.CurrentName ?? "").Trim()))
                    capture.Root.Children.Add(Visit(window, 0));
            }
            if (!capture.Root.Descendants().Any(n => n.Framework == settings.FrameworkId))
                throw new BridgeException("framework_not_found", "対象画面にIEモードの要素が見つかりません。");
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    public IReadOnlyList<Reservation> Read()
    {
        using var capture = ReadTree();
        return RowParser.Parse(capture.Root, settings).Select(r => r.Reservation).ToArray();
    }

    public void Invoke(Reservation expected, string action, Func<bool> mayInvoke)
        => InvokeCore(expected, action, mayInvoke, true);

    private void InvokeCore(Reservation expected, string action, Func<bool> mayInvoke, bool prepareHover)
    {
        if (action is not ("arrived" or "link")) throw new BridgeException("unsupported_action", "操作対象外です。");
        if (!expected.HasPatientIdentity) throw new BridgeException("identity_missing", "患者未割当の予約は操作できません。");
        using var capture = ReadTree();
        var rows = RowParser.Parse(capture.Root, settings).Where(r => r.Reservation.PatientId == expected.PatientId).ToArray();
        if (rows.Length != 1 || rows[0].Reservation.PatientName != expected.PatientName ||
            rows[0].Reservation.ReceptionNo != expected.ReceptionNo || rows[0].Reservation.InternalId != expected.InternalId)
            throw new BridgeException("identity_changed", "操作直前に患者行が変わりました。再確認してください。");
        var row = rows[0];
        var buttonNode = action == "arrived" ? row.Arrival : row.Link;
        if (action == "link" && buttonNode?.Name == "〆") return;
        if (action == "link" && buttonNode?.Name != "")
            throw new BridgeException("link_state_unverified", "連携ボタンの状態を確認できません。");
        if (buttonNode is null || !buttonNode.Enabled)
            throw new BridgeException("action_unavailable", "対象ボタンが操作できません。");
        var button = capture.Elements[buttonNode.Key];
        if (action == "link" && prepareHover)
        {
            Hover(capture, button, mayInvoke);
            // A fresh tree is required after waiting: the list can refresh or reorder.
            InvokeCore(expected, action, mayInvoke, false);
            return;
        }
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
            VerifyPointer(capture, button);
            if (!mayInvoke()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
            try
            {
                // One physical click after mouseover. Never fall back to another click.
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            }
            catch { throw new BridgeException("outcome_unknown", "連携クリックの結果が不明です。画面を確認してください。"); }
            return;
        }
        if (button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
            throw new BridgeException("invoke_unavailable", "InvokePatternがありません。");
        capture.Own(invoke);
        if (!mayInvoke()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
        invoke.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static IntPtr MainWindow(Capture capture) =>
        GetAncestor(new IntPtr(capture.Elements[capture.Root.Key].CurrentNativeWindowHandle), 2);

    private void VerifyPointer(Capture capture, IUIAutomationElement button)
    {
        var rect = button.CurrentBoundingRectangle;
        var main = MainWindow(capture);
        if (main == IntPtr.Zero || GetForegroundWindow() != main || button.CurrentIsOffscreen != 0 ||
            rect.right <= rect.left || rect.bottom <= rect.top || !GetCursorPos(out var point) ||
            point.X != (rect.left + rect.right) / 2 || point.Y != (rect.top + rect.bottom) / 2 ||
            GetAncestor(WindowFromPoint(point), 2) != main)
            throw new BridgeException("hover_interrupted", "連携ボタンの位置・前面表示またはマウス位置が変わりました。操作を中止しました。");
        var hit = capture.Own(automation.ElementFromPoint(new tagPOINT { x = point.X, y = point.Y }));
        if (hit.CurrentProcessId != button.CurrentProcessId || hit.CurrentControlType != 50000 ||
            hit.CurrentAutomationId != button.CurrentAutomationId)
            throw new BridgeException("hover_interrupted", "マウス位置に対象の連携ボタンがありません。操作を中止しました。");
    }

    private void Hover(Capture capture, IUIAutomationElement button, Func<bool> mayOperate)
    {
        if (!mayOperate()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
        var main = MainWindow(capture);
        if (main == IntPtr.Zero || GetForegroundWindow() != main && !SetForegroundWindow(main))
            throw new BridgeException("hover_unavailable", "iCall管理画面を前面に表示してください。");
        if (button.GetCurrentPattern(UIA_PatternIds.UIA_ScrollItemPatternId) is IUIAutomationScrollItemPattern scroll)
        { capture.Own(scroll); scroll.ScrollIntoView(); }
        var rect = button.CurrentBoundingRectangle;
        if (button.CurrentIsOffscreen != 0 || rect.right <= rect.left || rect.bottom <= rect.top ||
            !SetCursorPos((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2))
            throw new BridgeException("hover_unavailable", "連携ボタンへマウスを移動できません。");
        for (int i = 0; i < 25; i++)
        {
            Thread.Sleep(100);
            if (!mayOperate()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
            VerifyPointer(capture, button);
        }
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
        invoke.Invoke();
    }

    public Reservation AssignDummy(Reservation expected, string patientId, Func<bool> mayOperate) =>
        DummyAssignment.Run(new AssignmentSession(this), expected, patientId, mayOperate);

    private sealed class AssignmentSession(ICallAutomation owner) : IDummyAssignmentSession
    {
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private Reservation expected = null!;
        private T Until<T>(Func<Capture, T> read)
        {
            BridgeException? last = null;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                try { using var capture = owner.ReadTree(); return read(capture); }
                catch (BridgeException ex) when (ex.Code is "assignment_popup_not_unique" or "assignment_input_not_unique" or
                    "assignment_button_unavailable" or "patient_search_unverified" or "row_layout_unverified")
                { last = ex; }
                Thread.Sleep(200);
            }
            throw new BridgeException("assignment_timeout", "患者割当の画面更新待ちがタイムアウトしました。" + last?.Message);
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
            Until(capture =>
            {
                var scope = DummyAssignmentParser.Scope(capture.Root);
                DummyAssignmentParser.VerifySlot(capture.Root, scope, expected);
                DummyAssignmentParser.VerifySearchMode(scope);
                var input = capture.Elements[DummyAssignmentParser.Input(scope).Key];
                if (input.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is not IUIAutomationValuePattern value ||
                    value.CurrentIsReadOnly != 0)
                    throw new BridgeException("value_unavailable", "診察券番号入力欄をValuePatternで編集できません。");
                capture.Own(value);
                if (!mayOperate()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
                value.SetValue(patientId);
                if (value.CurrentValue != patientId)
                    throw new BridgeException("patient_input_mismatch", "入力した診察券番号を確認できません。");
                InvokeButton(capture, DummyAssignmentParser.Button(scope, "患者検索"), mayOperate);
                return true;
            });
            return Until(capture =>
            {
                var scope = DummyAssignmentParser.Scope(capture.Root);
                DummyAssignmentParser.VerifySlot(capture.Root, scope, expected);
                if (DummyAssignmentParser.Input(scope).Value != patientId)
                    throw new BridgeException("patient_input_mismatch", "検索中に診察券番号が変わりました。");
                return DummyAssignmentParser.Patient(scope, patientId);
            });
        }
        public void Commit(AssignmentPatient patient, Func<bool> mayOperate)
        {
            using var capture = owner.ReadTree();
            var scope = DummyAssignmentParser.Scope(capture.Root);
            DummyAssignmentParser.VerifySlot(capture.Root, scope, expected);
            var current = DummyAssignmentParser.Patient(scope, patient.PatientId);
            if (current != patient || DummyAssignmentParser.Input(scope).Value != patient.PatientId)
                throw new BridgeException("patient_search_unverified", "操作直前に検索結果が変わりました。");
            var rows = RowParser.Parse(capture.Root, owner.settings);
            var slot = rows.Where(r => r.Reservation.ReceptionNo == expected.ReceptionNo).ToArray();
            if (slot.Length != 1 || !slot[0].Reservation.IsUnassignedDummy ||
                slot[0].Reservation.InternalId != expected.InternalId || rows.Any(r => r.Reservation.PatientId == patient.PatientId))
                throw new BridgeException("dummy_slot_changed", "枠または患者の予約状態が変わりました。");
            VerifyCells(capture, slot[0]);
            InvokeButton(capture, DummyAssignmentParser.Button(scope, "割当する"), mayOperate);
        }
        public Reservation AwaitAssigned(Reservation target, string patientId)
        {
            return Until(capture =>
            {
                var rows = RowParser.Parse(capture.Root, owner.settings).Select(r => r.Reservation).ToArray();
                var patients = rows.Where(r => r.PatientId == patientId).ToArray();
                if (patients.Length != 1 || patients[0].ReceptionNo != target.ReceptionNo || !patients[0].HasPatientIdentity ||
                    patients[0].InternalId != target.InternalId)
                    throw new BridgeException("patient_search_unverified", "割当後の一覧を確認しています。");
                return patients[0];
            });
        }
    }

    public string Diagnose()
    {
        using var capture = ReadTree();
        var output = new StringBuilder("画面構造診断（患者情報を含みます。外部への共有前に匿名化してください）\r\n");
        void Dump(UiNode n, int depth)
        {
            output.AppendLine($"{new string(' ', depth * 2)}#{n.Key} type={n.ControlType} role={n.Role} enabled={n.Enabled} framework={n.Framework} name=[{n.Name}] id=[{n.AutomationId}] help=[{n.HelpText}] value=[{n.Value}]");
            foreach (var c in n.Children) Dump(c, depth + 1);
        }
        Dump(capture.Root, 0);
        return output.ToString();
    }

    public void Dispose() { if (Marshal.IsComObject(automation)) Marshal.ReleaseComObject(automation); }
}
