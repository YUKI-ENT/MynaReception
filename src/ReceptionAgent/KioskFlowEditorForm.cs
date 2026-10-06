using System.ComponentModel;
using System.Text.Json;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal sealed class KioskFlowAdvancedEditorForm : Form
{
    public KioskFlow Flow { get; private set; }
    public KioskFlowAdvancedEditorForm(KioskFlow source)
    {
        Flow = JsonSerializer.Deserialize<KioskFlow>(JsonSerializer.Serialize(source))!;
        Text = "Kiosk画面フローの編集"; Size = new Size(1300, 900); MinimumSize = new Size(1000, 720); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 55)); layout.RowStyles.Add(new(SizeType.Percent, 45));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var intro = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        TextBox Field(string label, string value, int width)
        {
            intro.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(5, 8, 3, 0) });
            var field = new TextBox { Text = value, Width = width }; intro.Controls.Add(field); return field;
        }
        var title = Field("開始タイトル", Flow.StartTitle, 180);
        var message = Field("開始本文", Flow.StartMessage, 330);
        var startButton = Field("開始ボタン", Flow.StartButton, 180);
        var first = Field("最初のページID", Flow.FirstPageId, 100);
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(1200, 0), Text =
            "Card＝資格確認／Birthday＝誕生月日／Choice＝質問／Lookup＝照会／Guidance＝案内。ページIDで接続します。\n条件は上から評価し、変数の値が一致した最初の行へ進みます。一致しなければ通常の次ページへ。Finishは再診・予約ありだけに適用します。" }, 0, 1);
        DataGridView Grid() => new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = true,
            AllowUserToDeleteRows = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersWidth = 28 };
        void Column(DataGridView grid, string property, string label, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn
            { DataPropertyName = property, HeaderText = label, Width = width });
        var pages = Grid(); pages.DataSource = new BindingList<KioskFlowPage>(Flow.Pages);
        Column(pages, "Id", "ページID", 110);
        pages.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Kind", HeaderText = "種類", DataSource = Enum.GetValues<KioskPageKind>(), Width = 100 });
        Column(pages, "Title", "タイトル", 200); Column(pages, "Message", "本文", 290);
        Column(pages, "ButtonLabel", "次へボタンの文面", 170); Column(pages, "Variable", "回答で設定する変数", 140);
        Column(pages, "NextPageId", "通常の次ページID", 130);
        pages.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "NextStep", HeaderText = "案内の動作", DataSource = Enum.GetValues<KioskNextStep>(), Width = 100 });
        layout.Controls.Add(pages, 0, 2);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var choices = Grid(); Column(choices, "Id", "選択肢ID", 130); Column(choices, "Label", "ボタンの文面", 400); Column(choices, "Value", "変数に設定する値", 200);
        var routes = Grid(); Column(routes, "Variable", "条件の変数", 200); Column(routes, "Value", "一致する値", 250); Column(routes, "NextPageId", "条件一致時の次ページID", 250);
        var choicesTab = new TabPage("選択ページの回答ボタン"); choicesTab.Controls.Add(choices); tabs.TabPages.Add(choicesTab);
        var routesTab = new TabPage("選択ページの分岐条件（上から優先）"); routesTab.Controls.Add(routes); tabs.TabPages.Add(routesTab);
        KioskFlowPage? selectedPage = null;
        void SelectPage()
        {
            if (pages.CurrentRow?.DataBoundItem is not KioskFlowPage page) return;
            choices.Enabled = page.Kind == KioskPageKind.Choice;
            routes.Enabled = page.Kind != KioskPageKind.Guidance;
            if (ReferenceEquals(page, selectedPage)) return;
            foreach (var grid in new[] { choices, routes }) { grid.EndEdit(); if (grid.DataSource is not null) BindingContext?[grid.DataSource]?.EndCurrentEdit(); }
            selectedPage = page;
            choices.DataSource = new BindingList<KioskFlowChoice>(page.Choices);
            routes.DataSource = new BindingList<KioskFlowRoute>(page.Routes);
            choices.Enabled = page.Kind == KioskPageKind.Choice;
            routes.Enabled = page.Kind != KioskPageKind.Guidance;
        }
        pages.CurrentCellChanged += (_, _) => SelectPage();
        pages.CellValueChanged += (_, _) => SelectPage();
        layout.Controls.Add(tabs, 0, 3);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(1200, 0), Text =
            "変数：month/day＝誕生月日、saysReserved＝予約回答、hasFever＝発熱回答（true/false）。任意の質問変数も追加できます。\n照会結果専用：category＝ReturningWithReservation / ReturningWithoutReservation / NewWithReservation / NewWithoutReservation、hasChart / actualReservation＝true/false。\n行の追加は末尾の空行、削除は行を選んでDelete。分岐の順序変更は条件行を選んで↑↓ボタン。" }, 0, 4);
        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        void Commit()
        {
            foreach (var grid in new[] { pages, choices, routes }) { grid.EndEdit(); if (grid.DataSource is not null) BindingContext?[grid.DataSource]?.EndCurrentEdit(); }
            Flow.StartTitle = title.Text; Flow.StartMessage = message.Text; Flow.StartButton = startButton.Text; Flow.FirstPageId = first.Text;
            Flow.Validate();
        }
        Button Command(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "フローを確認してください"); } };
            commands.Controls.Add(button); return button;
        }
        void Move(int offset)
        {
            routes.EndEdit();
            if (routes.DataSource is not BindingList<KioskFlowRoute> list || routes.CurrentRow?.DataBoundItem is not KioskFlowRoute route) return;
            int from = list.IndexOf(route), to = from + offset;
            if (to < 0 || to >= list.Count) return;
            list.RemoveAt(from); list.Insert(to, route); routes.CurrentCell = routes.Rows[to].Cells[0];
        }
        Command("条件↑", () => Move(-1)); Command("条件↓", () => Move(1));
        Command("検証", () => { Commit(); MessageBox.Show(this, "文面・接続・循環・必要回答の検証に成功しました。"); });
        Command("フローを試す（架空の回答）", () => { Commit(); using var preview = new KioskFlowPreviewForm(Flow); preview.ShowDialog(this); });
        Command("適用して閉じる", () => { Commit(); DialogResult = DialogResult.OK; Close(); });
        var cancel = Command("キャンセル", () => { DialogResult = DialogResult.Cancel; Close(); }); CancelButton = cancel;
        layout.Controls.Add(commands, 0, 5); Controls.Add(layout); Shown += (_, _) => SelectPage();
    }
}

