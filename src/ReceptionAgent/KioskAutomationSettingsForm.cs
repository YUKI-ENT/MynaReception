using System.ComponentModel;
using System.Drawing.Printing;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal sealed class KioskAutomationSettingsForm : ReceptionAgentForm
{
    private sealed class AnswerCondition
    {
        public AnswerCondition() { }
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }
    public KioskAutomationSettingsForm(string dataDirectory)
    {
        Text = "Kiosk自動発券設定"; ClientSize = new(780, 640); StartPosition = FormStartPosition.CenterParent;
        var settings = KioskOptions.Load(dataDirectory);
        var current = settings.Automation;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(16) };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(720, 0),
            Text = "対象はKioskで本人・カルテ番号・予約を確認した当日の再診・予約ありです。発熱なし・予約ありの回答が一致し、フローが受付完了案内（Finish）へ進む場合に発券します。\r\n来院確認・連携はメイン画面上部のチェックで指定します。Kioskの回答前でも予約確定後に実行します。" });
        var autoPrint = new CheckBox { Text = "受付票を自動発券（患者用・スタッフ用を各1枚／80mm・58mm）", Checked = current.AutoPrint, AutoSize = true };
        layout.Controls.Add(autoPrint);
        layout.Controls.Add(new Label { Text = "自動発券プリンタ（POS-80Cなど）。用紙幅・文面はReceptionAgentのPOSプリンタ設定を使用。", AutoSize = true });
        var printer = new ComboBox { Width = 700, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "自動発券プリンタ", Enabled = current.AutoPrint };
        printer.Items.AddRange(PrinterSettings.InstalledPrinters.Cast<string>().ToArray());
        if (current.PrinterName.Length > 0 && !printer.Items.Contains(current.PrinterName)) printer.Items.Add(current.PrinterName);
        if (current.PrinterName.Length > 0) printer.SelectedItem = current.PrinterName;
        else
        {
            var pos = AgentSettings.Load(dataDirectory).PosPrint;
            if (printer.Items.Contains(pos.PrinterName)) printer.SelectedItem = pos.PrinterName;
        }
        autoPrint.CheckedChanged += (_, _) => printer.Enabled = autoPrint.Checked;
        layout.Controls.Add(printer);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(720, 0), Text = "追加条件（すべて一致した受付のみ自動化）。空欄なら上記の基本条件だけで判定します。例: 外来区分＝一般診療。" });
        var conditions = new BindingList<AnswerCondition>(current.RequiredAnswers.Select(p => new AnswerCondition { Key = p.Key, Value = p.Value }).ToList());
        var grid = new DataGridView { Width = 720, Height = 215, AutoGenerateColumns = false, DataSource = conditions,
            AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersWidth = 28 };
        var questions = settings.Flow.Pages.Where(p => p.Kind == KioskPageKind.Choice).GroupBy(p => p.Variable)
            .Select(g => new { Key = g.Key, Label = settings.Flow.FieldLabel(g.Key) }).ToList();
        grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Key", HeaderText = "回答項目", Width = 300,
            DataSource = questions, ValueMember = "Key", DisplayMember = "Label" });
        var allChoices = settings.Flow.Pages.Where(p => p.Kind == KioskPageKind.Choice).SelectMany(p => p.Choices)
            .GroupBy(c => c.Value).Select(g => new { Value = g.Key, Label = g.First().Label }).ToList();
        grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Value", HeaderText = "一致する回答", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            DataSource = allChoices, ValueMember = "Value", DisplayMember = "Label" });
        void ChoicesForRow(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count || grid.Rows[rowIndex].IsNewRow) return;
            var row = grid.Rows[rowIndex];
            string key = row.Cells[0].Value as string ?? "";
            var values = settings.Flow.Pages.Where(p => p.Kind == KioskPageKind.Choice && p.Variable == key).SelectMany(p => p.Choices)
                .GroupBy(c => c.Value).Select(g => new { Value = g.Key, Label = g.First().Label }).ToList();
            ((DataGridViewComboBoxCell)row.Cells[1]).DataSource = values;
        }
        grid.DataBindingComplete += (_, _) => { foreach (DataGridViewRow row in grid.Rows) ChoicesForRow(row.Index); };
        grid.CellValueChanged += (_, e) => { if (e.ColumnIndex == 0) ChoicesForRow(e.RowIndex); };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.DataError += (_, e) => { e.ThrowException = false; };
        layout.Controls.Add(grid);
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new(720, 0),
            Text = "発券条件は来院確認・連携の条件には適用しません。来院確認・連携はメイン画面のチェックで別に指定します。\r\n発券設定は新しいKiosk受付から適用。印刷結果が不明な場合は自動再発券せず、職員が印刷キューと用紙を確認します。" });
        var result = new Label { AutoSize = true, MaximumSize = new(720, 0) };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var save = new Button { Text = "保存", AutoSize = true };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            try
            {
                grid.EndEdit(); BindingContext?[conditions]?.EndCurrentEdit();
                var rows = conditions.Where(p => p.Key.Length > 0 || p.Value.Length > 0).ToArray();
                if (rows.Select(p => p.Key).Distinct().Count() != rows.Length) throw new ArgumentException("同じ回答項目は1件だけ指定してください。");
                // Preserve unrelated flow edits made since this dialog was opened.
                var latest = KioskOptions.Load(dataDirectory);
                latest.Automation = new KioskAutomationOptions { AutoLink = false, AutoArrival = false,
                    AutoPrint = autoPrint.Checked, PrinterName = printer.SelectedItem as string ?? "",
                    RequiredAnswers = rows.ToDictionary(p => p.Key, p => p.Value) };
                latest.Save(dataDirectory); DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { result.Text = ex.Message; }
        };
        buttons.Controls.AddRange([save, cancel]); layout.Controls.Add(buttons); layout.Controls.Add(result);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
}
