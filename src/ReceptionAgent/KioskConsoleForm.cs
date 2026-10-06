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
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(1030, 0), Text = "上の4分類案内は旧API用です。通常の質問・案内は「質問・画面フローを編集」で編集します。StaffHelp＝職員案内／Finish＝案内表示終了（受付完了操作ではありません）。本人照合・発熱・予約不一致の職員確認が優先されます。" }, 0, 3);
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
                history.DataSource = sessions.List().Select(s => new { セッションID = s.Id, 状態 = s.State, 氏名 = s.ConfirmedName, 氏名カナ = s.Answers.NameKana,
                    誕生月日 = s.MonthDayAnswers is { } a ? $"{a.Month:00}/{a.Day:00}" : s.Answers.Birthdate,
                    発熱 = s.MonthDayAnswers is { } fever ? fever.HasFever == true ? "あり" : "なし" : "未回答",
                    予約回答 = s.MonthDayAnswers is { } reserved ? reserved.SaysReserved == true ? "あり" : "なし" : "未回答",
                    カルテ番号 = s.PatientId, 予約番号 = s.ReceptionNo, 回答日時 = s.InputCompletedAt?.LocalDateTime,
                    開始日時 = s.StartedAt.LocalDateTime, 分類 = Reception.ReceptionClassification.Label(s.Category), 表示 = s.Message,
                    外来区分 = s.Variables.TryGetValue("clinicClass", out var clinic) ? s.Options.Flow.ValueLabel("clinicClass", clinic) : "未回答", フローページ = s.FlowPageId,
                    回答変数 = string.Join(" / ", s.Variables.Select(v => s.Options.Flow.FieldLabel(v.Key) + "=" + s.Options.Flow.ValueLabel(v.Key, v.Value))), XML取込ID = s.CaptureId }).ToList();
                if (selectedId is not null) foreach (DataGridViewRow row in history.Rows)
                    if (row.Cells["セッションID"].Value as string == selectedId) { history.CurrentCell = row.Cells["状態"]; break; }
                server.Text = "Webサーバー: " + (serverUrl().Length > 0 ? serverUrl() : "起動していません");
            }
            catch (Exception ex) { result.Text = "セッション確認エラー: " + ex.Message; }
        }
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var save = new Button { Text = "案内設定を保存", AutoSize = true };
        var editFlow = new Button { Text = "質問・画面フローを編集", AutoSize = true };
        editFlow.Click += (_, _) =>
        {
            using var editor = new KioskFlowEditorForm(settings.Flow);
            if (editor.ShowDialog(this) == DialogResult.OK)
            { settings.Flow = editor.Flow; result.Text = "フローを適用しました。「案内設定を保存」で保存してください。"; }
        };
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
        var receipt = new Button { Text = "選択受付の受付済み証…", AutoSize = true };
        receipt.Click += (_, _) =>
        {
            try
            {
                if (history.CurrentRow?.Cells["セッションID"].Value is not string id) throw new InvalidOperationException("受付を選択してください。");
                var session = sessions.List().Single(s => s.Id == id);
                using var form = new KioskReceiptForm(sessions.Receipt(id, session.Device)); form.ShowDialog(this);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "受付済み証"); }
        };
        var receiptSample = new Button { Text = "受付済み証のテスト見本…", AutoSize = true };
        receiptSample.Click += (_, _) => { using var form = new KioskReceiptForm(new("sample", "12345", "受付 太郎", "ウケツケ タロウ", "37", DateTimeOffset.Now), true); form.ShowDialog(this); };
        commands.Controls.AddRange([editFlow, save, preview, open, cancel, receipt, receiptSample]); layout.Controls.Add(commands, 0, 5); layout.Controls.Add(result, 0, 6); Controls.Add(layout);
        Shown += (_, _) => { Reload(); timer.Start(); }; timer.Tick += (_, _) => Reload();
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
    }
}
