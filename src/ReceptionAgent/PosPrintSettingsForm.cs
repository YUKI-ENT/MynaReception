using System.ComponentModel;
using System.Drawing.Printing;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal sealed class PosPrintSettingsForm : ReceptionAgentForm
{
    private sealed class PrintLine
    {
        public PrintLine() { }
        public string Text { get; set; } = "";
        public string FontName { get; set; } = "Yu Gothic";
        public float FontSizePt { get; set; } = 11;
        public bool Bold { get; set; }
        public float SpaceAfterMm { get; set; } = 1.27f;
        public PosTextAlignment Alignment { get; set; } = PosTextAlignment.Center;
        public bool Boxed { get; set; }
        public PosLineStyle Style() => new() { FontName = FontName, FontSizePt = FontSizePt, Bold = Bold, SpaceAfterMm = SpaceAfterMm, Alignment = Alignment, Boxed = Boxed };
    }
    internal PosPrintOptions Options { get; private set; }
    public PosPrintSettingsForm(PosPrintOptions current)
    {
        Options = current;
        Text = "POSプリンタ・印刷文面"; ClientSize = new(1000, 760); MinimumSize = new(860, 650); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 12 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 12; i++) layout.RowStyles.Add(new(i == 9 ? SizeType.Percent : SizeType.AutoSize, i == 9 ? 100 : 0));
        void Add(Control c) { c.Anchor = AnchorStyles.Left | AnchorStyles.Right; layout.Controls.Add(c); }
        var enabled = new CheckBox { Text = "POSプリンタ出力を有効にする（患者用・スタッフ用を各1枚）", AutoSize = true, Checked = current.Enabled }; Add(enabled);
        Add(new Label { Text = "Windowsプリンタ名（既定のプリンタではなく、ここで指定したプリンタを使用）", AutoSize = true });
        var printer = new ComboBox { Text = current.PrinterName, DropDownStyle = ComboBoxStyle.DropDown, AccessibleName = "POSプリンタ" };
        printer.Items.AddRange(PrinterSettings.InstalledPrinters.Cast<string>().ToArray()); Add(printer);
        Add(new Label { Text = "用紙幅（プリンタ側の用紙幅設定も合わせてください）", AutoSize = true });
        var width = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; width.Items.AddRange(["80mm", "58mm"]); width.SelectedIndex = current.PaperWidthMm == 58 ? 1 : 0; Add(width);
        Add(new Label { Text = "医院名", AutoSize = true }); var clinic = new TextBox { Text = current.ClinicName }; Add(clinic);
        Add(new Label { Text = "各行の文面・フォント・サイズ・太字・余白・配置を編集できます。最下行で追加、行選択＋Deleteで削除。\n差し込み項目: {医院名} {日付時刻} {予約番号} {番号} {名前} {フリガナ} {診察券番号}\n{名前}・{フリガナ}はXML優先。XML由来を指定する場合: {マイナ氏名} {マイナフリガナ}\nサイズはpt、余白はmm。折り返し内の行間はフォントに従います。{番号}は予約番号です。", AutoSize = true });
        var matchFonts = new CheckBox { Text = "スタッフ用のフォント・文字サイズを患者用に合わせる", AutoSize = true, Checked = current.MatchStaffFontsToPatient }; Add(matchFonts);
        var tabs = new TabControl { Dock = DockStyle.Fill, MinimumSize = new(0, 180) }; layout.Controls.Add(tabs);
        (BindingList<PrintLine> Lines, DataGridView Grid) Editor(string title, bool staff)
        {
            string template = staff ? current.StaffTemplate : current.PatientTemplate;
            var lines = new BindingList<PrintLine>(template.Replace("\r\n", "\n").Split('\n').Select((text, index) =>
            {
                var style = current.StyleFor(staff, index, text);
                return new PrintLine { Text = text, FontName = style.FontName, FontSizePt = style.FontSizePt, Bold = style.Bold, SpaceAfterMm = style.SpaceAfterMm, Alignment = style.Alignment, Boxed = style.Boxed == true };
            }).ToList());
            var page = new TabPage(title);
            var grid = new DataGridView { Dock = DockStyle.Fill, DataSource = lines, AutoGenerateColumns = false,
                AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersWidth = 32, AccessibleName = title + "の印刷項目" };
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Text", HeaderText = "文面・差し込み項目", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180, SortMode = DataGridViewColumnSortMode.NotSortable });
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            string[] names = fonts.Families.Select(f => f.Name).Concat(lines.Select(l => l.FontName))
                .Concat(current.PatientStyles.Values.Concat(current.StaffStyles.Values).Select(s => s.FontName)).Append("Yu Gothic").Distinct().Order().ToArray();
            grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "FontName", HeaderText = "フォント", Width = 160, DataSource = names, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "FontSizePt", HeaderText = "サイズ(pt)", Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Bold", HeaderText = "太字", Width = 50, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "SpaceAfterMm", HeaderText = "後の余白(mm)", Width = 110, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Alignment", HeaderText = "配置", Width = 95,
                DataSource = new[] { new { Value = PosTextAlignment.Left, Label = "左寄せ" }, new { Value = PosTextAlignment.Center, Label = "中央寄せ" }, new { Value = PosTextAlignment.Right, Label = "右寄せ" } },
                ValueMember = "Value", DisplayMember = "Label", SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Boxed", HeaderText = "枠囲い", Width = 65, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.DataError += (_, e) => { e.ThrowException = false; MessageBox.Show(this, "文字サイズと余白には数値を入力してください。フォントは一覧から選択してください。", "印刷項目を確認してください"); };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            void Move(int delta)
            {
                if (!grid.EndEdit()) return;
                int index = grid.CurrentRow?.Index ?? -1, next = index + delta;
                if (index < 0 || index >= lines.Count || next < 0 || next >= lines.Count) return;
                var item = lines[index]; lines.RemoveAt(index); lines.Insert(next, item); grid.CurrentCell = grid.Rows[next].Cells[0];
            }
            var up = new Button { Text = "行を上へ", AutoSize = true }; up.Click += (_, _) => Move(-1);
            var down = new Button { Text = "行を下へ", AutoSize = true }; down.Click += (_, _) => Move(1);
            actions.Controls.AddRange([up, down]); page.Controls.Add(grid); page.Controls.Add(actions); tabs.TabPages.Add(page);
            return (lines, grid);
        }
        var patient = Editor("患者用", false); var staff = Editor("スタッフ用", true);
        void MatchFonts()
        {
            if (!matchFonts.Checked)
            {
                staff.Grid.Columns[1].ReadOnly = staff.Grid.Columns[2].ReadOnly = false;
                return;
            }
            if (!patient.Grid.EndEdit() || !staff.Grid.EndEdit()) return;
            var draft = new PosPrintOptions { PaperWidthMm = width.SelectedIndex == 1 ? 58 : 80,
                PatientTemplate = string.Join("\n", patient.Lines.Select(l => l.Text)),
                PatientStyles = patient.Lines.Select((l, i) => (i, style: l.Style())).ToDictionary(p => p.i, p => p.style) };
            for (int i = 0; i < staff.Lines.Count; i++)
            {
                var style = draft.StyleFor(true, i, staff.Lines[i].Text);
                staff.Lines[i].FontName = style.FontName; staff.Lines[i].FontSizePt = style.FontSizePt;
            }
            staff.Lines.ResetBindings();
            staff.Grid.Columns[1].ReadOnly = staff.Grid.Columns[2].ReadOnly = true;
        }
        matchFonts.CheckedChanged += (_, _) => MatchFonts();
        tabs.SelectedIndexChanged += (_, _) => MatchFonts();
        MatchFonts();
        Add(new Label { Text = "スタッフ案内も「文面」欄で編集できます。「枠囲い」をONにすれば、変更した文言も枠で囲みます。\n文字サイズ: 6～48pt／余白: 0～20mm。各用紙を別ページで送信。ドライバでページごとの自動カットを有効にしてください。", AutoSize = true });
        PosPrintOptions Read()
        {
            MatchFonts();
            if (!patient.Grid.EndEdit() || !staff.Grid.EndEdit()) throw new ArgumentException("編集中の値を確認してください。");
            BindingContext?[patient.Lines]?.EndCurrentEdit(); BindingContext?[staff.Lines]?.EndCurrentEdit();
            return new() { Enabled = enabled.Checked, PrinterName = printer.Text.Trim(), PaperWidthMm = width.SelectedIndex == 1 ? 58 : 80,
                ClinicName = clinic.Text, MatchStaffFontsToPatient = matchFonts.Checked, PatientTemplate = string.Join("\n", patient.Lines.Select(l => l.Text)), StaffTemplate = string.Join("\n", staff.Lines.Select(l => l.Text)),
                PatientStyles = patient.Lines.Select((l, i) => (i, style: l.Style())).ToDictionary(p => p.i, p => p.style),
                StaffStyles = staff.Lines.Select((l, i) => (i, style: l.Style())).ToDictionary(p => p.i, p => p.style) };
        }
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var preview = new Button { Text = "見本・テスト印刷…", AutoSize = true };
        preview.Click += (_, _) =>
        {
            try { var options = Read(); options.Validate(); using var dialog = new KioskReceiptForm(new("sample", "0012345", "受付 太郎", "ウケツケ タロウ", "37", DateTimeOffset.Now) { XmlName = "受付 太郎", XmlNameKana = "ウケツケ タロウ" }, true, options); dialog.ShowDialog(this); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "印刷設定を確認してください"); }
        };
        var save = new Button { Text = "適用", AutoSize = true };
        save.Click += (_, _) => { try { var options = Read(); options.Validate(); Options = options; DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([preview, save, cancel]); Add(buttons); Controls.Add(layout); CancelButton = cancel;
    }
}