internal sealed class KioskFlowPreviewForm : Form
{
    public KioskFlowPreviewForm(KioskFlow flow)
    {
        Text = "画面フローのプレビュー — 実際の受付や印刷は行いません"; Size = new Size(800, 680); StartPosition = FormStartPosition.CenterParent;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24) };
        var variables = new Dictionary<string, string>();
        void Render(string id)
        {
            layout.Controls.Clear(); var page = flow.Page(id);
            layout.Controls.Add(new Label { Text = page.Title, Font = new Font(Font.FontFamily, 20), AutoSize = true, MaximumSize = new Size(700, 0) });
            layout.Controls.Add(new Label { Text = page.Message, AutoSize = true, MaximumSize = new Size(700, 0), Padding = new Padding(0, 12, 0, 12) });
            void Button(string label, Action action) { var b = new Button { Text = label, AutoSize = true, MinimumSize = new Size(160, 50) }; b.Click += (_, _) => action(); layout.Controls.Add(b); }
            void Next() => Render(flow.Next(page, variables));
            if (page.Kind == KioskPageKind.Birthday)
            {
                var month = new NumericUpDown { Minimum = 1, Maximum = 12 }; var day = new NumericUpDown { Minimum = 1, Maximum = 31 };
                layout.Controls.Add(new Label { Text = "誕生月／日（プレビュー用）", AutoSize = true }); layout.Controls.Add(month); layout.Controls.Add(day);
                Button(page.ButtonLabel, () => { variables["month"] = month.Value.ToString(); variables["day"] = day.Value.ToString(); Next(); });
            }
            else if (page.Kind == KioskPageKind.Choice) foreach (var choice in page.Choices)
                Button(choice.Label, () => { variables[page.Variable] = choice.Value; Next(); });
            else if (page.Kind == KioskPageKind.Lookup)
            {
                layout.Controls.Add(new Label { Text = "架空の照会結果を選んで分岐を確認", AutoSize = true });
                foreach (var category in Enum.GetValues<Reception.ReceptionCategory>().Where(c => c is not (Reception.ReceptionCategory.Pending or Reception.ReceptionCategory.NeedsReview)))
                    Button(Reception.ReceptionClassification.Label(category), () => {
                        variables["category"] = category.ToString(); variables["hasChart"] = category is Reception.ReceptionCategory.ReturningWithReservation or Reception.ReceptionCategory.ReturningWithoutReservation ? "true" : "false";
                        variables["actualReservation"] = category is Reception.ReceptionCategory.ReturningWithReservation or Reception.ReceptionCategory.NewWithReservation ? "true" : "false"; Next(); });
            }
            else if (page.Kind == KioskPageKind.Card) Button(page.ButtonLabel, Next);
            else layout.Controls.Add(new Label { Text = "設定した案内の動作: " + page.NextStep, AutoSize = true });
            layout.Controls.Add(new Label { Text = "実機では本人照合・発熱・予約不一致の職員確認が優先されます。", AutoSize = true });
            layout.Controls.Add(new Label { Text = string.Join(" / ", variables.Select(v => v.Key + "=" + v.Value)), AutoSize = true, MaximumSize = new Size(700, 0) });
            Button("最初から試す", () => { variables.Clear(); Render(flow.FirstPageId); });
        }
        Controls.Add(layout); Render(flow.FirstPageId);
    }
}

