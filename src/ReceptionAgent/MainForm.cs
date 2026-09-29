namespace ReceptionAgent;

public sealed class MainForm : Form
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceptionAgent");
    private BulkRegistrationForm? bulk;
    public MainForm()
    {
        Text = "ReceptionAgent — 受付ステータス";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1120, 490); MinimumSize = new Size(950, 460);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Absolute, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "受付の処理状況", AutoSize = true, Font = new Font("Yu Gothic UI", 17, FontStyle.Bold), Margin = new Padding(0, 0, 0, 16) }, 0, 0);
        layout.Controls.Add(new StationDisplay("face XML検知", "患者ID取得", "予約番号取得", "Dynamics受付転送", "iCall操作", "発券") { Dock = DockStyle.Fill }, 0, 1);
        layout.Controls.Add(new Label { Text = "受付フロー：未接続（今後実装）", AutoSize = true, Margin = new Padding(0, 15, 0, 10) }, 0, 2);
        layout.Controls.Add(new Label { Text = "現在は照会番号登録の整備を進めています。\r\n患者ID・予約番号・各処理の結果は、受付フロー接続後にここへ表示します。", Dock = DockStyle.Fill }, 0, 3);
        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var open = new Button { Text = "照会番号 Bulk Console", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        open.Click += (_, _) =>
        {
            if (bulk is null || bulk.IsDisposed) bulk = new BulkRegistrationForm();
            bulk.Show(this); bulk.Activate();
        };
        var settings = new Button { Text = "設定", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        settings.Click += (_, _) =>
        {
            try { using var dialog = new AgentSettingsForm(); dialog.ShowDialog(this); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を読み込めません"); }
        };
        commands.Controls.AddRange([open, settings]); layout.Controls.Add(commands, 0, 4); Controls.Add(layout);
        FormClosing += (_, e) => { if (bulk?.IsRunning == true) { e.Cancel = true; MessageBox.Show(this, "Bulk Consoleで一時停止してから終了してください。"); } };
    }
}
