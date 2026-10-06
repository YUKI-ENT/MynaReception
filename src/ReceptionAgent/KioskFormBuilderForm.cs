using System.ComponentModel;
using System.Text.Json;
using ReceptionAgent.Kiosk;
using ReceptionAgent.Reception;

namespace ReceptionAgent;

internal sealed class KioskFlowEditorForm : Form
{
    private sealed record Pick(string Key, string Label);
    public KioskFlow Flow { get; private set; }
    private readonly ListBox pages = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox title = new() { Dock = DockStyle.Fill };
    private readonly TextBox message = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox buttonText = new() { Dock = DockStyle.Fill };
    private readonly ComboBox field = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox next = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox finish = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label kind = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = true };
    private readonly DataGridView choices = Grid();
    private readonly DataGridView routes = Grid();
    private KioskFlowPage? current;
    private bool loading;
    private readonly TextBox welcomeTitle = new() { Dock = DockStyle.Fill };
    private readonly TextBox welcomeMessage = new() { Dock = DockStyle.Fill, Multiline = true };
    private readonly TextBox welcomeButton = new() { Dock = DockStyle.Fill };
    private readonly ComboBox first = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true,
        AllowUserToDeleteRows = true, RowHeadersWidth = 25, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private static string KindName(KioskPageKind value) => value switch { KioskPageKind.Card => "説明", KioskPageKind.Birthday => "誕生月日",
        KioskPageKind.Choice => "質問", KioskPageKind.Lookup => "予約照会", _ => "案内" };
    private static void Bind(ComboBox combo, IEnumerable<Pick> items, string selected)
    { combo.DisplayMember = nameof(Pick.Label); combo.ValueMember = nameof(Pick.Key); combo.DataSource = items.ToList(); combo.SelectedValue = selected; }
    private IEnumerable<Pick> PagePicks(bool terminalOnly = false) => new[] { new Pick("", "（選択してください）") }
        .Concat(Flow.Pages.Where(p => !terminalOnly || p.Kind == KioskPageKind.Guidance).Select(p => new Pick(p.Id, KindName(p.Kind) + "：" + p.Title)));
    private IEnumerable<Pick> FieldPicks(bool includeSystem) => Flow.Fields.Select(f => new Pick(f.Key, f.Label)).Concat(includeSystem
        ? new[] { "month", "day", "category", "hasChart", "actualReservation" }.Select(key => new Pick(key, Flow.FieldLabel(key))) : []);
    public KioskFlowEditorForm(KioskFlow source)
    {
        Flow = JsonSerializer.Deserialize<KioskFlow>(JsonSerializer.Serialize(source))!;
        RegisterLegacyFields();
        Text = "Kiosk質問フォームの作成"; Size = new Size(1240, 880); MinimumSize = new Size(1000, 720); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 10);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(14) };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(new Label { AutoSize = true, Text = "左で質問・案内を選び、右で文面と回答を編集します。回答の保存先と、次に表示するページを選べます。", Padding = new Padding(0, 0, 0, 12) }, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Size = new Size(1200, 680), SplitterDistance = 300 };
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        left.RowStyles.Add(new(SizeType.Percent, 100)); left.RowStyles.Add(new(SizeType.AutoSize)); left.RowStyles.Add(new(SizeType.AutoSize));
        pages.FormattingEnabled = true; pages.Format += (_, e) => { if (e.ListItem is KioskFlowPage p) e.Value = $"{Flow.Pages.IndexOf(p) + 1}.  [{KindName(p.Kind)}] {p.Title}"; };
        left.Controls.Add(pages, 0, 0);
        var add = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        AddButton(add, "質問を追加", () => AddQuestion(false)); AddButton(add, "外来区分を追加", () => AddQuestion(true));
        AddButton(add, "案内を追加", AddGuidance); AddButton(add, "削除", RemovePage);
        left.Controls.Add(add, 0, 1);
        left.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(290, 0), Text = "入力：顔認証XML・誕生月日・質問の回答\n結果：XMLの参照・カルテ番号・予約番号・発熱・外来区分・追加回答\n\nXMLと照会結果はシステムが取得します。質問の回答で上書きしません。", Padding = new Padding(0, 12, 0, 0) }, 0, 2);
        split.Panel1.Controls.Add(left);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var questionTab = new TabPage("質問・案内の編集");
        var edit = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(12) };
        edit.ColumnStyles.Add(new(SizeType.Absolute, 150)); edit.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 32, 38, 110, 38, 44, 170, 44, 44 }) edit.RowStyles.Add(new(SizeType.Absolute, height));
        edit.RowStyles.Add(new(SizeType.Percent, 100));
        void Row(int index, string label, Control control) { edit.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, index); edit.Controls.Add(control, 1, index); }
        Row(0, "ページの種類", kind); Row(1, "質問・案内のタイトル", title); Row(2, "説明文", message); Row(3, "次へボタンの文面", buttonText);
        var fieldPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; fieldPanel.ColumnStyles.Add(new(SizeType.Percent, 100)); fieldPanel.ColumnStyles.Add(new(SizeType.AutoSize));
        fieldPanel.Controls.Add(field, 0, 0); var newField = new Button { Text = "保存項目を追加", AutoSize = true };
        newField.Click += (_, _) => Try(AddField); fieldPanel.Controls.Add(newField, 1, 0); Row(4, "回答を保存する項目", fieldPanel);
        choices.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Label", HeaderText = "回答ボタンの文面", FillWeight = 65 });
        choices.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Value", HeaderText = "保存する値", FillWeight = 35 }); Row(5, "回答ボタン", choices);
        Row(6, "通常の次ページ", next);
        Bind(finish, [new("staff", "職員に案内する"), new("finish", "案内表示を終了する（再診・予約あり）")], "staff"); Row(7, "最後の案内の動作", finish);
        Row(8, "", new Label { AutoSize = true, MaximumSize = new Size(650, 0), Text = "条件分岐がない場合は「通常の次ページ」へ進みます。別の案内へ進めたい回答は「表示条件」タブで設定します。", ForeColor = Color.DimGray });
        questionTab.Controls.Add(edit); tabs.TabPages.Add(questionTab);
        var routeTab = new TabPage("表示条件"); var routeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        routeLayout.RowStyles.Add(new(SizeType.AutoSize)); routeLayout.RowStyles.Add(new(SizeType.Percent, 100)); routeLayout.RowStyles.Add(new(SizeType.AutoSize));
        routeLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(780, 0), Text = "「回答項目 が この値 のとき → このページを表示」と設定します。上の条件から優先し、一致しなければ通常の次ページへ進みます。", Padding = new Padding(0, 0, 0, 12) }, 0, 0);
        routes.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Variable", HeaderText = "回答・照会項目", DisplayMember = "Label", ValueMember = "Key" });
        routes.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Value", HeaderText = "この値のとき", DisplayMember = "Label", ValueMember = "Key" });
        routes.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "NextPageId", HeaderText = "表示するページ", DisplayMember = "Label", ValueMember = "Key", FillWeight = 160 });
        routeLayout.Controls.Add(routes, 0, 1); var routeCommands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        AddButton(routeCommands, "条件を上へ", () => MoveRoute(-1)); AddButton(routeCommands, "条件を下へ", () => MoveRoute(1)); routeLayout.Controls.Add(routeCommands, 0, 2);
        routeTab.Controls.Add(routeLayout); tabs.TabPages.Add(routeTab);
        var welcomeTab = new TabPage("開始画面"); var welcomeLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(16) };
        welcomeLayout.ColumnStyles.Add(new(SizeType.Absolute, 160)); welcomeLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        int row = 0; foreach (var pair in new (string Label, Control Input)[] { ("タイトル", welcomeTitle), ("説明文", welcomeMessage), ("開始ボタン", welcomeButton), ("最初に表示するページ", first) })
        { welcomeLayout.RowStyles.Add(new(SizeType.Absolute, pair.Input == welcomeMessage ? 100 : 44)); welcomeLayout.Controls.Add(new Label { Text = pair.Label, AutoSize = true }, 0, row); welcomeLayout.Controls.Add(pair.Input, 1, row++); }
        welcomeTab.Controls.Add(welcomeLayout); tabs.TabPages.Add(welcomeTab);
        split.Panel2.Controls.Add(tabs); root.Controls.Add(split, 0, 1);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 12, 0, 0) };
        AddButton(commands, "設定をチェック", () => { Commit(); Flow.Validate(); status.Text = "接続・必要な回答・分岐を確認しました。"; });
        AddButton(commands, "患者画面を試す", () => { Commit(); Flow.Validate(); using var preview = new KioskFlowPreviewForm(Flow); preview.ShowDialog(this); });
        AddButton(commands, "詳細設定", () => { Commit(); using var advanced = new KioskFlowAdvancedEditorForm(Flow); if (advanced.ShowDialog(this) == DialogResult.OK) { Flow = advanced.Flow; RegisterLegacyFields(); LoadWelcome(); Reload(Flow.Pages[0]); } });
        AddButton(commands, "適用して閉じる", () => { Commit(); Flow.Validate(); DialogResult = DialogResult.OK; Close(); });
        AddButton(commands, "キャンセル", () => { DialogResult = DialogResult.Cancel; Close(); }); commands.Controls.Add(status); root.Controls.Add(commands, 0, 2); Controls.Add(root);
        pages.SelectedIndexChanged += (_, _) => { if (!loading) { Commit(); LoadPage(pages.SelectedItem as KioskFlowPage); } };
        routes.CurrentCellDirtyStateChanged += (_, _) => { if (routes.IsCurrentCellDirty) routes.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        routes.CellValueChanged += (_, e) => { if (!loading && e.RowIndex >= 0 && e.ColumnIndex == 0) ConfigureRouteRow(routes.Rows[e.RowIndex]); };
        routes.DataError += (_, e) => { e.ThrowException = false; status.Text = "条件の項目・値・ページを選択してください。"; };
        LoadWelcome();
        Reload(Flow.Pages[0]);
    }
    private void Try(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を確認してください"); } }
    private void AddButton(Control parent, string text, Action action) { var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 3, 6, 3) }; b.Click += (_, _) => Try(action); parent.Controls.Add(b); }
    private void RegisterLegacyFields()
    { foreach (var p in Flow.Pages.Where(p => p.Kind == KioskPageKind.Choice)) if (!Flow.Fields.Any(f => f.Key == p.Variable)) Flow.Fields.Add(new() { Key = p.Variable, Label = p.Title }); }
    private void LoadWelcome() { welcomeTitle.Text = Flow.StartTitle; welcomeMessage.Text = Flow.StartMessage; welcomeButton.Text = Flow.StartButton; }
    private void Commit()
    {
        if (loading) return;
        choices.EndEdit(); routes.EndEdit();
        foreach (var grid in new[] { choices, routes }) if (grid.DataSource is not null) BindingContext?[grid.DataSource]?.EndCurrentEdit();
        if (current is { } p)
        {
            p.Title = title.Text; p.Message = message.Text; p.ButtonLabel = buttonText.Text;
            if (p.Kind == KioskPageKind.Choice && field.SelectedValue is string key) p.Variable = key;
            if (p.Kind == KioskPageKind.Choice && p.Variable.StartsWith("answer_", StringComparison.Ordinal) && Flow.Pages.Count(x => x.Variable == p.Variable) == 1 &&
                Flow.Fields.FirstOrDefault(f => f.Key == p.Variable) is { } definition) definition.Label = p.Title;
            if (p.Kind != KioskPageKind.Guidance && next.SelectedValue is string target) p.NextPageId = target;
            if (p.Kind == KioskPageKind.Guidance) p.NextStep = finish.SelectedValue as string == "finish" ? KioskNextStep.Finish : KioskNextStep.StaffHelp;
            foreach (var c in p.Choices) { if (string.IsNullOrEmpty(c.Id)) c.Id = "option_" + Guid.NewGuid().ToString("N")[..8]; if (string.IsNullOrEmpty(c.Value)) c.Value = c.Id; }
        }
        Flow.StartTitle = welcomeTitle.Text; Flow.StartMessage = welcomeMessage.Text; Flow.StartButton = welcomeButton.Text;
        if (first.SelectedValue is string start) Flow.FirstPageId = start;
    }
    private void Reload(KioskFlowPage select)
    {
        loading = true; pages.Items.Clear(); foreach (var p in Flow.Pages) pages.Items.Add(p); pages.SelectedItem = select;
        Bind(first, PagePicks().Skip(1), Flow.FirstPageId); loading = false; LoadPage(select);
    }
    private void LoadPage(KioskFlowPage? p)
    {
        if (p is null) return; loading = true; current = p;
        kind.Text = KindName(p.Kind); title.Text = p.Title; message.Text = p.Message; buttonText.Text = p.ButtonLabel;
        field.Enabled = choices.Enabled = p.Kind == KioskPageKind.Choice; buttonText.Enabled = p.Kind is KioskPageKind.Card or KioskPageKind.Birthday;
        next.Enabled = routes.Enabled = p.Kind != KioskPageKind.Guidance; finish.Enabled = p.Kind == KioskPageKind.Guidance;
        Bind(field, FieldPicks(false), p.Variable); Bind(next, PagePicks(p.Kind == KioskPageKind.Lookup).Where(x => x.Key != p.Id), p.NextPageId);
        finish.SelectedValue = p.NextStep == KioskNextStep.Finish ? "finish" : "staff";
        choices.DataSource = new BindingList<KioskFlowChoice>(p.Choices);
        ((DataGridViewComboBoxColumn)routes.Columns[0]).DataSource = new[] { new Pick("", "（選択）") }.Concat(FieldPicks(true)).ToList();
        ((DataGridViewComboBoxColumn)routes.Columns[1]).DataSource = new List<Pick> { new("", "（選択）") }.Concat(p.Routes.Select(r => new Pick(r.Value, r.Value))).DistinctBy(x => x.Key).ToList();
        ((DataGridViewComboBoxColumn)routes.Columns[2]).DataSource = PagePicks(p.Kind == KioskPageKind.Lookup).Where(x => x.Key != p.Id).ToList();
        routes.DataSource = new BindingList<KioskFlowRoute>(p.Routes); foreach (DataGridViewRow r in routes.Rows) ConfigureRouteRow(r); loading = false;
    }
    private void ConfigureRouteRow(DataGridViewRow row)
    {
        if (row.DataBoundItem is not KioskFlowRoute route) return;
        var values = route.Variable switch
        {
            "hasFever" or "saysReserved" or "hasChart" or "actualReservation" => new List<Pick> { new("true", "あり"), new("false", "なし") },
            "category" => Enum.GetValues<ReceptionCategory>().Where(c => c is not (ReceptionCategory.Pending or ReceptionCategory.NeedsReview)).Select(c => new Pick(c.ToString(), ReceptionClassification.Label(c))).ToList(),
            "month" or "day" => Enumerable.Range(1, route.Variable == "month" ? 12 : 31).Select(n => new Pick(n.ToString(), n.ToString())).ToList(),
            _ => Flow.Pages.Where(p => p.Kind == KioskPageKind.Choice && p.Variable == route.Variable).SelectMany(p => p.Choices).Select(c => new Pick(c.Value, c.Label)).DistinctBy(v => v.Key).ToList()
        };
        values.Insert(0, new("", "（選択）")); if (!values.Any(v => v.Key == route.Value)) values.Add(new(route.Value, route.Value));
        ((DataGridViewComboBoxCell)row.Cells[1]).DataSource = values;
    }
    private void MoveRoute(int offset)
    {
        Commit(); if (current is null || routes.CurrentRow?.DataBoundItem is not KioskFlowRoute route) return;
        int index = current.Routes.IndexOf(route), target = index + offset; if (target < 0 || target >= current.Routes.Count) return;
        current.Routes.RemoveAt(index); current.Routes.Insert(target, route); LoadPage(current); routes.CurrentCell = routes.Rows[target].Cells[0];
    }
    private void AddField()
    {
        if (current?.Kind != KioskPageKind.Choice) throw new ArgumentException("質問ページを選んでから保存項目を追加してください。");
        using var dialog = new Form { Text = "回答の保存項目を追加", Size = new Size(420, 180), StartPosition = FormStartPosition.CenterParent };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        panel.Controls.Add(new Label { Text = "項目の名前（例：ワクチンの種類）", AutoSize = true }); var name = new TextBox { Width = 360 }; panel.Controls.Add(name);
        var ok = new Button { Text = "追加", DialogResult = DialogResult.OK }; panel.Controls.Add(ok); dialog.Controls.Add(panel); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Length > 100) throw new ArgumentException("項目の名前は1～100文字で指定してください。");
        Commit(); var key = "answer_" + Guid.NewGuid().ToString("N")[..8]; Flow.Fields.Add(new() { Key = key, Label = name.Text }); current.Variable = key; LoadPage(current);
    }
    private void AddQuestion(bool clinic)
    {
        Commit(); var anchor = current;
        if (anchor is null || anchor.Kind is KioskPageKind.Lookup or KioskPageKind.Guidance) throw new ArgumentException("質問を追加する位置として、照会前の質問・説明を選んでください。");
        if (clinic && Flow.Pages.Any(p => p.Kind == KioskPageKind.Choice && p.Variable == "clinicClass")) throw new ArgumentException("外来区分の質問は既にあります。");
        var id = "question_" + Guid.NewGuid().ToString("N")[..8]; string key = clinic ? "clinicClass" : "answer_" + Guid.NewGuid().ToString("N")[..8];
        if (!Flow.Fields.Any(f => f.Key == key)) Flow.Fields.Add(new() { Key = key, Label = "追加の質問" });
        var page = new KioskFlowPage { Id = id, Kind = KioskPageKind.Choice, Title = clinic ? "本日はどの外来を受診されますか？" : "追加の質問", Variable = key, NextPageId = anchor.NextPageId,
            Choices = clinic ? [new() { Id = "general", Label = "一般診療", Value = "general" }, new() { Id = "vaccination", Label = "予防接種", Value = "vaccination" }, new() { Id = "special", Label = "特殊外来", Value = "special" }]
                : [new() { Id = "yes", Label = "はい", Value = "yes" }, new() { Id = "no", Label = "いいえ", Value = "no" }] };
        anchor.NextPageId = page.Id; Flow.Pages.Insert(Flow.Pages.IndexOf(anchor) + 1, page); Reload(page);
    }
    private void AddGuidance()
    { Commit(); var page = new KioskFlowPage { Id = "guidance_" + Guid.NewGuid().ToString("N")[..8], Kind = KioskPageKind.Guidance, Title = "追加の案内", Message = "受付へお声掛けください。" }; Flow.Pages.Add(page); Reload(page); }
    private void RemovePage()
    {
        Commit(); if (current is not { } p) return;
        if (p.Kind is KioskPageKind.Card or KioskPageKind.Birthday or KioskPageKind.Lookup || p.Variable is "hasFever" or "saysReserved") throw new ArgumentException("資格確認・本人照合・発熱・予約の必須ページは削除できません。");
        bool referenced = Flow.FirstPageId == p.Id || Flow.Pages.Any(x => x.NextPageId == p.Id || x.Routes.Any(r => r.NextPageId == p.Id));
        if (p.Kind == KioskPageKind.Guidance && referenced) throw new ArgumentException("この案内を使用している表示条件・次ページを変更してから削除してください。");
        if (Flow.Pages.Any(x => x != p && x.Routes.Any(r => r.Variable == p.Variable))) throw new ArgumentException("この質問の回答を使う表示条件を先に変更してください。");
        foreach (var x in Flow.Pages) { if (x.NextPageId == p.Id) x.NextPageId = p.NextPageId; foreach (var route in x.Routes.Where(r => r.NextPageId == p.Id)) route.NextPageId = p.NextPageId; }
        if (Flow.FirstPageId == p.Id) Flow.FirstPageId = p.NextPageId;
        Flow.Pages.Remove(p); current = null; Reload(Flow.Pages[0]);
    }
}
