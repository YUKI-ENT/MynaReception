using ReceptionAgent.Reception;
using ReceptionAgent.Oqs.ReferenceNumber;

namespace ReceptionAgent;

public sealed class MainForm : Form
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceptionAgent");
    private readonly CaptureStore store = new(Path.Combine(DataDirectory, "Reception"));
    private readonly Label status = new() { AutoSize = true, Text = "設定でフォルダーを指定し、監視開始を押してください。" };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, BackgroundColor = SystemColors.Window, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private CancellationTokenSource? cancellation;
    private FaceCaptureMonitor? monitor;
    private readonly SingleReferenceRegistrationService registration = new(Path.Combine(DataDirectory, "SingleReferenceRegistration"), TimeSpan.FromSeconds(30));
    private readonly Button arrived = new() { Text = "来院確認", AutoSize = true, Enabled = false };
    private readonly Button link = new() { Text = "連携", AutoSize = true, Enabled = false };
    private readonly Button register = new() { Text = "照会番号登録／結果再確認", AutoSize = true, Enabled = false };
    private readonly CancellationTokenSource resultCancellation = new();
    private Task? pendingResultRefresh;
    private bool refreshingResults;
    private bool busy;
    private IReadOnlyList<CaptureRecord> records = [];
    private string? revision;
    public MainForm()
    {
        Text = "ReceptionAgent — 受付ステータス"; StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 720); MinimumSize = new Size(960, 550);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(new Label { AutoSize = true, Text = "最新500件／ダブルクリックで詳細。選択行から手動操作します。来院確認・連携の結果はボタン呼出し結果です。" }, 0, 2);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var start = new Button { Text = "監視開始", AutoSize = true }; var stop = new Button { Text = "監視停止", Enabled = false, AutoSize = true };
        var settings = new Button { Text = "設定", AutoSize = true }; var manual = new Button { Text = "手動患者検索（テスト）", AutoSize = true };
        start.Click += async (_, _) =>
        {
            if (cancellation is not null) return;
            try
            {
                var config = AgentSettings.Load(DataDirectory); config.ValidateMonitoring();
                cancellation = new CancellationTokenSource(); monitor = new FaceCaptureMonitor(store, config, DynamicsFacePatientFinder.FindAsync, registration);
                start.Enabled = settings.Enabled = false; stop.Enabled = true;
                await Task.Run(() => monitor.RunAsync(cancellation.Token));
            }
            catch (OperationCanceledException) { status.Text = "監視を停止しました。保存済みの未完了要求は次回開始時に再確認します。"; }
            catch (Exception ex) { status.Text = "監視停止: " + ex.Message; MessageBox.Show(this, status.Text, "監視を開始・継続できません"); }
            finally { cancellation?.Dispose(); cancellation = null; monitor = null; start.Enabled = settings.Enabled = true; stop.Enabled = false; Reload(); }
        };
        stop.Click += (_, _) => { cancellation?.Cancel(); stop.Enabled = false; status.Text = "停止しています…"; };
        settings.Click += (_, _) => { try { using var dialog = new AgentSettingsForm(); dialog.ShowDialog(this); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        manual.Click += (_, _) => { using var dialog = new ManualLookupForm(); dialog.ShowDialog(this); };
        arrived.Click += async (_, _) => await RunSelectedAsync("arrived");
        link.Click += async (_, _) => await RunSelectedAsync("link");
        register.Click += async (_, _) => await RunSelectedAsync("register");
        grid.SelectionChanged += (_, _) => UpdateActions();
        commands.Controls.AddRange([arrived, link, register, start, stop, settings, manual]);
        layout.Controls.Add(commands, 0, 3); Controls.Add(layout);
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var r = records.FirstOrDefault(r => r.Id == grid.Rows[e.RowIndex].Cells["ID"].Value as string);
            if (r is null) return;
            string detail = $"{r.FileName}\r\n取得元: {r.SourcePath}\r\n状態: {r.Status}\r\n患者照合: {r.Lookup?.Message}\r\n来院確認: {r.ArrivalResult}\r\n連携: {r.LinkResult}\r\n要求ID: {r.PendingRequest?.RequestId}\r\n";
            if (r.Face?.Insurance is { } insurance) detail += $"保険者番号: {insurance.InsurerNumber}\r\n記号: {insurance.Symbol}\r\n番号: {insurance.Number}\r\n枝番: {insurance.Branch}\r\n";
            if (r.Lookup is not null) detail += string.Join("\r\n", r.Lookup.Candidates.Select(c => $"候補: {c.PatientId} / {c.Name} / {string.Join(",", c.RawChartNumbers)}"));
            var job = registration.Load(r.Id);
            if (job is not null) detail += $"\r\n照会番号登録: {job.State} / {job.Message}\r\n要求: {job.RequestFileName}\r\n応答: {job.ResponseFileName}\r\n結果: {job.SegmentOfResult} / {job.ErrorCode} / {job.ErrorMessage} / {job.ProcessingResultStatus} / {job.ProcessingResultCode} / {job.ProcessingResultMessage}";
            MessageBox.Show(this, detail, "取込結果");
        };
        refresh.Tick += async (_, _) => await RefreshAsync();
        Shown += (_, _) => { Reload(); refresh.Start(); };
        FormClosing += (_, e) =>
        {
            if (cancellation is not null) { e.Cancel = true; cancellation.Cancel(); status.Text = "監視停止後に閉じてください。"; }
            else if (busy) { e.Cancel = true; status.Text = "手動要求の結果待ちです。処理終了後に閉じてください。"; }
        };
        FormClosed += (_, _) => { refresh.Stop(); resultCancellation.Cancel(); refresh.Dispose(); resultCancellation.Dispose(); };
    }
    private async Task RefreshAsync()
    {
        if (refreshingResults || IsDisposed) return;
        refreshingResults = true;
        var token = resultCancellation.Token;
        try
        {
            if (!busy)
            {
                pendingResultRefresh = Task.Run(() => registration.RefreshResultsAsync(token), token);
                await pendingResultRefresh;
            }
            if (!IsDisposed) Reload();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) status.Text = "登録結果・履歴確認エラー: " + ex.Message; }
        finally { refreshingResults = false; }
    }
    private static string RegistrationStatus(CaptureRecord record, SingleReferenceRegistrationJob? job) => job?.State switch
    {
        ReferenceRegistrationState.Completed => "成功",
        ReferenceRegistrationState.Failed => "失敗",
        ReferenceRegistrationState.RequestSubmitted or ReferenceRegistrationState.ResultWaiting => "応答待ち",
        ReferenceRegistrationState.Cancelled => "中断",
        ReferenceRegistrationState.RequestCreating => "要求作成中",
        _ => !string.IsNullOrWhiteSpace(record.AutomaticRegistrationError) ? "失敗（自動登録）" :
            record.AutomaticRegistrationAttempted ? "自動登録中" : string.IsNullOrWhiteSpace(record.Face?.ReferenceNumber) ? "未要求" : "登録済み（XML）"
    };
    private void Reload()
    {
        var rows = store.List();
        if (!busy && monitor is not null && cancellation?.IsCancellationRequested == false) status.Text = monitor.CaptureStatus + $" ／ 要確認 {rows.Count(r => r.Conflict || r.Stage == CaptureStage.NeedsReview)}件（表示範囲）";
        var jobs = rows.ToDictionary(r => r.Id, r => registration.Load(r.Id));
        string next = string.Join('|', rows.Select(r => r.Id + r.UpdatedAt.ToString("O") + r.Conflict + jobs[r.Id]?.UpdatedAt.ToString("O")));
        if (revision == next) return;
        string? selected = grid.CurrentRow?.Cells["ID"].Value as string;
        records = rows; revision = next;
        grid.DataSource = rows.Select(r => new { ID = r.Id, 状態 = r.Conflict ? "同名XMLの内容競合・停止" : r.Status,
            患者ID = r.Lookup?.Selected?.PatientId, 内部カルテ番号 = string.Join(",", r.Lookup?.Selected?.RawChartNumbers ?? []), 予約番号 = r.ReceptionNo,
            氏名 = !string.IsNullOrWhiteSpace(r.Lookup?.Selected?.Name) ? r.Lookup.Selected.Name :
                !string.IsNullOrWhiteSpace(r.ReservationPatientName) ? r.ReservationPatientName : r.XmlPatientName, フリガナ = r.Face?.NameKana, 生年月日 = r.Face?.Birthdate.ToString("yyyy/MM/dd"),
            保険者番号 = r.Face?.Insurance?.InsurerNumber, 記号 = r.Face?.Insurance?.Symbol, 番号 = r.Face?.Insurance?.Number, 枝番 = r.Face?.Insurance?.Branch,
            来院確認 = r.ArrivalResult, 連携 = r.LinkResult, 照会番号 = r.Face?.ReferenceNumber, 照会番号登録 = RegistrationStatus(r, jobs[r.Id]), ファイル名 = r.FileName, ファイル作成日時 = r.FileCreatedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            XML生成日時 = r.GeneratedAt.ToString("yyyy/MM/dd HH:mm:ss"), 取得日時 = r.CapturedAt.ToString("yyyy/MM/dd HH:mm:ss") }).ToList();
        foreach (string column in new[] { "ID", "内部カルテ番号", "保険者番号", "記号", "番号", "枝番" })
            if (grid.Columns[column] is { } hidden) hidden.Visible = false;
        if (grid.Columns["患者ID"] is { } patientColumn) patientColumn.HeaderText = "カルテ番号";
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Cells["ID"].Value is not string id || !jobs.TryGetValue(id, out var job)) continue;
            var cell = row.Cells["照会番号登録"];
            cell.ToolTipText = job is null ? rows.First(r => r.Id == id).AutomaticRegistrationError : $"{job.Message}\r\n処理結果: {job.ProcessingResultStatus}\r\nエラー: {job.ErrorCode} {job.ErrorMessage}\r\n結果: {job.ProcessingResultCode} {job.ProcessingResultMessage}";
            cell.Style.BackColor = job?.State switch
            {
                ReferenceRegistrationState.Completed => Color.Honeydew,
                ReferenceRegistrationState.Failed => Color.MistyRose,
                ReferenceRegistrationState.RequestSubmitted or ReferenceRegistrationState.ResultWaiting => Color.LemonChiffon,
                _ => SystemColors.Window
            };
        }
        if (selected is not null) foreach (DataGridViewRow row in grid.Rows) if ((string?)row.Cells["ID"].Value == selected) { grid.CurrentCell = row.Cells["状態"]; break; }
        UpdateActions();
    }
    private CaptureRecord? SelectedRecord() => grid.Columns.Contains("ID") && grid.CurrentRow?.Cells["ID"].Value is string id ? store.Get(id) : null;
    private void UpdateActions()
    {
        var record = SelectedRecord();
        bool identified = !busy && record is { Conflict: false, Lookup.Selected: not null };
        bool reservation = identified && record!.GeneratedAt.Date == DateTime.Today &&
            !string.IsNullOrWhiteSpace(record.ReceptionNo) && !string.IsNullOrWhiteSpace(record.ReservationPatientName);
        bool CanOperate(string action) => reservation &&
            (record!.PendingRequest?.Action == action && record.Stage is CaptureStage.ArrivedWaiting or CaptureStage.LinkWaiting ||
             record.PendingRequest is null && record.Stage == CaptureStage.Completed &&
             !record.Responses.Any(r => r.RequestId == "ra-" + record.Id + "-" + action));
        arrived.Enabled = CanOperate("arrived"); link.Enabled = CanOperate("link");
        var job = record is null ? null : registration.Load(record.Id);
        register.Enabled = identified && record!.Lookup!.Candidates.Count == 1 && (job is null ? string.IsNullOrWhiteSpace(record!.Face?.ReferenceNumber) && record.Face?.Insurance is not null :
            job.State is ReferenceRegistrationState.ResultWaiting or ReferenceRegistrationState.RequestSubmitted);
    }
    private Task<SingleReferenceRegistrationJob> RegisterSelectedAsync(string id)
    {
        var record = store.Get(id);
        record.Face = Face.FaceXmlParser.Parse(store.ReadXml(id), record.Encoding);
        return registration.RegisterAsync(record, AgentSettings.Load(DataDirectory).OqsRoot, CancellationToken.None);
    }
    private async Task RunSelectedAsync(string action)
    {
        if (busy || SelectedRecord() is not { } record) return;
        busy = true; UpdateActions();
        try
        {
            if (action == "register")
            {
                if (pendingResultRefresh is not null) await pendingResultRefresh;
                status.Text = "照会番号の単件登録／応答確認中…";
                var result = await Task.Run(() => RegisterSelectedAsync(record.Id));
                status.Text = result.Message;
            }
            else
            {
                status.Text = action == "arrived" ? "来院確認を要求しています…" : "連携を要求しています…";
                var workflow = new ReceptionWorkflow(store, DynamicsFacePatientFinder.FindAsync);
                await workflow.QueueManualAsync(record.Id, action, CancellationToken.None);
                await workflow.StepByIdAsync(record.Id, CancellationToken.None);
                record = store.Get(record.Id);
                status.Text = record.Status;
            }
        }
        catch (Exception ex) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "操作結果を確認してください"); }
        finally { busy = false; revision = null; Reload(); UpdateActions(); }
    }
}
