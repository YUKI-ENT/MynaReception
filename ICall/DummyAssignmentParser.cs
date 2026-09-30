using System.Text.RegularExpressions;
using iCallManager.Core;

namespace iCallManager.ICall;

public static class DummyAssignmentParser
{
    private static string Normalize(string text) => Regex.Replace(text, @"[\s　:：]", "");
    private static IEnumerable<UiNode> All(UiNode node) => new[] { node }.Concat(node.Descendants());

    public static UiNode Scope(UiNode root)
    {
        var frames = All(root).Where(n => n.ControlType == 50032 &&
            Normalize(n.Name) == "順番ダミー割当" && n.Descendants().Any(c => c.AutomationId == "findword")).ToArray();
        if (frames.Length == 1) return frames[0];
        var windows = All(root).Where(n => n.ControlType == 50032 &&
            Normalize(n.Name).Contains("順番待ち予約ダミー割当") &&
            n.Descendants().Any(c => c.AutomationId == "findword")).ToArray();
        var innermost = windows.Where(n => !n.Descendants().Any(windows.Contains)).ToArray();
        if (innermost.Length != 1) throw new BridgeException("assignment_popup_not_unique", "ダミー割当画面を1つに特定できません。");
        return innermost[0];
    }
    public static bool HasPopup(UiNode root) => All(root).Any(n => n.ControlType == 50032 &&
        (Normalize(n.Name) == "順番ダミー割当" || Normalize(n.Name).Contains("順番待ち予約ダミー割当")));

    public static UiNode Input(UiNode scope)
    {
        var inputs = All(scope).Where(n => n.ControlType == 50004 && n.AutomationId == "findword" && n.Enabled).ToArray();
        if (inputs.Length != 1) throw new BridgeException("assignment_input_not_unique", "診察券番号入力欄を1件に特定できません。");
        return inputs[0];
    }
    public static UiNode Button(UiNode scope, string name)
    {
        var buttons = All(scope).Where(n => n.ControlType == 50000 && n.Name == name && n.Enabled).ToArray();
        if (buttons.Length != 1) throw new BridgeException("assignment_button_unavailable", name + "ボタンを1件に特定できません。");
        return buttons[0];
    }
    public static void VerifySearchMode(UiNode scope)
    {
        var input = Input(scope);
        var search = Button(scope, "患者検索");
        var containers = All(scope).Where(n => n.Descendants().Contains(input) && n.Descendants().Contains(search)).ToArray();
        var smallest = containers.OrderBy(n => n.Descendants().Count()).FirstOrDefault() ?? scope;
        var combos = All(smallest).Where(n => n.ControlType == 50003).ToArray();
        if (combos.Length != 1 || Normalize(combos[0].Value) != "診察券番号")
            throw new BridgeException("patient_search_mode_unverified", "検索条件が診察券番号になっていることを確認できません。");
    }
    private static string Text(UiNode node)
    {
        if (node.ControlType == 50004) return node.Value;
        var leaves = node.Descendants().Where(n => n.ControlType == 50020 && !string.IsNullOrWhiteSpace(n.Name))
            .Select(n => n.Name.Trim()).Distinct().ToArray();
        if (leaves.Length > 0) return string.Join(" ", leaves);
        var edits = node.Descendants().Where(n => n.ControlType == 50004).ToArray();
        return edits.Length == 1 ? edits[0].Value : node.Name.Trim();
    }
    private static string Label(UiNode scope, string label)
    {
        var values = new List<string>();
        foreach (var parent in All(scope))
            for (int i = 0; i + 1 < parent.Children.Count; i++)
                if (Normalize(Text(parent.Children[i])) == label)
                    values.Add(Text(parent.Children[i + 1]).Trim());
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            throw new BridgeException("patient_search_unverified", label + "の結果を1件に特定できません。");
        return values[0];
    }
    public static void VerifySlot(UiNode root, UiNode scope, Reservation expected)
    {
        string slot = Normalize(Label(scope, "割当予約番号"));
        if (slot != expected.ReceptionNo && slot != expected.ReceptionNo + "番")
            throw new BridgeException("dummy_slot_changed", "割当画面の受付番号が要求した枠と一致しません。");
        if (expected.InternalId.Length > 0)
        {
            var ids = All(root).Where(n => n == scope || n.Descendants().Contains(scope) || scope.Descendants().Contains(n))
                .SelectMany(n => Regex.Matches(n.Name, @"[?&]rsv_sn=([0-9]+)(?:&|$)").Cast<Match>())
                .Select(m => m.Groups[1].Value).Distinct().ToArray();
            if (ids.Length != 1 || ids[0] != expected.InternalId)
                throw new BridgeException("dummy_slot_changed", "割当画面の内部予約IDが一致しません。");
        }
    }
    public static AssignmentPatient Patient(UiNode scope, string expectedPatientId)
    {
        var patient = new AssignmentPatient(Label(scope, "診察券"), Label(scope, "おなまえ"));
        if (patient.PatientId != expectedPatientId || patient.PatientName == "-")
            throw new BridgeException("patient_search_unverified", "検索結果の診察券番号が要求と一致しません。");
        Button(scope, "割当する");
        return patient;
    }
}
