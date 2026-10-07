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
        var openResponse = new CheckBox { AutoSize = true, Text = "監視開始時にエクスプローラで開く", Checked = current.OpenResponseFolderOnMonitorStart };
        layout.Controls.Add(openResponse);
        layout.Controls.Add(new Label { Text = "受付連携方式（一覧の連携ボタン）", AutoSize = true });
        var linkMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, AccessibleName = "受付連携方式" };
        linkMode.Items.AddRange(["iCall連携", "直接出力（Shift_JIS / CP932）", "連携しない"]);
        linkMode.SelectedIndex = (int)current.ReceptionLinkMode; layout.Controls.Add(linkMode);
        var receipt = Folder("Dynamics受付出力フォルダー（receipt.txt）", current.DynamicsReceiptDirectory);
        receipt.Enabled = current.ReceptionLinkMode == Reception.ReceptionLinkMode.DirectOutput;
        linkMode.SelectedIndexChanged += (_, _) => receipt.Enabled = linkMode.SelectedIndex == 1;
        var automation = new Button { Text = "Kiosk自動受付設定…（発券・連携・来院確認）", AutoSize = true };
        automation.Click += (_, _) => { using var dialog = new KioskAutomationSettingsForm(MainForm.DataDirectory); dialog.ShowDialog(this); };
        layout.Controls.Add(automation);
        layout.Controls.Add(new Label { Text = "face XMLの文字コード", AutoSize = true });
        var encoding = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, AccessibleName = "face XMLの文字コード" };
        encoding.Items.AddRange(["UTF-8", "Shift_JIS（CP932）"]);
        encoding.SelectedIndex = current.FaceEncoding == Face.FaceXmlEncoding.Utf8 ? 0 : 1; layout.Controls.Add(encoding);

        var autoRegister = new CheckBox { AutoSize = true, Checked = current.AutoRegisterReferenceNumber,
            Text = "Dynamicsでカルテ番号を取得した場合、照会番号を自動登録する" };
        layout.Controls.Add(autoRegister);
        var reconcile = new CheckBox { AutoSize = true, Checked = current.ReconcileReferenceNumber,
            Text = "当日の患者を低頻度でDynamics再検証する（自動登録ON時は照会番号も登録・訂正）" };
        layout.Controls.Add(reconcile);
        layout.Controls.Add(new Label { AutoSize = true, Text = "再検証は監視中の当日保存済みXMLが対象。取込5分後からWKO資格確認結果表示の確定カルテ番号を照合し、整合後は終了します。" });
        layout.Controls.Add(new Label { AutoSize = true, Text = "受付連携方式は次の連携操作から適用します。受付済みのファイル出力は設定変更後も配送を続けます。他の取込設定は次回監視開始後に取り込むXMLへ適用します。" });
        layout.Controls.Add(new Label { AutoSize = true, Text = "XML取得・患者照合・予約検索は自動です。来院確認・連携は一覧から手動、またはKiosk自動受付設定で要求します。照会番号登録は手動、または上記設定で自動です。\r\n来院確認・iCall連携にはiCallManager側の「実操作を有効化」が必要です。直接出力の配送はアプリ起動中に継続します。自動取込は当日のXMLです。" });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "保存", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                new AgentSettings { OqsRoot = oqs.Text, FaceXmlDirectory = face.Text, FaceTrashDirectory = trash.Text,
                    AutoRegisterReferenceNumber = autoRegister.Checked, ReconcileReferenceNumber = reconcile.Checked, ICallRequestDirectory = request.Text, ICallResponseDirectory = response.Text, MarkArrived = false, LinkReservation = false,
                    OpenResponseFolderOnMonitorStart = openResponse.Checked,
                    ReceptionLinkMode = (Reception.ReceptionLinkMode)linkMode.SelectedIndex, DynamicsReceiptDirectory = receipt.Text,
                    FaceEncoding = encoding.SelectedIndex == 0 ? Face.FaceXmlEncoding.Utf8 : Face.FaceXmlEncoding.ShiftJis }.Save(MainForm.DataDirectory);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons); Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
}
