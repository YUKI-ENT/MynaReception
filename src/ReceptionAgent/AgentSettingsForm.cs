namespace ReceptionAgent;

public sealed class AgentSettingsForm : Form
{
    public AgentSettingsForm()
    {
        Text = "ReceptionAgent 設定"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(800, 580); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var current = AgentSettings.Load(MainForm.DataDirectory);
        if (!Enum.IsDefined(current.FaceEncoding)) throw new InvalidDataException("face XMLの文字コード設定が不正です。");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, AutoScroll = true };
        TextBox Folder(string label, string value)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true });
            var line = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            line.ColumnStyles.Add(new(SizeType.Percent, 100)); line.ColumnStyles.Add(new(SizeType.AutoSize));
            var path = new TextBox { Dock = DockStyle.Fill, Text = value, AccessibleName = label };
            var browse = new Button { Text = "参照…", AutoSize = true };
            browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath; };
            line.Controls.Add(path, 0, 0); line.Controls.Add(browse, 1, 0); layout.Controls.Add(line); return path;
        }
        var oqs = Folder("OQS連携フォルダー（照会番号単件登録用／req・res の親）", current.OqsRoot);
        var face = Folder("face XML出力フォルダー", current.FaceXmlDirectory);
        var trash = Folder("Dynamics取込後のtrashフォルダー", current.FaceTrashDirectory);
        var request = Folder("iCallManager requestフォルダー（req）", current.ICallRequestDirectory);
        var response = Folder("iCallManager responseフォルダー（res）", current.ICallResponseDirectory);
        layout.Controls.Add(new Label { Text = "face XMLの文字コード", AutoSize = true });
        var encoding = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, AccessibleName = "face XMLの文字コード" };
        encoding.Items.AddRange(["UTF-8", "Shift_JIS（CP932）"]);
        encoding.SelectedIndex = current.FaceEncoding == Face.FaceXmlEncoding.Utf8 ? 0 : 1; layout.Controls.Add(encoding);

        layout.Controls.Add(new Label { AutoSize = true, Text = "XML取得・患者照合・予約検索は自動です。来院確認・連携・照会番号登録は一覧から手動で要求します。\r\niCallManager側の「実操作を有効化」も必要です。自動取込はファイル名の日付が当日のXMLです。" });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                new AgentSettings { OqsRoot = oqs.Text, FaceXmlDirectory = face.Text, FaceTrashDirectory = trash.Text,
                    ICallRequestDirectory = request.Text, ICallResponseDirectory = response.Text, MarkArrived = false, LinkReservation = false,
                    FaceEncoding = encoding.SelectedIndex == 0 ? Face.FaceXmlEncoding.Utf8 : Face.FaceXmlEncoding.ShiftJis }.Save(MainForm.DataDirectory);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons); Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
}
