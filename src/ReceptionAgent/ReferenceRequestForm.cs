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
    private bool busy;
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
        layout.Controls.Add(new Label { Text = "診察券番号を指定して、Dynamicsから保険情報を取得しXMLを作成します。\r\n複数のカルテ枝番は主保険を採用します。この画面では送信・登録は行いません。",
            AutoSize = true, Margin = new Padding(3, 3, 3, 12) }, 0, 0);
        var fields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
        fields.ColumnStyles.Add(new(SizeType.AutoSize));
        fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var (key, label) in new[] { ("institution", "医療機関コード"), ("patient", "診察券番号（枝番なし患者ID）"), ("raw", "採用カルテ番号"),
            ("insurer", "保険者番号"), ("symbol", "被保険者証記号（なしの場合は空欄）"), ("number", "被保険者証番号"), ("branch", "枝番") })
        {
            int row = fields.RowCount++;
            var input = new TextBox { Dock = DockStyle.Fill, AccessibleName = label, ReadOnly = key != "patient" };
            inputs[key] = input;
            fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, row);
            fields.Controls.Add(input, 1, row);
            input.TextChanged += (_, _) => { preparedXml = null; save.Enabled = false; preview.Clear(); status.Text = "入力変更後はXMLを再作成してください。"; };
        }
        layout.Controls.Add(fields, 0, 1);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var build = new Button { Text = "COM取得・XMLプレビュー", AutoSize = true };
        var clear = new Button { Text = "入力をクリア", AutoSize = true };
        commands.Controls.AddRange([build, save, clear]);
        layout.Controls.Add(commands, 0, 2);
        layout.Controls.Add(preview, 0, 3);
        layout.Controls.Add(status, 0, 4);
        Controls.Add(layout);
        build.Click += async (_, _) =>
        {
            preparedXml = null; save.Enabled = false; preview.Clear();
            busy = true; build.Enabled = clear.Enabled = inputs["patient"].Enabled = false;
            try
            {
                foreach (var input in inputs.Where(p => p.Key != "patient").Select(p => p.Value)) input.Clear();
                status.Text = "Dynamicsから取得しています…";
                string institution = await DynamicsPatientProvider.ReadInstitutionCodeAsync(CancellationToken.None);
                var patient = await new DynamicsPatientProvider("枝番", true, institution).GetPatientAsync(inputs["patient"].Text.Trim());
                if (patient is null) throw new InvalidOperationException("対象患者が見つかりません。");
                var info = patient.Insurance;
                inputs["institution"].Text = institution; inputs["raw"].Text = patient.Identity.RawChartNo;
                inputs["insurer"].Text = info.InsurerNumber; inputs["symbol"].Text = info.InsuredCardSymbol;
                inputs["number"].Text = info.InsuredIdentificationNumber; inputs["branch"].Text = info.InsuredBranchNumber;
                var target = new ReferenceRegistrationTarget(patient.Identity.PatientId, info.InsurerNumber, info.InsuredCardSymbol, info.InsuredIdentificationNumber, info.InsuredBranchNumber, info.IsPublicAssistance);
                identifier = ReferenceNumberRequestBuilder.CreateIdentifier();
                preparedXml = new ReferenceNumberRequestBuilder().Build(institution, identifier, target);
                preview.Text = ReferenceNumberRequestBuilder.Preview(preparedXml);
                save.Enabled = true;
                status.Text = "生成済み（未送信）。内容を確認して保存できます。";
            }
            catch (Exception ex) { status.Text = "生成できません: " + ex.Message; }
            finally { busy = false; build.Enabled = clear.Enabled = inputs["patient"].Enabled = true; }
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
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        status.Text = "診察券番号を入力してCOM取得・XMLプレビューを押してください。";
    }
}

