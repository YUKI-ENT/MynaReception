using System.Diagnostics;
using ReceptionAgent.Dynamics;
using ReceptionAgent.Oqs.ReferenceNumber;

namespace ReceptionAgent;

public sealed class BulkRegistrationForm : Form
{
    private readonly RegistrationJobStore store = new(Path.Combine(MainForm.DataDirectory, "ReferenceRegistration", "Jobs"));
    private readonly IDynamicsProvider? provider;
    private readonly Label destination = new() { AutoSize = true, MaximumSize = new Size(1020, 0), Text = "医療機関コードはDynamicsから自動取得／OQSフォルダーはメイン画面の設定で指定" };
    private bool preparing;
    private readonly NumericUpDown from = Number("開始患者番号"), to = Number("終了患者番号");
    private readonly ComboBox history = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 660 };
    private readonly Button create = new() { Text = "範囲を保存", AutoSize = true };
    private readonly Button run = new() { Text = "開始／再開", AutoSize = true };
    private readonly Button pause = new() { Text = "一時停止", AutoSize = true, Enabled = false };
    private readonly Button draft = new() { Text = "XML作成（未送信）", AutoSize = true };
    private string? draftDirectory;
    private readonly Label counts = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1020, 0) };
    private readonly StationDisplay stations = new("患者・保険情報取得", "受付番号取得", "処理要求中", "完了");
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, BackgroundColor = SystemColors.Window, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoGenerateColumns = true, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private CancellationTokenSource? cancellation;
    private Guid? selected;
    public bool IsRunning => cancellation is not null || preparing;
    private sealed record JobChoice(Guid Id, string Label) { public override string ToString() => Label; }
    private static NumericUpDown Number(string name) => new() { Minimum = 1, Maximum = 999999999, Value = 1, Width = 120, AccessibleName = name };

    public BulkRegistrationForm(IDynamicsProvider? dynamicsProvider = null)
    {
        provider = dynamicsProvider;
        Text = "照会番号登録 — Bulk Console ステータス";
        Size = new Size(1120, 790); MinimumSize = new Size(1050, 720); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : i == 4 ? SizeType.Absolute : SizeType.AutoSize, i == 6 ? 100 : i == 4 ? 96 : 0));
        layout.Controls.Add(new Label { Text = "照会番号の一括登録", AutoSize = true, Font = new Font("Yu Gothic UI", 16, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(destination, 0, 1);
        var range = Row();
        range.Controls.AddRange([Caption("診察券番号"), from, Caption("から"), to, Caption("まで"), create, run, pause, draft]);
        layout.Controls.Add(range, 0, 2);
        var previous = Row(); previous.Controls.AddRange([Caption("保存した範囲"), history]);
        var refresh = new Button { Text = "履歴を再読込", AutoSize = true }; previous.Controls.Add(refresh);
        layout.Controls.Add(previous, 0, 3); layout.Controls.Add(stations, 0, 4);
        layout.Controls.Add(counts, 0, 5); layout.Controls.Add(grid, 0, 6);
        var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1 };
        footer.Controls.Add(status);
        var tools = Row();
        var xml = new Button { Text = "単件XMLの作成・確認", AutoSize = true };
        var open = new Button { Text = "結果保存フォルダー", AutoSize = true };
        var diagnose = new Button { Text = "Dynamics COM診断", AutoSize = true };
        var openDraft = new Button { Text = "未送信XMLフォルダー", AutoSize = true };
        openDraft.Click += (_, _) => Guard(() => { string path = draftDirectory ?? Path.Combine(MainForm.DataDirectory, "Work", "ReferenceNumber"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });
        tools.Controls.AddRange([diagnose, xml, open, openDraft]); footer.Controls.Add(tools); layout.Controls.Add(footer, 0, 7);
        Controls.Add(layout);
        create.Click += async (_, _) => await CreateJobAsync();
        run.Click += async (_, _) => await RunAsync();
        draft.Click += async (_, _) => await CreateDraftsAsync();
        pause.Click += (_, _) => { cancellation?.Cancel(); status.Text = "一時停止中…送信済み要求の結果待ちは保存されます。"; };
        history.SelectedIndexChanged += (_, _) => Guard(() =>
        {
            selected = (history.SelectedItem as JobChoice)?.Id;
            if (selected is { } id)
            {
                var job = store.Load(id);
                from.Value = job.From; to.Value = job.To; ShowJob(job);
            }
        });
        refresh.Click += (_, _) => Guard(() => ReloadHistory(selected));
        xml.Click += (_, _) => new ReferenceRequestForm().Show(this);
        diagnose.Click += async (_, _) =>
        {
            diagnose.Enabled = false;
            try
            {
                string report = await DynamicsPatientProvider.DiagnoseAsync();
                using var dialog = new Form { Text = "Dynamics COM診断（項目定義）", Size = new Size(850, 680), StartPosition = FormStartPosition.CenterParent };
                dialog.Controls.Add(new TextBox { Text = report, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill });
                dialog.ShowDialog(this);
                status.Text = "COM接続を確認しました。保険証枝番は患者保険マスター.枝番、照会番号はカルテ番号÷10です。";
            }
            catch (Exception ex) { status.Text = "COM診断: " + ex.Message; }
            finally { diagnose.Enabled = true; }
        };
        open.Click += (_, _) => Guard(() => { string path = Path.Combine(MainForm.DataDirectory, "ReferenceRegistration", "Jobs"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); });
        FormClosing += (_, e) => { if (IsRunning) { e.Cancel = true; cancellation?.Cancel(); status.Text = "一時停止しています。停止後に閉じてください。"; } };
        Guard(() => ReloadHistory(null));
        status.Text = "Dynamicsで患者マスターを開き、対象範囲を保存してください。開始するとOQSへ登録要求を送信します。";
        UpdateButtons();
    }
    private static FlowLayoutPanel Row() => new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 5, 0, 5) };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 5, 3) };
    private void Guard(Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; run.Enabled = false; } }
    private async Task CreateJobAsync()
    {
        if (IsRunning) return;
        preparing = true;
        try
        {
            UpdateButtons();
            string oqsRoot = AgentSettings.NormalizeRoot(AgentSettings.Load(MainForm.DataDirectory).OqsRoot);
            status.Text = "Dynamicsの医院情報から医療機関コードを取得しています…";
            string institution = await DynamicsPatientProvider.ReadInstitutionCodeAsync(CancellationToken.None);
            using var lease = store.AcquireLease();
            var job = new ReferenceRegistrationJob { InstitutionCode = institution, OqsRoot = oqsRoot, From = (long)from.Value, To = (long)to.Value,
                InsuranceBranchField = "枝番", RemoveChartBranchDigit = true };
            job.Validate();
            // Preserve old runs. Overlapping ranges require reviewing their history rather than silently re-registering.
            foreach (var id in store.ListIds())
            {
                var existing = store.Load(id);
                if (selected == existing.Id && existing.Entries.Count == 0 && existing.From == job.From && existing.To == job.To && existing.InstitutionCode == job.InstitutionCode)
                {
                    job.Id = existing.Id; job.CreatedAt = existing.CreatedAt; continue;
                }
                if (existing.InstitutionCode == job.InstitutionCode && existing.From <= job.To && existing.To >= job.From)
                    throw new InvalidOperationException("同じ医療機関に重なる保存範囲があります。既存の履歴を選択して再開してください。");
            }
            store.Save(job); ReloadHistory(job.Id);
            status.Text = "範囲を保存しました。開始するとOQSへ登録要求を送信します。";
        }
        catch (Exception ex) { status.Text = "範囲を保存できません: " + ex.Message; }
        finally { preparing = false; Guard(UpdateButtons); }
    }
    private void ReloadHistory(Guid? id)
    {
        var jobs = store.ListIds().Select(store.Load).OrderByDescending(j => j.CreatedAt).ToList();
        history.Items.Clear();
        foreach (var job in jobs) history.Items.Add(new JobChoice(job.Id, $"{job.CreatedAt:yyyy/MM/dd HH:mm}　{job.From}～{job.To}　最終処理: {job.LastProcessed?.ToString() ?? "未処理"}"));
        if (history.Items.Count > 0) history.SelectedIndex = Math.Max(0, jobs.FindIndex(j => j.Id == id));
        else { selected = null; counts.Text = "保存した範囲はありません。"; }
        UpdateButtons();
    }
    private void ShowJob(ReferenceRegistrationJob job)
    {
        destination.Text = $"保存範囲の医療機関コード: {job.InstitutionCode}　OQS: {job.OqsRoot}";
        counts.Text = $"成功 {job.Entries.Count(e => e.Stage == RegistrationStage.Completed)} 件　失敗 {job.Entries.Count(e => e.Stage == RegistrationStage.Failed)} 件　患者なし {job.Entries.Count(e => e.Stage == RegistrationStage.NotFound)} 件　／対象 {job.To - job.From + 1} 番号\r\n最終処理番号: {job.LastProcessed?.ToString() ?? "—"}　最終成功番号: {job.LastSuccessful}　次の確認番号: {(job.IsFinished ? "範囲終了" : job.NextPatient.ToString())}";
        grid.DataSource = job.Entries.Select(e => new { 患者ID = e.PatientId, 状態 = StageText(e.Stage), OQS受付番号 = e.ReceptionNumber, 結果コード = e.ResultCode, 更新日時 = e.UpdatedAt.ToString("MM/dd HH:mm:ss") }).ToList();
        var last = job.Entries.LastOrDefault();
        stations.SetState(last is null ? -1 : Station(last.Stage), IsRunning && last?.IsTerminal == false, last?.Stage == RegistrationStage.Failed);
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        draft.Enabled = create.Enabled = history.Enabled = !IsRunning; pause.Enabled = cancellation is not null;
        var job = selected is { } id ? store.Load(id) : null;
        run.Enabled = !IsRunning && job is not null && !job.IsFinished && (provider is not null || job.InsuranceBranchField.Length > 0 || job.Entries.Any(e => !e.IsTerminal && e.Stage != RegistrationStage.Pending));
        from.Enabled = to.Enabled = !IsRunning;
    }
    private async Task CreateDraftsAsync()
    {
        if (IsRunning) return;
        long first = (long)from.Value, last = (long)to.Value;
        if (last < first || last - first >= 100000) { status.Text = "患者番号の範囲を確認してください（1回最大100000番号）。"; return; }
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var rows = new System.ComponentModel.BindingList<DraftView>();
        try
        {
            UpdateButtons();
            grid.DataSource = rows;
            status.Text = "Dynamicsから医院情報・主保険を取得してXMLを作成します（未送信）。";
            stations.SetState(0, true);
            string institution = await DynamicsPatientProvider.ReadInstitutionCodeAsync(token);
            draftDirectory = Path.Combine(MainForm.DataDirectory, "Work", "ReferenceNumber", "Draft_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
            destination.Text = "未送信XML: " + draftDirectory;
            var progress = new Progress<ReferenceDraftRow>(row =>
            {
                rows.Add(new(row.PatientId, row.RawChartNo, row.Status, row.Code, row.FileName));
                counts.Text = $"XML作成 {rows.Count(r => r.ファイル名.Length > 0)} 件　要確認・患者なし {rows.Count(r => r.ファイル名.Length == 0)} 件　確認済み {rows.Count}／{last - first + 1} 番号（未送信・未登録）";
            });
            var exporter = new ReferenceDraftExporter(provider ?? new DynamicsPatientProvider("枝番", true, institution), MainForm.DataDirectory);
            await Task.Run(() => exporter.ExportAsync(institution, first, last, draftDirectory, progress, token));
            status.Text = "XML作成が終了しました。未送信XMLフォルダーで確認できます。登録を行う場合は範囲を保存し、開始／再開を押してください。";
        }
        catch (OperationCanceledException) { status.Text = "XML作成を停止しました。作成済みXMLと確認結果はローカルWorkに残っています（未送信）。"; }
        catch (Exception ex) { status.Text = "XML作成を停止しました: " + ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; stations.SetState(-1, false); Guard(UpdateButtons); }
    }
    private sealed record DraftView(string 患者ID, string 採用カルテ番号, string 状態, string 結果コード, string ファイル名);
    private async Task RunAsync()
    {
        if (selected is not { } id || IsRunning) return;
        ReferenceRegistrationJob job;
        try { job = store.Load(id); }
        catch (Exception ex) { status.Text = "履歴を読み込めません: " + ex.Message; return; }
        if (job.From != (long)from.Value || job.To != (long)to.Value)
        { status.Text = "入力が保存範囲と異なります。範囲を保存するか、履歴を再読込してください。"; return; }
        cancellation = new CancellationTokenSource(); UpdateButtons();
        var progress = new Progress<BulkProgress>(p => { status.Text = $"患者 {p.PatientId}：{p.Message}"; Guard(() => ShowJob(store.Load(id))); });
        try
        {
            IDynamicsProvider? activeProvider = provider;
            if (activeProvider is null && job.InsuranceBranchField.Length > 0)
                activeProvider = new DynamicsPatientProvider(job.InsuranceBranchField, job.RemoveChartBranchDigit, job.InstitutionCode);
            // Pending result recovery remains available even while Access is closed.
            if (activeProvider is DynamicsPatientProvider dynamics && !job.Entries.Any(e => !e.IsTerminal && e.Stage != RegistrationStage.Pending))
                await dynamics.ValidateAsync(cancellation.Token);
            var service = new ReferenceNumberRegistrationService(store, MainForm.DataDirectory, activeProvider);
            await Task.Run(() => service.RunAsync(id, progress, cancellation.Token));
            status.Text = "範囲の処理が終了しました。失敗・患者なしは成功件数に含みません。";
        }
        catch (OperationCanceledException) { status.Text = "一時停止しました。再開時は保存した状態から続けます。"; }
        catch (Exception ex) { status.Text = "処理を保留しました: " + ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; Guard(() => ShowJob(store.Load(id))); }
    }
    private static int Station(RegistrationStage s) => s switch { RegistrationStage.Pending => 0, RegistrationStage.UploadWaiting => 1, RegistrationStage.DownloadReady or RegistrationStage.ResultWaiting => 2, _ => 3 };
    private static string StageText(RegistrationStage s) => s switch { RegistrationStage.Pending => "未送信", RegistrationStage.UploadWaiting => "受付番号待ち", RegistrationStage.DownloadReady => "受付済み", RegistrationStage.ResultWaiting => "最終結果待ち", RegistrationStage.Completed => "成功", RegistrationStage.Failed => "失敗", _ => "患者なし" };
}
