using iCallManager.Bridge;
using iCallManager.Core;
using iCallManager.ICall;

namespace iCallManager;

public partial class Form1 : Form
{
    private readonly AppSettings settings;
    private readonly ReservationService service;
    private readonly CancellationTokenSource shutdown = new();
    private readonly DataGridView grid = new();
    private readonly TextBox logBox = new();
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox enableOperations = new() { Text = "実操作を有効化（API・デバッグ共通）", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox confirm = new() { Text = "デバッグ操作前に確認", Checked = true, AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox autoSync = new() { Text = "定期同期", Checked = true, AutoSize = true, Margin = new Padding(8) };
    private readonly NumericUpDown interval = new() { Minimum = 2, Maximum = 3600, Width = 65 };
    private readonly Button arrived = new() { Text = "選択患者の来院確認", AutoSize = true };
    private readonly Button link = new() { Text = "選択患者の連携", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer;
    private bool syncing, operating, closing;
    private Task? bridgeTask;

    public Form1(AppSettings settings)
    {
        InitializeComponent();
        using (var iconStream = typeof(Form1).Assembly.GetManifestResourceStream("iCallManager.ApplicationIcon.ico")!)
        using (var sourceIcon = new Icon(iconStream))
        {
            var applicationIcon = (Icon)sourceIcon.Clone();
            Icon = applicationIcon;
            Disposed += (_, _) => applicationIcon.Dispose();
        }
        this.settings = settings;
        service = new ReservationService(() => new ICallAutomation(settings));
        Text = "iCallManager — 受付同期・連携";
        Size = new Size(1150, 760);
        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Absolute, 145));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var read = new Button { Text = "今すぐ同期", AutoSize = true };
        var diagnose = new Button { Text = "行構造診断", AutoSize = true };
        var configure = new Button { Text = "設定", AutoSize = true };
        var settingsNotice = new Label { AutoSize = true, ForeColor = Color.DarkOrange, Margin = new Padding(8) };
        interval.Value = settings.SyncIntervalSeconds;
        toolbar.Controls.AddRange([read, autoSync, interval, new Label { Text = "秒", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, diagnose, configure, settingsNotice]);
        configure.Click += (_, _) =>
        {
            try
            {
                using var dialog = new SettingsForm(settings.BridgeDirectory, Icon);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                bool changed = !string.Equals(Path.TrimEndingDirectorySeparator(settings.BridgeDirectory),
                    dialog.SavedBridgeDirectory, StringComparison.OrdinalIgnoreCase);
                settingsNotice.Text = changed ? "設定保存済み・再起動で反映" : "";
                Log("API連携フォルダー設定を保存: " + dialog.SavedBridgeDirectory +
                    (changed ? "（次回起動から反映）" : ""));
            }
            catch (Exception ex) { MessageBox.Show(this, "設定画面を開けません。\r\n" + ex.Message, "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        actions.Controls.AddRange([enableOperations, confirm, arrived, link]);
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoGenerateColumns = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (var (property, label) in new[] { ("ReceptionNo", "受付番号"), ("PatientId", "患者ID"), ("PatientName", "患者名"), ("InternalId", "内部ID"), ("HasMarkArrivedButton", "来院ボタン有"), ("HasLinkButton", "連携ボタン有"), ("HasAssignmentButton", "患者割当ボタン有"), ("CanAssignDummy", "ダミー割当可"), ("OperationRestriction", "操作制限") })
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = label });
        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(status, 0, 3);
        layout.Controls.Add(logBox, 0, 4);
        Controls.Add(layout);
        timer = new System.Windows.Forms.Timer(components!) { Interval = settings.SyncIntervalSeconds * 1000 };
        timer.Tick += async (_, _) => { if (autoSync.Checked) await SyncAsync(); };
        interval.ValueChanged += (_, _) => timer.Interval = (int)interval.Value * 1000;
        read.Click += async (_, _) => await SyncAsync();
        diagnose.Click += async (_, _) =>
        {
            diagnose.Enabled = false;
            try
            {
                string diagnostic = await service.DiagnoseAsync(shutdown.Token);
                if (closing) return;
                using var viewer = new Form { Text = "行構造診断 — 患者情報を含みます", Icon = this.Icon, Size = new Size(1000, 700), StartPosition = FormStartPosition.CenterParent };
                viewer.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = diagnostic });
                viewer.ShowDialog(this);
            }
            catch (Exception ex) { Log("診断失敗: " + ex.Message); }
            finally { if (!closing) diagnose.Enabled = true; }
        };
        enableOperations.CheckedChanged += (_, _) =>
        {
            service.OperationsEnabled = enableOperations.Checked;
            Log(enableOperations.Checked ? "実操作を有効化しました（SMB要求も操作可能）。" : "実操作を無効化しました。");
            UpdateButtons();
        };
        grid.SelectionChanged += (_, _) => UpdateButtons();
        arrived.Click += async (_, _) => await OperateAsync("arrived");
        link.Click += async (_, _) => await OperateAsync("link");
        Shown += async (_, _) =>
        {
            Log("起動時は読取専用です。ログイン済みの当日受付一覧を表示してください。");
            Log("要求フォルダー: " + Path.Combine(settings.BridgeDirectory, "request"));
            Log("設定: " + AppSettings.SettingsPath);
            try
            {
                var bridge = new FileBridge(settings.BridgeDirectory, Path.Combine(AppSettings.DataDirectory, "State"), service.ExecuteAsync, Log);
                bridgeTask = Task.Run(async () =>
                {
                    using (bridge)
                    {
                        try { await bridge.RunAsync(shutdown.Token); }
                        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                        catch (Exception ex) { Log("SMB受信停止: " + ex.GetType().Name); }
                    }
                });
            }
            catch (Exception ex) { Log("SMB開始失敗: " + ex.Message); }
            timer.Start();
            await SyncAsync();
        };
        FormClosing += (_, _) => { closing = true; timer.Stop(); service.OperationsEnabled = false; shutdown.Cancel(); service.Dispose(); };
        UpdateButtons();
    }

    private Reservation? Selected => grid.CurrentRow?.DataBoundItem as Reservation;
    private void UpdateButtons()
    {
        bool allowed = !operating && !syncing && service.OperationsEnabled && service.Snapshot.IsCurrent;
        arrived.Enabled = allowed && Selected?.CanMarkArrived == true;
        link.Enabled = allowed && Selected?.CanLink == true;
    }

    private async Task SyncAsync()
    {
        if (syncing || operating || closing) return;
        syncing = true;
        UpdateButtons();
        try { await service.SyncAsync(shutdown.Token); }
        catch (Exception ex) { Log("同期失敗: " + ex.Message); }
        finally
        {
            if (!closing)
            {
                var selected = Selected;
                var snapshot = service.Snapshot;
                grid.DataSource = snapshot.Rows.ToList();
                grid.ClearSelection();
                grid.CurrentCell = null;
                if (selected != null)
                    foreach (DataGridViewRow row in grid.Rows)
                        if (row.DataBoundItem is Reservation r && r.PatientId == selected.PatientId && r.ReceptionNo == selected.ReceptionNo && r.PatientName == selected.PatientName)
                        { grid.CurrentCell = row.Cells[0]; row.Selected = true; break; }
                status.Text = $"{snapshot.Status}　最終成功: {snapshot.LastSuccess?.ToString("HH:mm:ss") ?? "なし"}";
                status.ForeColor = snapshot.IsCurrent ? Color.DarkGreen : Color.Firebrick;
                syncing = false;
                UpdateButtons();
            }
        }
    }

    private async Task OperateAsync(string action)
    {
        var row = Selected;
        if (row is null || operating || !service.OperationsEnabled) return;
        operating = true;
        UpdateButtons();
        try
        {
            string label = action == "arrived" ? "来院確認" : "連携";
            if (confirm.Checked && MessageBox.Show(this,
                $"受付番号: {row.ReceptionNo}\n患者ID: {row.PatientId}\n患者名: {row.PatientName}\n\n「{label}」を実行しますか？",
                "実操作の確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var result = await service.ExecuteAsync(new(Guid.NewGuid().ToString("N"), action, row.PatientId, row.ReceptionNo, row.PatientName), shutdown.Token);
            Log($"{label}: {result.Code} — {result.Message}");
        }
        catch (Exception ex) { Log("操作エラー: " + ex.Message); }
        finally { operating = false; if (!closing) { UpdateButtons(); await SyncAsync(); } }
    }

    private void Log(string text)
    {
        if (closing || IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => Log(text)); } catch (InvalidOperationException) { } return; }
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}";
        if (logBox.TextLength > 60000) logBox.Clear();
        logBox.AppendText(line);
        try
        {
            var directory = Path.Combine(AppSettings.DataDirectory, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd}.log"), line);
        }
        catch (IOException) { status.Text = "ログ保存に失敗しました。ディスク・権限を確認してください。"; }
        catch (UnauthorizedAccessException) { status.Text = "ログ保存の権限がありません。"; }
    }
}
