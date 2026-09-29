using ReceptionAgent.Face;

namespace ReceptionAgent;

public sealed class ManualLookupForm : Form
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceptionAgent");
    private BulkRegistrationForm? bulk;
    private CancellationTokenSource? searchCancellation;
    private readonly StationDisplay stations = new("face XML読込", "患者ID取得", "予約番号取得", "Dynamics受付転送", "iCall操作", "発券");
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(1040, 0), Text = "face XMLを選択して患者IDを検索してください。後続の受付操作は未接続です。" };
    private readonly Label identity = new() { AutoSize = true, MaximumSize = new Size(1040, 0) };
    private readonly DataGridView candidates = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, BackgroundColor = SystemColors.Window, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };

    public ManualLookupForm()
    {
        Text = "ReceptionAgent — 手動患者検索（API送信なし）";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1120, 650); MinimumSize = new Size(950, 540);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new(SizeType.Absolute, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(stations, 0, 0);
        layout.Controls.Add(status, 0, 1); layout.Controls.Add(identity, 0, 2); layout.Controls.Add(candidates, 0, 3);
        layout.Controls.Add(new Label { AutoSize = true, Text = "この画面は患者照合のテスト用です。履歴保存・予約API送信はメイン画面の監視から行います。" }, 0, 4);
        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var search = new Button { Text = "face XMLを選択・患者検索", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        var open = new Button { Text = "照会番号 Bulk Console（保留）", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
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
        search.Click += async (_, _) =>
        {
            if (searchCancellation is not null) return;
            try
            {
                var config = AgentSettings.Load(DataDirectory);
                using var dialog = new OpenFileDialog { Title = "face XMLを選択", Filter = "face XML|OQSsiquc01res_face_*.xml|XMLファイル|*.xml", CheckFileExists = true };
                if (!string.IsNullOrWhiteSpace(config.FaceXmlDirectory)) dialog.InitialDirectory = config.FaceXmlDirectory;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                searchCancellation = new CancellationTokenSource();
                search.Enabled = settings.Enabled = false;
                candidates.DataSource = null; identity.Text = "";
                stations.SetState(0, true); status.Text = "face XMLを読み取っています…";
                var face = await FaceXmlParser.LoadAsync(dialog.FileName, config.FaceEncoding, searchCancellation.Token);
                identity.Text = $"XML: {Path.GetFileName(dialog.FileName)}　フリガナ: {face.NameKana}　生年月日: {face.Birthdate:yyyy/MM/dd}";
                stations.SetState(1, true); status.Text = "Dynamicsの患者マスターを検索しています…";
                var result = await DynamicsFacePatientFinder.FindAsync(face, searchCancellation.Token);
                IReadOnlyList<FacePatientMatch> matches = result.Selected is { } selected ? [selected] : result.Candidates;
                candidates.DataSource = matches.Select(m => new { 患者ID = m.PatientId, カルテ番号 = string.Join(", ", m.RawChartNumbers), 氏名 = m.Name, フリガナ = m.NameKana, 生年月日 = m.Birthdate.ToString("yyyy/MM/dd") }).ToList();
                if (result.Selected is not null)
                {
                    stations.SetState(1, false, isCompleted: true);
                    status.Text = $"患者ID {result.Selected.PatientId} を特定しました（{result.Message}）。後続の受付操作はまだ行いません。";
                }
                else if (matches.Count == 0)
                {
                    stations.SetState(1, false, true); status.Text = "一致する患者が見つかりません。フリガナと生年月日を確認してください。";
                }
                else
                {
                    stations.SetState(1, false, true); status.Text = $"同じフリガナ・生年月日の患者IDが{matches.Count}件あります。{result.Message}";
                    MessageBox.Show(this, status.Text + "\r\n候補一覧を確認し、Dynamicsで本人を確認してください。後続処理には進みません。", "患者の確認が必要です", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (OperationCanceledException) { status.Text = "検索を停止しました。"; stations.SetState(-1, false); }
            catch (Exception ex)
            {
                candidates.DataSource = null; stations.SetState(-1, false);
                status.Text = "患者を特定できません: " + ex.Message;
            }
            finally { searchCancellation?.Dispose(); searchCancellation = null; search.Enabled = settings.Enabled = true; }
        };
        commands.Controls.AddRange([search, settings, open]); layout.Controls.Add(commands, 0, 5); Controls.Add(layout);
        FormClosing += (_, e) =>
        {
            if (searchCancellation is not null) { e.Cancel = true; searchCancellation.Cancel(); status.Text = "検索を停止しています。停止後に閉じてください。"; }
            else if (bulk?.IsRunning == true) { e.Cancel = true; MessageBox.Show(this, "Bulk Consoleで一時停止してから終了してください。"); }
        };
    }
}

