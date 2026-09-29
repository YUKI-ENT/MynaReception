using ReceptionAgent.Oqs;
using ReceptionAgent.Oqs.ReferenceNumber;

namespace ReceptionAgent;

public sealed class ReferenceRequestForm : Form
{
    private readonly Dictionary<string, TextBox> inputs = [];
    private readonly TextBox preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button save = new() { Text = "ローカルWorkへ保存", AutoSize = true, Enabled = false };
    private byte[]? preparedXml;
    private string identifier = "";
    private static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReceptionAgent");

    public ReferenceRequestForm()
    {
        Text = "ReceptionAgent — 照会番号登録要求の作成";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(920, 760);
        MinimumSize = new Size(720, 650);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "照会番号登録：1患者分の健康保険情報から要求XMLを作成します。\r\n現在は手入力による生成確認用です。OQSサーバーへの送信・登録は行いません。",
            AutoSize = true, Margin = new Padding(3, 3, 3, 12) }, 0, 0);
        var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
        fields.ColumnStyles.Add(new(SizeType.AutoSize));
        fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var (key, label) in new[] { ("institution", "医療機関コード"), ("patient", "枝番なし患者ID（照会番号）"),
            ("insurer", "保険者番号"), ("symbol", "被保険者証記号（なしの場合は空欄）"), ("number", "被保険者証番号"), ("branch", "枝番") })
        {
            int row = fields.RowCount++;
            var input = new TextBox { Dock = DockStyle.Fill, AccessibleName = label };
            inputs[key] = input;
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, row);
            fields.Controls.Add(input, 1, row);
            input.TextChanged += (_, _) => { preparedXml = null; save.Enabled = false; preview.Clear(); status.Text = "入力変更後はXMLを再作成してください。"; };
        }
        layout.Controls.Add(fields, 0, 1);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var build = new Button { Text = "XML作成・プレビュー", AutoSize = true };
        var clear = new Button { Text = "入力をクリア", AutoSize = true };
        commands.Controls.AddRange([build, save, clear]);
        layout.Controls.Add(commands, 0, 2);
        layout.Controls.Add(preview, 0, 3);
        layout.Controls.Add(status, 0, 4);
        Controls.Add(layout);
        build.Click += (_, _) =>
        {
            preparedXml = null; save.Enabled = false; preview.Clear();
            try
            {
                string Value(string key) => inputs[key].Text.Trim();
                var target = new ReferenceRegistrationTarget(Value("patient"), Value("insurer"), Value("symbol"), Value("number"), Value("branch"));
                identifier = ReferenceNumberRequestBuilder.CreateIdentifier();
                preparedXml = new ReferenceNumberRequestBuilder().Build(Value("institution"), identifier, target);
                preview.Text = ReferenceNumberRequestBuilder.Preview(preparedXml);
                save.Enabled = true;
                status.Text = "生成済み（未送信）。内容を確認して保存できます。";
            }
            catch (Exception ex) { status.Text = "生成できません: " + ex.Message; }
        };
        save.Click += (_, _) =>
        {
            if (preparedXml is null) return;
            try
            {
                string work = Path.Combine(DataDirectory, "Work", "ReferenceNumber");
                Directory.CreateDirectory(work);
                string sequence = new OqsRequestSequence(DataDirectory).Next(DateOnly.FromDateTime(DateTime.Now));
                string path = Path.Combine(work, "OQSmuimm01req_" + sequence + ".xml");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(preparedXml); stream.Flush(true); }
                save.Enabled = false;
                preparedXml = null;
                status.Text = "保存済み（未送信）: " + path;
            }
            catch (Exception ex) { status.Text = "保存できません: " + ex.Message; }
        };
        clear.Click += (_, _) => { foreach (var input in inputs.Where(p => p.Key != "institution").Select(p => p.Value)) input.Clear(); preview.Clear(); preparedXml = null; save.Enabled = false; status.Text = "患者入力をクリアしました。"; };
        inputs["institution"].ReadOnly = true;
        Shown += async (_, _) =>
        {
            build.Enabled = false;
            try
            {
                inputs["institution"].Text = await DynamicsPatientProvider.ReadInstitutionCodeAsync(CancellationToken.None);
                status.Text = "医療機関コードをDynamicsから取得しました。この画面ではローカルXMLのみ作成します。";
                build.Enabled = true;
            }
            catch (Exception ex) { status.Text = "医療機関コードを取得できません: " + ex.Message + " 接続後に画面を開き直してください。"; }
        };
        status.Text = "医療機関コードをDynamicsから取得します。";
    }
}

