using System.ComponentModel;
using System.Diagnostics;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal sealed class KioskConsoleForm : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    public KioskConsoleForm(KioskSessions sessions, Func<string> serverUrl)
    {
        Text = "Kioskコンソール — 職員テスト"; Size = new Size(1100, 760); StartPosition = FormStartPosition.CenterParent;
        var settings = KioskOptions.Load(MainForm.DataDirectory);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(16) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Absolute, 230)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var server = new Label { AutoSize = true, Text = "Webサーバー: " + (serverUrl().Length > 0 ? serverUrl() : "起動していません") };
        layout.Controls.Add(server, 0, 0);
        var fields = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        NumericUpDown Number(string label, int value, int minimum, int maximum)
        {
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(8, 8, 4, 0) });
            var number = new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = value, Width = 85 }; fields.Controls.Add(number); return number;
        }
        var port = Number("ポート（次回起動時）", settings.Port, 1024, 65535);
        var faceTimeout = Number("XML待機秒", settings.FaceTimeoutSeconds, 15, 300);
        var lookupTimeout = Number("照会待機秒", settings.LookupTimeoutSeconds, 15, 300);
        layout.Controls.Add(fields, 0, 1);
        var rules = new DataGridView { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            DataSource = new BindingList<KioskRule>(settings.Rules) };
        rules.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Category", HeaderText = "受付分類", ReadOnly = true, Width = 220 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Title", HeaderText = "案内タイトル", Width = 200 });
        rules.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Message", HeaderText = "案内本文", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        rules.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "NextStep", HeaderText = "次の動作",
            DataSource = Enum.GetValues<KioskNextStep>().Select(value => new { Value = value, Text = value == KioskNextStep.StaffHelp ? "職員案内" : "案内表示終了" }).ToList(),
            ValueMember = "Value", DisplayMember = "Text", Width = 120 });
        rules.CellFormatting += (_, e) => { if (e.ColumnIndex == 0 && e.Value is Reception.ReceptionCategory category) { e.Value = Reception.ReceptionClassification.Label(category); e.FormattingApplied = true; } };
        layout.Controls.Add(rules, 0, 2);
        layout.Controls.Add(new Label { AutoSize = true, Text = "StaffHelp＝職員案内／Finish＝案内表示を終了可能（受付完了操作ではありません）。受診歴とカルテの不一致・複数候補等は職員案内を優先します。" }, 0, 3);
        var history = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells };
        layout.Controls.Add(history, 0, 4);
        var result = new Label { AutoSize = true };
        void Reload()
        {
            try
            {
                var rows = sessions.List();
                string? selectedId = history.CurrentRow?.Cells["セッションID"].Value as string;
                foreach (var s in rows.Where(s => s.State is KioskSessionState.WaitingForXml or KioskSessionState.LookingUp)) _ = sessions.Get(s.Id, s.Device);
                history.DataSource = sessions.List().Select(s => new { セッションID = s.Id, 状態 = s.State, 氏名カナ = s.Answers.NameKana,
                    生年月日 = s.Answers.Birthdate, 開始日時 = s.StartedAt.LocalDateTime, 分類 = Reception.ReceptionClassification.Label(s.Category), 表示 = s.Message, XML取込ID = s.CaptureId }).ToList();
                if (selectedId is not null) foreach (DataGridViewRow row in history.Rows)
                    if (row.Cells["セッションID"].Value as string == selectedId) { history.CurrentCell = row.Cells["状態"]; break; }
                server.Text = "Webサーバー: " + (serverUrl().Length > 0 ? serverUrl() : "起動していません");
            }
            catch (Exception ex) { result.Text = "セッション確認エラー: " + ex.Message; }
        }
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "案内設定を保存", AutoSize = true };
        save.Click += (_, _) =>
        {
            try { rules.EndEdit(); settings.Port = (int)port.Value; settings.FaceTimeoutSeconds = (int)faceTimeout.Value; settings.LookupTimeoutSeconds = (int)lookupTimeout.Value;
                settings.Save(MainForm.DataDirectory); result.Text = "保存しました。案内と待機時間は新しいセッションへ適用します。ポート変更はAgent再起動で反映します。"; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を確認してください"); }
        };
        var open = new Button { Text = "Kiosk画面を開く", AutoSize = true };
        open.Click += (_, _) => { try { if (serverUrl().Length == 0) throw new InvalidOperationException("Webサーバーが起動していません。");
            Process.Start(new ProcessStartInfo(serverUrl()) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        var preview = new Button { Text = "選択案内をプレビュー", AutoSize = true };
        preview.Click += (_, _) => { rules.EndEdit(); if (rules.CurrentRow?.DataBoundItem is KioskRule rule) MessageBox.Show(this, rule.Message + "\r\n\r\n動作: " + rule.NextStep, rule.Title); };
        var cancel = new Button { Text = "選択セッションを中止", AutoSize = true };
        cancel.Click += (_, _) => { try { if (history.CurrentRow?.Cells["セッションID"].Value is string id) {
            var session = sessions.List().Single(s => s.Id == id); sessions.End(id, session.Device, true); Reload(); } } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        commands.Controls.AddRange([save, preview, open, cancel]); layout.Controls.Add(commands, 0, 5); layout.Controls.Add(result, 0, 6); Controls.Add(layout);
        Shown += (_, _) => { Reload(); timer.Start(); }; timer.Tick += (_, _) => Reload();
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
    }
}
