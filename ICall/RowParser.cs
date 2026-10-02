using System.Text.RegularExpressions;
using iCallManager.Core;

namespace iCallManager.ICall;

public sealed class UiNode
{
    public int Key { get; init; }
    public string Name { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string Value { get; init; } = "";
    public string HelpText { get; init; } = "";
    public string Framework { get; init; } = "";
    public int ControlType { get; init; }
    public int Role { get; init; }
    public bool Enabled { get; init; }
    public List<UiNode> Children { get; } = [];
    public IEnumerable<UiNode> Descendants() => Children.SelectMany(c => new[] { c }.Concat(c.Descendants()));
}

// Container is the real row when exposed, otherwise the real table. Cells always belong to this patient only.
public sealed record ParsedRow(Reservation Reservation, UiNode Container, IReadOnlyList<UiNode> Cells,
    UiNode? Arrival, UiNode? Link)
{
    public UiNode? Assignment { get; init; }
}

public static class RowParser
{
    private static bool IsTable(UiNode n) => n.Role == 24 || n.ControlType is 50036 or 50028;
    private static bool IsCell(UiNode n) => n.Role is 25 or 26 or 29 || n.ControlType == 50035;
    private static bool IsRow(UiNode n) => n.Role == 28 ||
        n.ControlType == 50029 && n.Children.Any(IsCell);
    private static IEnumerable<UiNode> InsideTable(UiNode n) => n.Children
        .Where(c => !IsTable(c)).SelectMany(c => new[] { c }.Concat(InsideTable(c)));
    private static string Normalize(string text) => Regex.Replace(text, @"[\s　:：]", "");

    private static string CellText(UiNode cell)
    {
        // Prefer leaf text; parent Name commonly repeats the same accessible text.
        var text = cell.Descendants().Where(n => n.ControlType == 50020 && !string.IsNullOrWhiteSpace(n.Name))
            .Select(n => n.Name.Trim()).Distinct().ToArray();
        return text.Length == 0 ? cell.Name.Trim() : string.Join(" ", text);
    }

    private static List<UiNode> Cells(UiNode row)
    {
        var direct = row.Children.Where(IsCell).ToList();
        return direct.Count > 0 ? direct : row.Children.SelectMany(c => c.Children).Where(IsCell).ToList();
    }

