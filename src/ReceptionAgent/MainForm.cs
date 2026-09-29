using ReceptionAgent.Reception;

namespace ReceptionAgent;

public sealed class MainForm : Form
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceptionAgent");
    private readonly CaptureStore store = new(Path.Combine(DataDirectory, "Reception"));
    private readonly Label status = new() { AutoSize = true, Text = "設定でフォルダーを指定し、監視開始を押してください。" };
    private readonly StationDisplay stations = new("face XML取得", "患者ID取得", "予約番号取得", "iCall操作", "処理完了");
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, BackgroundColor = SystemColors.Window, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private CancellationTokenSource? cancellation;
    private FaceCaptureMonitor? monitor;
    private BulkRegistrationForm? bulk;
    private IReadOnlyList<CaptureRecord> records = [];
    private string? revision;
    public MainForm()
    {
        Text = "ReceptionAgent — 受付ステータス"; StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 720); MinimumSize = new Size(960, 550);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.Absolute, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(stations, 0, 0); layout.Controls.Add(status, 0, 1); layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(new Label { AutoSize = true, Text = "最新500件を表示／行をダブルクリックで詳細。操作送信済みはボタン呼出しの結果です。電子カルテ受付転送・発券は未接続です。" }, 0, 3);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var start = new Button { Text = "監視開始", AutoSize = true }; var stop = new Button { Text = "監視停止", Enabled = false, AutoSize = true };
        var settings = new Button { Text = "設定", AutoSize = true }; var manual = new Button { Text = "手動患者検索（テスト）", AutoSize = true };
        var openBulk = new Button { Text = "照会番号 Bulk Console（保留）", AutoSize = true };
        start.Click += async (_, _) =>
        {
            if (cancellation is not null) return;
            try
            {
                var config = AgentSettings.Load(DataDirectory); config.ValidateMonitoring();
                cancellation = new CancellationTokenSource(); monitor = new FaceCaptureMonitor(store, config, DynamicsFacePatientFinder.FindAsync);
                start.Enabled = settings.Enabled = false; stop.Enabled = true;
                await Task.Run(() => monitor.RunAsync(cancellation.Token));
            }
            catch (OperationCanceledException) { status.Text = "監視を停止しました。保存済みの未完了要求は次回開始時に再確認します。"; }
            catch (Exception ex) { status.Text = "監視停止: " + ex.Message; MessageBox.Show(this, status.Text, "監視を開始・継続できません"); }
            finally { cancellation?.Dispose(); cancellation = null; monitor = null; start.Enabled = settings.Enabled = true; stop.Enabled = false; stations.SetState(-1, false); Reload(); }
        };
        stop.Click += (_, _) => { cancellation?.Cancel(); stop.Enabled = false; status.Text = "停止しています…"; };
        settings.Click += (_, _) => { try { using var dialog = new AgentSettingsForm(); dialog.ShowDialog(this); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        manual.Click += (_, _) => { using var dialog = new ManualLookupForm(); dialog.ShowDialog(this); };
        openBulk.Click += (_, _) => { if (bulk is null || bulk.IsDisposed) bulk = new BulkRegistrationForm(); bulk.Show(this); bulk.Activate(); };
        commands.Controls.AddRange([start, stop, settings, manual, openBulk]); layout.Controls.Add(commands, 0, 4); Controls.Add(layout);
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var r = records.FirstOrDefault(r => r.Id == grid.Rows[e.RowIndex].Cells["ID"].Value as string);
            if (r is null) return;
            string detail = $"{r.FileName}\r\n取得元: {r.SourcePath}\r\n状態: {r.Status}\r\n患者照合: {r.Lookup?.Message}\r\n来院確認: {r.ArrivalResult}\r\n連携: {r.LinkResult}\r\n要求ID: {r.PendingRequest?.RequestId}\r\n";
            if (r.Lookup is not null) detail += string.Join("\r\n", r.Lookup.Candidates.Select(c => $"候補: {c.PatientId} / {c.Name} / {string.Join(",", c.RawChartNumbers)}"));
            MessageBox.Show(this, detail, "取込結果");
        };
        refresh.Tick += (_, _) => { try { Reload(); } catch (Exception ex) { status.Text = "履歴表示エラー: " + ex.Message; } };
        Shown += (_, _) => { Reload(); refresh.Start(); };
        FormClosing += (_, e) =>
        {
            if (cancellation is not null) { e.Cancel = true; cancellation.Cancel(); status.Text = "監視停止後に閉じてください。"; }
            else if (bulk?.IsRunning == true) { e.Cancel = true; MessageBox.Show(this, "Bulk Consoleを一時停止してください。"); }
        };
        FormClosed += (_, _) => refresh.Dispose();
    }
    private void Reload()
    {
        var rows = store.List();
        if (monitor is not null && cancellation?.IsCancellationRequested == false) status.Text = monitor.CaptureStatus + $" ／ 要確認 {rows.Count(r => r.Conflict || r.Stage == CaptureStage.NeedsReview)}件（表示範囲）";
        string next = string.Join('|', rows.Select(r => r.Id + r.UpdatedAt.ToString("O") + r.Conflict));
        if (revision == next) return;
        string? selected = grid.CurrentRow?.Cells["ID"].Value as string;
        records = rows; revision = next;
        grid.DataSource = rows.Select(r => new { ID = r.Id, 状態 = r.Conflict ? "同名XMLの内容競合・停止" : r.Status,
            患者ID = r.Lookup?.Selected?.PatientId, カルテ番号 = string.Join(",", r.Lookup?.Selected?.RawChartNumbers ?? []), 予約番号 = r.ReceptionNo,
            氏名 = r.Lookup?.Selected?.Name ?? r.XmlPatientName, フリガナ = r.Face?.NameKana, 生年月日 = r.Face?.Birthdate.ToString("yyyy/MM/dd"),
            保険者番号 = r.Face?.Insurance?.InsurerNumber, 記号 = r.Face?.Insurance?.Symbol, 番号 = r.Face?.Insurance?.Number, 枝番 = r.Face?.Insurance?.Branch,
            来院確認 = r.ArrivalResult, 連携 = r.LinkResult, ファイル名 = r.FileName, ファイル作成日時 = r.FileCreatedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            XML生成日時 = r.GeneratedAt.ToString("yyyy/MM/dd HH:mm:ss"), 取得日時 = r.CapturedAt.ToString("yyyy/MM/dd HH:mm:ss") }).ToList();
        if (grid.Columns["ID"] is { } idColumn) idColumn.Visible = false;
        if (selected is not null) foreach (DataGridViewRow row in grid.Rows) if ((string?)row.Cells["ID"].Value == selected) { grid.CurrentCell = row.Cells["状態"]; break; }
        var active = rows.FirstOrDefault(r => !r.Conflict && r.Stage is not (CaptureStage.Completed or CaptureStage.NeedsReview));
        if (active is not null && monitor is not null) stations.SetState(active.Stage switch { CaptureStage.Captured => 1, CaptureStage.PatientIdentified or CaptureStage.FindWaiting => 2, _ => 3 }, true);
        else stations.SetState(rows.Count > 0 ? 4 : -1, false, hasError: rows.Any(r => r.Conflict || r.Stage == CaptureStage.NeedsReview), isCompleted: rows.Count > 0);
    }
}
