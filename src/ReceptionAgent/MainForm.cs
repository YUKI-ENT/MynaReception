using ReceptionAgent.Reception;
using ReceptionAgent.Oqs.ReferenceNumber;

namespace ReceptionAgent;

public sealed class MainForm : ReceptionAgentForm
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceptionAgent");
    private readonly CaptureStore store;
    private readonly string dataDirectory;
    private readonly Label status = new() { AutoSize = true, Text = "設定でフォルダーを指定し、左上のXML監視をONにしてください。" };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, BackgroundColor = SystemColors.Window, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private readonly ComboBox displayDate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private bool updatingDates;
    private bool followToday = true;
    private CancellationTokenSource? cancellation;
    private FaceCaptureMonitor? monitor;
    private readonly SingleReferenceRegistrationService registration;
    private readonly Button settings = new() { Text = "設定", AutoSize = true };
    private readonly Button assign = new() { Text = "予約なし枠割当", AutoSize = true, Enabled = false };
    private readonly Button arrived = new() { Text = "来院確認", AutoSize = true, Enabled = false };
    private readonly Button link = new() { Text = "連携", AutoSize = true, Enabled = false };
    private readonly Button register = new() { Text = "照会番号登録／結果再確認", AutoSize = true, Enabled = false };
    private readonly Button posPrint = new() { Text = "POS発券（2枚）…", AutoSize = true, Enabled = false };
    private readonly CancellationTokenSource resultCancellation = new();
    private Task? pendingResultRefresh;
    private bool refreshingResults;
    private bool busy;
    private IReadOnlyList<CaptureRecord> records = [];
    private string? revision;
    private volatile bool monitoringAvailable;
    private bool MonitoringReady => monitoringAvailable && monitor?.FoldersAccessible == true;
    private volatile ReceptionOperationMode operationMode;
    private bool KioskReady => MonitoringReady && operationMode == ReceptionOperationMode.Kiosk;
    private readonly TableLayoutPanel modeTiles = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, 10) };
    private readonly CheckBox xmlMode = CreateModeTile("XML監視", "クリックで監視開始／停止", Color.FromArgb(65, 100, 150));
    private readonly CheckBox reservationMode = CreateModeTile("予約あり", "来院確認・連携操作", Color.FromArgb(30, 125, 85));
    private readonly CheckBox walkInMode = CreateModeTile("予約なし", "枠割当（再診・番号確定）", Color.FromArgb(160, 105, 30));
    private readonly CheckBox kioskMode = CreateModeTile("Kioskモード", "端末で確認・発券", Color.FromArgb(110, 75, 170));
    private readonly CheckBox staffPrint = new() { Text = "Kioskなしの自動発券", AutoSize = true };
    private bool updatingMode;
    private readonly CheckBox autoArrival = new() { Text = "自動来院確認", AutoSize = true, AccessibleName = "自動来院確認" };
    private readonly CheckBox autoLink = new() { Text = "自動連携", AutoSize = true, AccessibleName = "自動連携" };
    private readonly StaffReceiptService staffReceipts;
    private readonly Kiosk.KioskSessions kioskSessions;
    private readonly Kiosk.KioskAutomationService kioskAutomation;
    private Web.KioskWebServer? kioskServer;
    private Task? monitorTask, webStartTask, refreshTask, manualTask, explorerTask;
    private bool closing, closeReady, resourcesDisposed;
    public MainForm() : this(DataDirectory) { }
    internal MainForm(string dataDirectory)
    {
        this.dataDirectory = dataDirectory;
        operationMode = AgentSettings.Load(dataDirectory).OperationMode;
        store = new(Path.Combine(dataDirectory, "Reception"));
        registration = new(Path.Combine(dataDirectory, "SingleReferenceRegistration"), TimeSpan.FromSeconds(30));
        Kiosk.KioskOptions KioskConfiguration()
        {
            var config = Kiosk.KioskOptions.Load(dataDirectory);
            // Arrival/link now belong to the main form's mode-independent switches.
            config.Automation.AutoArrival = false; config.Automation.AutoLink = false;
            return config;
        }
        kioskSessions = new Kiosk.KioskSessions(Path.Combine(dataDirectory, "Kiosk"), store, KioskConfiguration, () => KioskReady,
            receptionSettings: () => AgentSettings.Load(dataDirectory));
        kioskAutomation = new(kioskSessions, store, DynamicsFacePatientFinder.FindAsync,
            (receipt, printer) => KioskReceiptPrinter.Print(receipt, printer, AgentSettings.Load(dataDirectory).PosPrint), () => KioskReady);
        staffReceipts = new(store, () => AgentSettings.Load(dataDirectory),
            (receipt, options) => KioskReceiptPrinter.Print(receipt, options.PrinterName, options),
            () => MonitoringReady && operationMode == ReceptionOperationMode.Staff, kioskSessions.HasPendingAutomation);
        Text = "ReceptionAgent — 受付ステータス"; StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 720); MinimumSize = new Size(960, 550);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var dateBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        dateBar.Controls.Add(new Label { AutoSize = true, Text = "表示日（XML生成日）", Margin = new Padding(3, 6, 8, 3) });
        dateBar.Controls.Add(displayDate);
        modeTiles.ColumnStyles.Clear();
        foreach (var tile in new[] { xmlMode, reservationMode, walkInMode, kioskMode })
        {
            modeTiles.ColumnStyles.Add(new(SizeType.Percent, 25));
            modeTiles.Controls.Add(tile);
        }
        xmlMode.Click += async (_, _) => await ChangeLevelAsync(xmlMode.Checked ? 0 : 1);
        reservationMode.Click += async (_, _) => await ChangeLevelAsync(reservationMode.Checked ? 1 : 2);
        walkInMode.Click += async (_, _) => await ChangeLevelAsync(walkInMode.Checked ? 2 : 3);
        kioskMode.Click += async (_, _) => await ChangeLevelAsync(kioskMode.Checked ? 3 : 4);
        staffPrint.CheckedChanged += (_, _) =>
        {
            if (!updatingMode) ChangeMode(staffPrint.Checked ? ReceptionOperationMode.Staff : ReceptionOperationMode.Reservation);
        };
        UpdateModeTiles();
        bool updatingAutomatic = true;
        var automaticSettings = AgentSettings.Load(dataDirectory);
        autoArrival.Checked = automaticSettings.AutoArrival; autoLink.Checked = automaticSettings.AutoLink;
        updatingAutomatic = false;
        void SaveAutomatic()
        {
            if (updatingAutomatic) return;
            try
            {
                var config = AgentSettings.Load(dataDirectory);
                if (autoLink.Checked && config.ReceptionLinkMode == ReceptionLinkMode.None)
                    throw new InvalidOperationException("設定で連携方式（iCall連携または直接出力）を選択してください。");
                config.AutoArrival = autoArrival.Checked; config.AutoLink = autoLink.Checked; config.Save(dataDirectory);
                status.Text = "自動操作設定を保存しました。これから患者ID・予約番号が確定する受付に適用します。処理モード未選択では自動操作しません。";
            }
            catch (Exception ex)
            {
                updatingAutomatic = true;
                var config = AgentSettings.Load(dataDirectory);
                autoArrival.Checked = config.AutoArrival; autoLink.Checked = config.AutoLink;
                updatingAutomatic = false; MessageBox.Show(this, ex.Message, "自動操作を設定できません");
            }
        }
        autoArrival.CheckedChanged += (_, _) => SaveAutomatic(); autoLink.CheckedChanged += (_, _) => SaveAutomatic();
        dateBar.Controls.Add(autoArrival); dateBar.Controls.Add(autoLink); dateBar.Controls.Add(staffPrint);
        displayDate.Format += (_, e) =>
        {
            if (e.ListItem is DateOnly date) e.Value = date.ToString("yyyy/MM/dd") + (date == DateOnly.FromDateTime(DateTime.Today) ? "（当日）" : "");
        };
        displayDate.FormattingEnabled = true;
        displayDate.SelectedIndexChanged += (_, _) =>
        {
            if (updatingDates) return;
            followToday = displayDate.SelectedItem is DateOnly date && date == DateOnly.FromDateTime(DateTime.Today);
            revision = null; Reload();
        };
        layout.Controls.Add(modeTiles, 0, 0); layout.Controls.Add(status, 0, 1); layout.Controls.Add(dateBar, 0, 2); layout.Controls.Add(grid, 0, 3);
        layout.Controls.Add(new Label { AutoSize = true, Text = "選択日の最新500件／ダブルクリックで詳細。選択行から手動操作します。連携方式は設定で選択します。ファイル出力受付はDynamics取込完了を意味しません。" }, 0, 4);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var manual = new Button { Text = "手動患者検索（テスト）", AutoSize = true };
        settings.Click += (_, _) => { try { using var dialog = new AgentSettingsForm(dataDirectory); dialog.ShowDialog(this);
            updatingAutomatic = true;
            try { var config = AgentSettings.Load(dataDirectory); autoArrival.Checked = config.AutoArrival; autoLink.Checked = config.AutoLink; operationMode = config.OperationMode; UpdateModeTiles(); }
            finally { updatingAutomatic = false; }
            revision = null; Reload(); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        manual.Click += (_, _) => { using var dialog = new ManualLookupForm(); dialog.ShowDialog(this); };
        assign.Click += async (_, _) => await (manualTask = RunSelectedAsync("assign"));
        arrived.Click += async (_, _) => await (manualTask = RunSelectedAsync("arrived"));
        link.Click += async (_, _) => await (manualTask = RunSelectedAsync("link"));
        register.Click += async (_, _) => await (manualTask = RunSelectedAsync("register"));
        posPrint.Click += async (_, _) => await (manualTask = PrintSelectedAsync());
        grid.SelectionChanged += (_, _) => UpdateActions();
        var kiosk = new Button { Text = "Kioskコンソール", AutoSize = true };
        kiosk.Click += (_, _) => { try { using var dialog = new KioskConsoleForm(kioskSessions, () => kioskServer?.Url ?? ""); dialog.ShowDialog(this); } catch (Exception ex) { MessageBox.Show(this, ex.Message); } };
        commands.Controls.AddRange([assign, arrived, link, register, posPrint, settings, manual, kiosk]);
        layout.Controls.Add(commands, 0, 5); Controls.Add(layout);
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var r = records.FirstOrDefault(r => r.Id == grid.Rows[e.RowIndex].Cells["ID"].Value as string);
            if (r is null) return;
            string timing = $"\r\nXML初回発見: {r.FirstSeenAt:yyyy/MM/dd HH:mm:ss.fff}\r\nXML取得完了: {r.CapturedAt:yyyy/MM/dd HH:mm:ss.fff}\r\nカルテ検索: {r.PatientLookupMilliseconds:F0} ms\r\niCall照会開始: {r.ICallStartedAt:HH:mm:ss.fff}\r\niCall応答取得: {r.ICallReceivedAt:HH:mm:ss.fff}\r\niCall応答待ちタイムアウト回数: {r.ICallTimeoutCount}\r\n処理ログ: {Path.Combine(DataDirectory, "Reception", "Logs")}\r\n取込ID: {r.Id}\r\n";
            string detail = $"{r.FileName}\r\nファイル作成日時: {r.FileCreatedAt?.ToLocalTime():yyyy/MM/dd HH:mm:ss}\r\nXML生成日時: {r.GeneratedAt:yyyy/MM/dd HH:mm:ss}\r\n取得日時: {r.CapturedAt:yyyy/MM/dd HH:mm:ss}\r\n取得元: {r.SourcePath}\r\n状態: {r.Status}\r\n患者照合: {r.Lookup?.Message}\r\n再検証: {r.ReconciliationStatus}\r\n再検証カルテ番号: {r.VerifiedPatientId}\r\n次回: {r.ReconciliationNextAt}\r\n来院確認: {r.ArrivalResult}\r\n連携: {r.LinkResult}\r\n要求ID: {r.PendingRequest?.RequestId}\r\n";
            if (r.Face?.Insurance is { } insurance) detail += $"保険者番号: {insurance.InsurerNumber}\r\n記号: {insurance.Symbol}\r\n番号: {insurance.Number}\r\n枝番: {insurance.Branch}\r\n";
            if (r.Lookup is not null) detail += string.Join("\r\n", r.Lookup.Candidates.Select(c => $"候補: {c.PatientId} / {c.Name} / {string.Join(",", c.RawChartNumbers)}"));
            var job = registration.Load(r.Id);
            if (job is not null) detail += $"\r\n照会番号登録: {job.State} / {job.Message}\r\n登録番号: {job.Target.ReferenceNumber}\r\n訂正前: {job.PreviousReferenceNumber} / 訂正回数: {job.CorrectionCount}\r\n要求: {job.RequestFileName}\r\n応答: {job.ResponseFileName}\r\n結果: {job.SegmentOfResult} / {job.ErrorCode} / {job.ErrorMessage} / {job.ProcessingResultStatus} / {job.ProcessingResultCode} / {job.ProcessingResultMessage}";
            detail += $"\r\n枠割当: {r.AssignmentResult}\r\n割当確認日時: {r.AssignedAt}\r\n取得時モード: {r.OperationMode}\r\n自動発券: {r.StaffReceiptState}\r\n発券結果: {r.StaffReceiptMessage}\r\n発券送信日時: {r.StaffReceiptSpooledAt}";
            MessageBox.Show(this, detail + timing, "取込結果");
        };
        refresh.Tick += async (_, _) => { Reload(); await (refreshTask = RefreshAsync()); };
        Shown += (_, _) => { Reload(); refresh.Start(); };
        Shown += async (_, _) =>
        {
            try { if (AgentSettings.Load(dataDirectory).StartMonitoringOnLaunch) await StartMonitoringAsync(); }
            catch (Exception ex) { status.Text = "起動時の監視設定を読み取れません: " + ex.Message; }
        };
        Shown += async (_, _) => await (webStartTask = StartWebServerAsync());
        FormClosing += async (_, e) =>
        {
            if (closeReady) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; monitoringAvailable = false; refresh.Stop(); commands.Enabled = false; modeTiles.Enabled = false; autoArrival.Enabled = autoLink.Enabled = staffPrint.Enabled = false;
            status.Text = "終了しています… 未完了の要求は保存済みの記録から次回確認します。";
            await ShutdownAsync();
            closeReady = true;
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (!IsDisposed) Close(); }));
        };
        FormClosed += (_, _) => { if (resourcesDisposed) return; resourcesDisposed = true; refresh.Dispose(); };
    }
    private async Task StartMonitoringAsync(bool persist = false)
    {
        if (closing || busy || cancellation is not null) return;
        try
        {
            var config = AgentSettings.Load(dataDirectory);
            config.ValidateMonitoring();
            if (persist) { config.StartMonitoringOnLaunch = true; config.Save(dataDirectory); }
            cancellation = new CancellationTokenSource();
            monitor = new FaceCaptureMonitor(store, config, DynamicsFacePatientFinder.FindAsync, registration,
                DynamicsQualificationResultFinder.FindAsync, () => operationMode, () => AgentSettings.Load(dataDirectory));
            var activeMonitor = monitor; var token = cancellation.Token;
            settings.Enabled = false;
            monitoringAvailable = true;
            UpdateModeTiles();
            if (config.OpenResponseFolderOnMonitorStart)
                explorerTask = OpenResponseFolderAsync(config.ICallResponseDirectory, token);
            monitorTask = Task.Run(() => activeMonitor.RunAsync(token));
            await monitorTask;
        }
        catch (OperationCanceledException)
        { if (!closing) status.Text = "監視を停止しました。保存済みの未完了要求は次回開始時に再確認します。"; }
        catch (Exception ex)
        { if (!closing) { status.Text = "監視停止: " + ex.Message; MessageBox.Show(this, status.Text, "監視を開始・継続できません"); } }
        finally
        {
            monitoringAvailable = false; cancellation?.Dispose(); cancellation = null; monitor = null;
            if (!closing) { settings.Enabled = true; UpdateModeTiles(); Reload(); }
        }
    }
    private void StopMonitoring()
    {
        if (closing || busy || cancellation is null || cancellation.IsCancellationRequested) return;
        try
        {
            var config = AgentSettings.Load(dataDirectory);
            config.StartMonitoringOnLaunch = false;
            config.Save(dataDirectory);
            monitoringAvailable = false; cancellation.Cancel();
            status.Text = "停止しています…";
            UpdateModeTiles(); UpdateActions();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "監視状態を保存できません"); }
    }
    private static CheckBox CreateModeTile(string title, string description, Color color)
    {
        return new CheckBox { Appearance = Appearance.Button, AutoCheck = false, Dock = DockStyle.Fill,
            Height = 96, MinimumSize = new Size(0, 96), Margin = new Padding(3),
            FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11, FontStyle.Bold),
            Tag = (title, description, color), AccessibleName = title, UseVisualStyleBackColor = false };
    }
    private async Task ChangeLevelAsync(int level)
    {
        if (closing || busy || updatingMode || cancellation?.IsCancellationRequested == true) return;
        if (level == 0) { StopMonitoring(); return; }
        try
        {
            var config = AgentSettings.Load(dataDirectory);
            config.OperationMode = level switch
            {
                1 => ReceptionOperationMode.MonitorOnly,
                2 => ReceptionOperationMode.Reservation,
                3 => ReceptionOperationMode.WalkIn,
                4 => ReceptionOperationMode.Kiosk,
                _ => throw new ArgumentOutOfRangeException(nameof(level))
            };
            config.ValidateMonitoring();
            config.StartMonitoringOnLaunch = true;
            config.Save(dataDirectory);
            operationMode = config.OperationMode;
            if (cancellation is null) await StartMonitoringAsync();
            else status.Text = "動作レベルを保存しました。次に取得するXMLから適用し、次回起動時も復元します。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "動作レベルを変更できません"); }
        finally { UpdateModeTiles(); UpdateActions(); }
    }
    private void ChangeMode(ReceptionOperationMode mode)
    {
        if (closing || busy || updatingMode) return;
        try
        {
            var config = AgentSettings.Load(dataDirectory);
            config.OperationMode = mode;
            config.Save(dataDirectory);
            operationMode = mode;
            status.Text = "動作モードを保存しました。次に取得するXMLから適用します。開始済みの操作は継続します。";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "動作モードを保存できません"); }
        finally { UpdateModeTiles(); UpdateActions(); }
    }
    private void UpdateModeTiles()
    {
        updatingMode = true;
        try
        {
            xmlMode.Checked = cancellation is not null && !cancellation.IsCancellationRequested;
            xmlMode.Enabled = !closing && !busy && cancellation?.IsCancellationRequested != true;
            int level = xmlMode.Checked ? ReceptionModes.Level(operationMode) : 0;
            reservationMode.Checked = level >= 2;
            walkInMode.Checked = level >= 3;
            kioskMode.Checked = level >= 4;
            foreach (var tile in new[] { xmlMode, reservationMode, walkInMode, kioskMode })
            {
                var (title, description, color) = ((string, string, Color))tile.Tag!;
                tile.Text = $"{title}  {(tile.Checked ? "ON" : "OFF")}\n{description}";
                tile.BackColor = tile.Checked ? color : Color.FromArgb(238, 241, 245);
                tile.ForeColor = tile.Checked ? Color.White : Color.FromArgb(65, 70, 80);
                tile.FlatAppearance.BorderColor = color;
                tile.FlatAppearance.BorderSize = tile.Checked ? 3 : 1;
                tile.FlatAppearance.CheckedBackColor = color;
                tile.AccessibleDescription = tile.Text;
            }
            autoArrival.Enabled = autoLink.Enabled = !closing && !busy && reservationMode.Checked;
            staffPrint.Checked = operationMode == ReceptionOperationMode.Staff;
            staffPrint.Enabled = !closing && !busy && xmlMode.Checked && ReceptionModes.Level(operationMode) == 2;
        }
        finally { updatingMode = false; }
    }
    private async Task StartWebServerAsync()
    {
        var server = new Web.KioskWebServer(kioskSessions, () => KioskReady);
        try
        {
            var options = Kiosk.KioskOptions.Load(dataDirectory); options.Validate(); await server.StartAsync(options.Port);
            if (closing || IsDisposed) await server.DisposeAsync(); else kioskServer = server;
        }
        catch (Exception ex)
        {
            try { await server.DisposeAsync(); } catch { }
            if (!closing && !IsDisposed) status.Text = "Kiosk Webサーバー起動失敗: " + ex.Message;
        }
    }
    private async Task ShutdownAsync()
    {
        store.Log("shutdown_started", "", new { monitorActive = monitorTask is { IsCompleted: false }, manualActive = manualTask is { IsCompleted: false } });
        try { cancellation?.Cancel(); } catch (Exception ex) { store.Log("shutdown_cancel_error", "", new { error = ex.GetType().Name }); }
        try { resultCancellation.Cancel(); } catch (Exception ex) { store.Log("shutdown_cancel_error", "", new { error = ex.GetType().Name }); }
        async Task StopWeb()
        {
            if (webStartTask is not null) await webStartTask;
            var server = kioskServer; kioskServer = null;
            if (server is not null) await server.DisposeAsync();
        }
        var tasks = new[] { monitorTask, refreshTask, pendingResultRefresh, manualTask, explorerTask, StopWeb() }.OfType<Task>().ToArray();
        var all = Task.WhenAll(tasks);
        try { await all.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException) { store.Log("shutdown_timeout", "", new { pending = tasks.Count(t => !t.IsCompleted), waitSeconds = 8 }); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { store.Log("shutdown_error", "", new { error = ex.GetType().Name }); }
        _ = DisposeResultCancellationAsync(all);
        store.Log("shutdown_completed", "", new { pending = tasks.Count(t => !t.IsCompleted) });
    }
    private async Task DisposeResultCancellationAsync(Task all)
    {
        try { await all.ConfigureAwait(false); } catch { }
        finally { resultCancellation.Dispose(); }
    }
    private async Task OpenResponseFolderAsync(string folder, CancellationToken token)
    {
        try
        {
            while (!await Task.Run(() =>
            {
                try { using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator(); entries.MoveNext(); return true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            }, token)) await Task.Delay(TimeSpan.FromSeconds(5), token);
            token.ThrowIfCancellationRequested();
            bool minimized = await ResponseFolderExplorer.OpenMinimizedAsync(folder, token);
            store.Log("response_explorer_opened", "", new { minimized });
            if (!minimized && !closing && !IsDisposed) status.Text = "エクスプローラーの最小化を確認できませんでした。監視は継続しています。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            store.Log("response_explorer_failed", "", new { error = ex.GetType().Name });
            if (!closing && !IsDisposed) status.Text = "responseフォルダーを開けませんでした。監視は継続します: " + ex.Message;
        }
    }
    private async Task RefreshAsync()
    {
        if (closing || refreshingResults || IsDisposed) return;
        refreshingResults = true;
        var token = resultCancellation.Token;
        try
        {
            if (!busy)
            {
                await Task.Run(() => staffReceipts.RunOnceAsync(token), token);
                string? outputError = await Task.Run(() =>
                {
                    string? error = null;
                    var config = AgentSettings.Load(dataDirectory);
                    var folders = store.ReceiptOutputDirectories().ToList();
                    if (!string.IsNullOrWhiteSpace(config.DynamicsReceiptDirectory)) folders.Add(config.DynamicsReceiptDirectory);
                    foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try { new DynamicsReceiptOutput().Flush(folder); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                        {
                            error = "受付ファイル出力は再試行待ち: " + ex.Message;
                            store.Log("receipt_output_retry", "", new { error = ex.GetType().Name });
                        }
                    }
                    return error;
                }, token);
                pendingResultRefresh = Task.Run(() => registration.RefreshResultsAsync(token), token);
                await pendingResultRefresh;
                if (outputError is not null && !closing && !IsDisposed) status.Text = outputError;
                await Task.Run(() => kioskAutomation.RunOnceAsync(token), token);
            }
            if (!closing && !IsDisposed) Reload();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!closing && !IsDisposed) status.Text = "登録結果・履歴確認エラー: " + ex.Message; }
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
        if (closing || IsDisposed) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var selectedDate = followToday ? today : displayDate.SelectedItem is DateOnly date ? date : today;
        var dates = store.ListDates().Append(today).Distinct().OrderDescending().ToArray();
        if (!displayDate.Items.Cast<DateOnly>().SequenceEqual(dates) || !Equals(displayDate.SelectedItem, selectedDate))
        {
            updatingDates = true;
            try
            {
                displayDate.Items.Clear();
                displayDate.Items.AddRange(dates.Cast<object>().ToArray());
                displayDate.SelectedItem = selectedDate;
            }
            finally { updatingDates = false; }
        }
        var rows = store.List(date: selectedDate, orderByCaptureTime: true).OrderBy(r => r.CapturedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        if (!busy && monitor is not null && cancellation?.IsCancellationRequested == false) status.Text = monitor.CaptureStatus + $" ／ 要確認 {rows.Count(r => r.Conflict || r.Stage == CaptureStage.NeedsReview)}件（表示範囲）";
        var jobs = rows.ToDictionary(r => r.Id, r => registration.Load(r.Id));
        string next = selectedDate.ToString("yyyy-MM-dd") + ":" + string.Join('|', rows.Select(r => r.Id + r.UpdatedAt.ToString("O") + r.Conflict + jobs[r.Id]?.UpdatedAt.ToString("O")));
        if (revision == next) { UpdateActions(); return; }
        string? selected = grid.CurrentRow?.Cells["ID"].Value as string;
        records = rows; revision = next;
        grid.DataSource = rows.Select(r => new { ID = r.Id,
            患者ID = r.Lookup?.Selected?.PatientId, 内部カルテ番号 = string.Join(",", r.Lookup?.Selected?.RawChartNumbers ?? []), 予約番号 = r.ReceptionNo,
            氏名 = !string.IsNullOrWhiteSpace(r.Lookup?.Selected?.Name) ? r.Lookup.Selected.Name :
                !string.IsNullOrWhiteSpace(r.ReservationPatientName) ? r.ReservationPatientName : r.XmlPatientName, フリガナ = r.Face?.NameKana, 生年月日 = r.Face?.Birthdate.ToString("yyyy/MM/dd"),
            受付分類 = ReceptionClassification.Label(r.ReceptionCategory), 取得時間 = ReceptionClassification.FormatTime(r),
            保険者番号 = r.Face?.Insurance?.InsurerNumber, 記号 = r.Face?.Insurance?.Symbol, 番号 = r.Face?.Insurance?.Number, 枝番 = r.Face?.Insurance?.Branch,
            枠割当 = r.AssignmentResult, 来院確認 = r.ArrivalResult, 連携 = r.LinkResult, 照会番号 = r.Face?.ReferenceNumber, 照会番号登録 = RegistrationStatus(r, jobs[r.Id]),
            発券 = r.OperationMode == ReceptionOperationMode.Kiosk ? "Kiosk／手動" : r.OperationMode != ReceptionOperationMode.Staff && r.StaffReceiptState == StaffReceiptState.None ? "手動" : r.StaffReceiptState switch
            { StaffReceiptState.Running => "送信中", StaffReceiptState.Completed => "送信済み", StaffReceiptState.NeedsReview => "要確認", _ => "未発券" },
            状態 = r.Conflict ? "同名XMLの内容競合・停止" : r.Status, ファイル名 = r.FileName, ファイル作成日時 = r.FileCreatedAt?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"),
            再検証カルテ番号 = r.VerifiedPatientId, 再検証 = r.ReconciliationStatus,
            XML生成日時 = r.GeneratedAt.ToString("yyyy/MM/dd HH:mm:ss"), 取得日時 = r.CapturedAt.ToString("yyyy/MM/dd HH:mm:ss") }).ToList();
        foreach (string column in new[] { "ID", "内部カルテ番号", "保険者番号", "記号", "番号", "枝番", "ファイル名", "ファイル作成日時", "XML生成日時", "取得日時", "再検証カルテ番号", "再検証" })
            if (grid.Columns[column] is { } hidden) hidden.Visible = false;
        if (grid.Columns["患者ID"] is { } patientColumn) patientColumn.HeaderText = "カルテ番号";
        string[] columnOrder = ["患者ID", "予約番号", "氏名", "フリガナ", "生年月日", "受付分類", "取得時間", "枠割当", "来院確認", "連携", "照会番号", "照会番号登録", "発券", "状態", "ファイル名", "ファイル作成日時", "XML生成日時", "取得日時", "再検証カルテ番号", "再検証"];
        for (int i = 0; i < columnOrder.Length; i++)
            if (grid.Columns[columnOrder[i]] is { } column) column.DisplayIndex = i;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Cells["ID"].Value is not string id || !jobs.TryGetValue(id, out var job)) continue;
            var record = rows.First(r => r.Id == id);
            row.Cells["発券"].ToolTipText = record.StaffReceiptMessage;
            var classificationCell = row.Cells["受付分類"];
            classificationCell.Style.BackColor = record.ReceptionCategory switch
            {
                ReceptionCategory.ReturningWithReservation => Color.Honeydew,
                ReceptionCategory.ReturningWithoutReservation => Color.LightCyan,
                ReceptionCategory.NewWithReservation => Color.LemonChiffon,
                ReceptionCategory.NewWithoutReservation => Color.PeachPuff,
                ReceptionCategory.NeedsReview => Color.MistyRose,
                _ => Color.Gainsboro
            };
            classificationCell.Style.ForeColor = Color.Black;
            classificationCell.Style.SelectionBackColor = classificationCell.Style.BackColor;
            classificationCell.Style.SelectionForeColor = Color.Black;
            classificationCell.ToolTipText = "初診・再診は受付時のカルテ番号の取得有無による分類です。受診歴の断定ではありません。予約なしは現在のiCall一覧での検索結果です。カルテなしの予約ありは本人確認が必要です。";
            row.Cells["取得時間"].ToolTipText = $"XML生成日時（ファイル名）から受付分類確定まで。確定日時: {ReceptionClassification.ClassifiedAt(record)?.ToLocalTime():yyyy/MM/dd HH:mm:ss.fff}。照会番号登録・後日の再検証時間は含みません。";
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
        if (closing || IsDisposed) return;
        var record = SelectedRecord();
        bool identified = !busy && record is { Conflict: false, ReconciliationIdentityMismatch: false, Lookup.Selected: not null } &&
            (record.VerifiedPatientId.Length == 0 || record.VerifiedPatientId == record.Lookup.Selected.PatientId);
        bool reservation = identified && record!.GeneratedAt.Date == DateTime.Today &&
            !string.IsNullOrWhiteSpace(record.ReceptionNo) && !string.IsNullOrWhiteSpace(record.ReservationPatientName);
        modeTiles.Enabled = !busy;
        UpdateModeTiles();
        posPrint.Enabled = !busy && record is not null && Kiosk.KioskReceipt.CanPrintStaff(record) &&
            record.StaffReceiptState != StaffReceiptState.Running &&
            !(operationMode == ReceptionOperationMode.Staff && record.OperationMode == ReceptionOperationMode.Staff && record.StaffReceiptState == StaffReceiptState.None) &&
            !kioskSessions.HasPendingAutomation(record.Id) && AgentSettings.Load(dataDirectory).PosPrint.Enabled;
        bool CanOperate(string action) => reservation && !kioskSessions.HasPendingAutomation(record!.Id) &&
            (record!.PendingRequest?.Action == action && record.Stage is CaptureStage.ArrivedWaiting or CaptureStage.LinkWaiting ||
             record.PendingRequest is null && record.Stage == CaptureStage.Completed &&
             !record.Responses.Any(r => r.RequestId == "ra-" + record.Id + "-" + action));
        assign.Enabled = !busy && MonitoringReady && record is not null && WalkInAssignment.CanQueue(record) && !kioskSessions.HasPendingAutomation(record.Id);
        arrived.Enabled = CanOperate("arrived");
        link.Enabled = CanOperate("link") && record!.DynamicsReceiptDirectory.Length == 0 &&
            AgentSettings.Load(dataDirectory).ReceptionLinkMode != ReceptionLinkMode.None;
        var job = record is null ? null : registration.Load(record.Id);
        register.Enabled = identified && record!.Lookup!.Candidates.Count == 1 && (job is null ? string.IsNullOrWhiteSpace(record!.Face?.ReferenceNumber) && record.Face?.Insurance is not null :
            job.State is ReferenceRegistrationState.ResultWaiting or ReferenceRegistrationState.RequestSubmitted);
    }
    private Task<SingleReferenceRegistrationJob> RegisterSelectedAsync(string id)
    {
        var record = store.Get(id);
        record.Face = Face.FaceXmlParser.Parse(store.ReadXml(id), record.Encoding);
        return registration.RegisterAsync(record, AgentSettings.Load(dataDirectory).OqsRoot, CancellationToken.None);
    }
    private async Task PrintSelectedAsync()
    {
        if (closing || busy || SelectedRecord() is not { } record) return;
        busy = true; UpdateActions();
        try
        {
            var options = AgentSettings.Load(dataDirectory).PosPrint;
            if (!options.Enabled) return;
            if (record.StaffReceiptState == StaffReceiptState.Running || kioskSessions.HasPendingAutomation(record.Id))
                throw new InvalidOperationException("自動発券・Kiosk受付が処理中です。");
            using var dialog = new KioskReceiptForm(Kiosk.KioskReceipt.CreateStaff(record), false, options);
            dialog.ShowDialog(this);
            if (dialog.PrintSubmitted) await staffReceipts.RecordManualPrintAsync(record.Id, CancellationToken.None);
        }
        catch (Exception ex) { if (!closing) MessageBox.Show(this, ex.Message, "発券結果を確認してください"); }
        finally { busy = false; if (!closing) { revision = null; Reload(); } }
    }
    private async Task RunSelectedAsync(string action)
    {
        if (closing || busy || SelectedRecord() is not { } record) return;
        busy = true; UpdateActions();
        try
        {
            if (action == "register")
            {
                if (pendingResultRefresh is not null) await pendingResultRefresh;
                if (closing) return;
                status.Text = "照会番号の単件登録／応答確認中…";
                var result = await Task.Run(() => RegisterSelectedAsync(record.Id));
                if (!closing && !IsDisposed) status.Text = result.Message;
            }
            else
            {
                status.Text = action == "assign" ? "予約なし枠割当を要求しています…" : action == "arrived" ? "来院確認を要求しています…" : "連携を要求しています…";
                if (pendingResultRefresh is not null) await pendingResultRefresh;
                var workflow = new ReceptionWorkflow(store, DynamicsFacePatientFinder.FindAsync, AgentSettings.Load(dataDirectory));
                if (action == "assign" && (!MonitoringReady || kioskSessions.HasPendingAutomation(record.Id)))
                    throw new InvalidOperationException("監視中で、Kiosk受付の処理対象ではない行を選択してください。");
                await workflow.QueueManualAsync(record.Id, action, CancellationToken.None);
                await workflow.StepByIdAsync(record.Id, resultCancellation.Token);
                record = store.Get(record.Id);
                if (!closing && !IsDisposed) status.Text = record.Status;
            }
        }
        catch (Exception ex) { if (!closing && !IsDisposed) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "操作結果を確認してください"); } }
        finally { busy = false; if (!closing && !IsDisposed) { revision = null; Reload(); UpdateActions(); } }
    }
}
