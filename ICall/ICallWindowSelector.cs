using iCallManager.Core;

namespace iCallManager.ICall;

public sealed record ICallWindow(string Name, string ClassName, int ProcessId)
{
    public bool ContainsReservationList { get; init; }
    public bool ContainsAssignmentDialog { get; init; }
}

public static class ICallWindowSelector
{
    // IE modal dialogs may inherit the management page's title. Their native
    // window class distinguishes them even before the dialog title is loaded.
    public static bool IsDialog(ICallWindow window) =>
        window.ClassName == "Internet Explorer_TridentDlgFrame" ||
        // The IE modal host can outlive its HTML dialog after form submission.
        // In that state its title still matches the management page, but it
        // contains neither a reservation list nor any assignment controls.
        (window.ClassName == "Alternate Modal Top Most" && !window.ContainsReservationList) ||
        window.Name.Contains("ダミー割当", StringComparison.Ordinal) ||
        window.ContainsAssignmentDialog && !window.ContainsReservationList;

    public static ICallWindow DescribeContent(ICallWindow window, UiNode root, AppSettings? settings = null)
    {
        var nodes = new[] { root }.Concat(root.Descendants()).ToArray();
        bool hasList = nodes.Any(n => n.AutomationId == "tableList");
        if (!hasList)
        {
            // Retain support for IE providers that expose actual table rows
            // instead of the flat tableList structure.
            try { RowParser.Parse(root, settings ?? new()); hasList = true; }
            catch (BridgeException) { }
        }
        return window with
        {
            ContainsReservationList = hasList,
            ContainsAssignmentDialog = DummyAssignmentParser.HasPopup(root) || nodes.Any(n =>
                n.ControlType == 50032 &&
                (n.Name.Contains("vid=fmdmytrn", StringComparison.Ordinal) ||
                 n.Name.Contains("act=fmdmytrn", StringComparison.Ordinal)))
        };
    }

    public static int MainWindowIndex(IReadOnlyList<ICallWindow> windows, string titleContains)
    {
        var matches = Enumerable.Range(0, windows.Count).Where(i =>
            windows[i].Name.Contains(titleContains, StringComparison.Ordinal) && !IsDialog(windows[i])).ToArray();
        if (matches.Length != 1)
        {
            string details = string.Join("; ", Enumerable.Range(0, windows.Count)
                .Where(i => windows[i].Name.Contains(titleContains, StringComparison.Ordinal) || IsDialog(windows[i]))
                .Select(i => $"#{i} class={windows[i].ClassName} pid={windows[i].ProcessId} list={windows[i].ContainsReservationList} popup={windows[i].ContainsAssignmentDialog}"));
            throw new BridgeException("window_not_unique", $"iCall管理画面が{matches.Length}件あります。ログイン済み管理画面を1つ表示してください。[window-detect-v3] {details}");
        }
        return matches[0];
    }

    public static bool IncludeDialog(ICallWindow window, IReadOnlySet<int> processIds) =>
        IsDialog(window) && processIds.Contains(window.ProcessId);
}