    public static IReadOnlyList<ParsedRow> Parse(UiNode root, AppSettings settings)
    {
        var flatRows = ParseFlatICallTable(root, settings);
        if (flatRows is not null) return flatRows;
        var candidates = new List<List<ParsedRow>>();
        foreach (var table in new[] { root }.Concat(root.Descendants()).Where(IsTable))
        {
            var rows = InsideTable(table).Where(IsRow).ToList();
            var cells = rows.Select(Cells).ToList();
            int reception = settings.ReceptionNoColumn, patient = settings.PatientIdColumn, name = settings.PatientNameColumn;
            int waitingOrderColumn = -1;
            var headerIndexes = new HashSet<int>();
            for (int i = 0; i < cells.Count; i++)
            {
                var labels = cells[i].Select(c => Normalize(CellText(c))).ToList();
                int r = labels.FindIndex(t => t is "受付番号" or "予約番号" or "順番" or "番号");
                int p = labels.FindIndex(t => t is "診察券番号" or "患者ID" or "PatientID" or "診察券");
                int n = labels.FindIndex(t => t is "患者名" or "氏名" or "名前" or "おなまえ");
                if (r >= 0 && p >= 0 && n >= 0)
                {
                    waitingOrderColumn = labels.FindIndex(t => t == "待ち順");
                    headerIndexes.Add(i);
                    if (reception < 0) reception = r;
                    if (patient < 0) patient = p;
                    if (name < 0) name = n;
                }
            }
            if (reception < 0 || patient < 0 || name < 0 || new[] { reception, patient, name }.Distinct().Count() != 3)
                continue;
            // Require at least one data row or a recognized header before accepting a table.
            if (cells.Count == 0) continue;
            var parsed = new List<ParsedRow>();
            bool hasData = false, invalid = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (headerIndexes.Contains(i) || cells[i].Count > 0 && cells[i].All(c => c.Role == 25)) continue;
                if (cells[i].Count <= Math.Max(reception, Math.Max(patient, name))) { invalid = true; continue; }
                string no = CellText(cells[i][reception]), id = CellText(cells[i][patient]), patientName = CellText(cells[i][name]);
                if (!Regex.IsMatch(no, @"^\d+$") || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(patientName))
                { invalid = true; continue; }
                hasData = true;
                var buttons = rows[i].Descendants().Where(n => n.ControlType == 50000).ToList();
                var arrivals = buttons.Where(n => n.Name == "来院確認").ToArray();
                var assignments = buttons.Where(n => n.Name == "患者割当").ToArray();
                var links = buttons.Where(n => Regex.IsMatch(n.AutomationId, @"^chk\d+$")).ToArray();
                if (arrivals.Length > 1 || assignments.Length > 1 || links.Length > 1) { invalid = true; continue; }
                var assignment = assignments.SingleOrDefault();
                var arrival = arrivals.SingleOrDefault();
                var link = links.SingleOrDefault();
                // HelpText, when exposed, must agree with the name cell.
                if (new[] { arrival, link }.Any(b => b != null && b.HelpText.Length > 0 && Normalize(b.HelpText) != Normalize(patientName)))
                { invalid = true; continue; }
                bool identified = id != "-" && patientName != "-";
                parsed.Add(new(new(no, id, patientName, link?.AutomationId[3..] ?? "",
                    identified && arrival?.Enabled == true, identified && link?.Enabled == true)
                    { HasMarkArrivedButton = arrival != null, HasLinkButton = link != null, LinkButtonName = link?.Name,
                      HasAssignmentButton = assignment != null, CanAssignDummy = id == "-" && patientName == "-" && assignment?.Enabled == true,
                      WaitingOrder = waitingOrderColumn >= 0 && int.TryParse(CellText(cells[i][waitingOrderColumn]), out int order) ? order : null },
                    rows[i], cells[i], arrival, link) { Assignment = assignment });
            }
            if (hasData || headerIndexes.Count > 0)
            {
                if (invalid) throw new BridgeException("row_layout_unverified", "受付候補テーブルに解析できない行があります。行構造診断と列設定を確認してください。");
                candidates.Add(parsed);
            }
        }
        if (candidates.Count != 1)
            throw new BridgeException("table_not_unique", "受付テーブルを一意に特定できません。行構造診断と列設定を確認してください。");
        return candidates[0];
    }

    private static BridgeException FlatLayoutError(string detail) =>
        new("row_layout_unverified", "iCall受付テーブルの構造を確認できません。" + detail);

