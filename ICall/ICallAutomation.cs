using System.Runtime.InteropServices;
using System.Text;
using iCallManager.Core;
using UIAutomationClient;

namespace iCallManager.ICall;

public sealed class ICallAutomation(AppSettings settings) : IReservationAdapter
{
    private readonly CUIAutomation automation = new();

    private sealed class Capture : IDisposable
    {
        public List<object> ComObjects { get; } = [];
        public Dictionary<int, IUIAutomationElement> Elements { get; } = [];
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
                int role = 0;
                if (element.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern legacy)
                { capture.Own(legacy); role = (int)legacy.CurrentRole; }
                var node = new UiNode
                {
                    Key = count, Name = (element.CurrentName ?? "").Trim(),
                    AutomationId = element.CurrentAutomationId ?? "", HelpText = (element.CurrentHelpText ?? "").Trim(),
                    Framework = element.CurrentFrameworkId ?? "", ControlType = element.CurrentControlType,
                    Role = role, Enabled = element.CurrentIsEnabled != 0
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
        if (button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
            throw new BridgeException("invoke_unavailable", "InvokePatternがありません。");
        capture.Own(invoke);
        if (!mayInvoke()) throw new BridgeException("operations_disabled", "実操作が無効になりました。");
        invoke.Invoke();
    }

    public string Diagnose()
    {
        using var capture = ReadTree();
        var output = new StringBuilder("画面構造診断（患者情報を含みます。外部への共有前に匿名化してください）\r\n");
        void Dump(UiNode n, int depth)
        {
            output.AppendLine($"{new string(' ', depth * 2)}#{n.Key} type={n.ControlType} role={n.Role} enabled={n.Enabled} framework={n.Framework} name=[{n.Name}] id=[{n.AutomationId}] help=[{n.HelpText}]");
            foreach (var c in n.Children) Dump(c, depth + 1);
        }
        Dump(capture.Root, 0);
        return output.ToString();
    }

    public void Dispose() { if (Marshal.IsComObject(automation)) Marshal.ReleaseComObject(automation); }
}
