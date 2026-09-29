namespace ReceptionAgent;

public sealed class AgentSettingsForm : Form
{
    public AgentSettingsForm()
    {
        Text = "ReceptionAgent 設定"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 190); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        layout.Controls.Add(new Label { Text = "OQS連携フォルダー（req / res があるフォルダー）", AutoSize = true });
        var line = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        line.ColumnStyles.Add(new(SizeType.Percent, 100)); line.ColumnStyles.Add(new(SizeType.AutoSize));
        var path = new TextBox { Dock = DockStyle.Fill, AccessibleName = "OQS連携フォルダー", Text = AgentSettings.Load(MainForm.DataDirectory).OqsRoot };
        var browse = new Button { Text = "参照…", AutoSize = true };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "req / res の親フォルダーを選択", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath; };
        line.Controls.Add(path, 0, 0); line.Controls.Add(browse, 1, 0); layout.Controls.Add(line);
        layout.Controls.Add(new Label { AutoSize = true, Text = "医療機関コードはDynamicsの医院情報から自動取得します。\r\n変更したフォルダーは新しく保存する範囲に適用します。処理済み範囲の送信先は維持します。" });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_, _) =>
        {
            try { new AgentSettings { OqsRoot = path.Text }.Save(MainForm.DataDirectory); DialogResult = DialogResult.OK; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons); Controls.Add(layout);
        AcceptButton = save; CancelButton = cancel;
    }
}