    private static IReadOnlyList<ParsedRow>? ParseFlatICallTable(UiNode root, AppSettings settings)
    {
        var nodes = new[] { root }.Concat(root.Descendants()).ToList();
        var tables = nodes.Where(n => IsTable(n) && n.AutomationId == "tableList").ToArray();
        if (tables.Length == 0) return null;
        if (tables.Length != 1) throw new BridgeException("table_not_unique", "iCall受付テーブル(tableList)が複数あります。");
        var table = tables[0];
        if (InsideTable(table).Any(IsRow)) return null;
        if (table.Framework != settings.FrameworkId) throw FlatLayoutError("IEモードの表ではありません。");

        // The actual IE tree exposes a separate header table followed by a flat tableList.
        // 機能選択 spans three data cells: arrival/assignment, hold, guide.
        var parent = nodes.SingleOrDefault(n => n.Children.Contains(table));
        int index = parent?.Children.IndexOf(table) ?? -1;
        if (index < 1 || !IsTable(parent!.Children[index - 1])) throw FlatLayoutError("直前の見出し表がありません。");
        var header = parent.Children[index - 1];
        string[] expectedHeaders = ["待ち順", "受付時刻", "受付番号", "診察券", "おなまえ", "メモ",
            "性別", "年齢", "受付方法", "お知らせ", "チェック", "連携", "来院時刻", "機能選択"];
        if (header.Children.Count != expectedHeaders.Length || !header.Children.All(IsCell) ||
            !header.Children.Select(c => Normalize(CellText(c))).SequenceEqual(expectedHeaders))
            throw FlatLayoutError("見出しの順序または列数が診断済み構造と異なります。");
        if (settings.ReceptionNoColumn is not (-1 or 2) || settings.PatientIdColumn is not (-1 or 3) ||
            settings.PatientNameColumn is not (-1 or 4))
            throw FlatLayoutError("列設定は自動(-1)、または受付番号=2・患者ID=3・患者名=4にしてください。");
        const int width = 16;
        if (!table.Children.All(IsCell) || table.Children.Count % width != 0)
            throw FlatLayoutError("データ列数が16の倍数ではありません。画面更新途中の場合は次回同期で再確認します。");

        var result = new List<ParsedRow>();
        foreach (var cells in table.Children.Chunk(width))
        {
            string no = CellText(cells[2]), id = CellText(cells[3]), name = CellText(cells[4]);
            string waitingText = CellText(cells[0]);
            int? waitingOrder = null;
            if (waitingText != "-")
            {
                if (!Regex.IsMatch(waitingText, @"\A[1-9][0-9]*\z") ||
                    !int.TryParse(waitingText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int order))
                    throw FlatLayoutError("待ち順の値を確認できません。");
                waitingOrder = order;
            }
            var numberLinks = cells[2].Descendants().Where(n => n.ControlType == 50005).ToArray();
            if (!Regex.IsMatch(CellText(cells[1]), @"\A(?:(?:[01][0-9]|2[0-3]):[0-5][0-9]|-)\z") ||
                !Regex.IsMatch(no, @"\A[0-9]+\z") || numberLinks.Length != 1 || numberLinks[0].Name != no)
                throw FlatLayoutError("待ち順・時刻・受付番号リンクの位置が一致しません。");

            var buttons = cells.SelectMany(c => c.Descendants()).Where(n => n.ControlType == 50000).ToList();
            var arrivals = buttons.Where(n => n.Name == "来院確認").ToArray();
            var assignments = buttons.Where(n => n.Name == "患者割当").ToArray();
                var links = buttons.Where(n => Regex.IsMatch(n.AutomationId, @"\Achk[0-9]+\z")).ToArray();
            var guides = buttons.Where(n => Regex.IsMatch(n.AutomationId, @"\Aguid01[0-9]+\z")).ToArray();
            if (arrivals.Length > 1 || assignments.Length > 1 || links.Length > 1 || guides.Length > 1 ||
                arrivals.Any(n => !cells[13].Descendants().Contains(n)) ||
                assignments.Any(n => !cells[13].Descendants().Contains(n)) ||
                links.Any(n => !cells[11].Descendants().Contains(n)) ||
                guides.Any(n => !cells[15].Descendants().Contains(n)))
                throw FlatLayoutError("操作ボタンの列位置または個数が一致しません。");
            var assignment = assignments.SingleOrDefault();
                var arrival = arrivals.SingleOrDefault();
            var link = links.SingleOrDefault();
            var guide = guides.SingleOrDefault();
            string internalId = link?.AutomationId[3..] ?? guide?.AutomationId[6..] ?? "";
            if (link != null && guide != null && link.AutomationId[3..] != guide.AutomationId[6..])
                throw FlatLayoutError("同じ行の連携・案内ボタンの内部IDが一致しません。");
            if (new[] { arrival, link, guide }.Any(b => b != null && b.HelpText.Length > 0 && Normalize(b.HelpText) != Normalize(name)))
                throw FlatLayoutError("氏名セルとボタンの患者名が一致しません。");
            bool identified = !string.IsNullOrWhiteSpace(id) && id != "-" && !string.IsNullOrWhiteSpace(name) && name != "-";
            result.Add(new(new(no, id, name, internalId, identified && arrival?.Enabled == true,
                identified && link?.Enabled == true)
                { HasMarkArrivedButton = arrival != null, HasLinkButton = link != null, LinkButtonName = link?.Name,
                  HasAssignmentButton = assignment != null, CanAssignDummy = id == "-" && (name == "-" || string.IsNullOrWhiteSpace(name)) && assignment?.Enabled == true,
                  WaitingOrder = waitingOrder }, table, cells, arrival, link) { Assignment = assignment });
        }
        if (result.Select(r => r.Reservation.ReceptionNo).Distinct().Count() != result.Count ||
            result.Where(r => r.Reservation.InternalId.Length > 0).GroupBy(r => r.Reservation.InternalId).Any(g => g.Count() > 1))
            throw FlatLayoutError("受付番号または内部IDが重複しています。");
        return result;
    }
}
