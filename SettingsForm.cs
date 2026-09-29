using iCallManager.Core;

namespace iCallManager;

public sealed class SettingsForm : Form
{
    private readonly TextBox folder = new() { Dock = DockStyle.Fill };
    private readonly Label preview = new() { AutoSize = true, Dock = DockStyle.Fill };
    public string? SavedBridgeDirectory { get; private set; }

    public SettingsForm(string activeDirectory, Icon? icon)
    {
        Text = "iCallManager 設定";
        Icon = icon;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(720, 320);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        var active = new TextBox { ReadOnly = true, Text = activeDirectory, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, TabStop = false };
        var title = new Label { Text = "API連携フォルダー（このPC上のフォルダー）", AutoSize = true, Margin = new Padding(3, 14, 3, 5) };
        var activePanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        activePanel.Controls.Add(new Label { Text = "現在使用中", AutoSize = true });
        activePanel.Controls.Add(active);
        layout.Controls.Add(activePanel, 0, 0); layout.SetColumnSpan(activePanel, 2);
        layout.Controls.Add(title, 0, 1); layout.SetColumnSpan(title, 2);
        folder.Text = AppSettings.Load().BridgeDirectory;
        folder.AccessibleName = "API連携フォルダー";
        var browse = new Button { Text = "参照…", AutoSize = true };
        layout.Controls.Add(folder, 0, 2); layout.Controls.Add(browse, 1, 2);
        preview.Margin = new Padding(3, 8, 3, 8);
        layout.Controls.Add(preview, 0, 3); layout.SetColumnSpan(preview, 2);
        var note = new Label
        {
            Text = "保存後、アプリを終了して起動し直すと反映されます。\r\n旧フォルダーの要求・応答は移動しません。\r\n別PCから接続する場合は、指定先のWindows共有設定も行ってください。",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8)
        };
        layout.Controls.Add(note, 0, 4); layout.SetColumnSpan(note, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "保存（次回起動から反映）", AutoSize = true };
        buttons.Controls.AddRange([cancel, save]);
        layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        folder.TextChanged += (_, _) => UpdatePreview();
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "API連携フォルダーを選択", UseDescriptionForTitle = true };
            try { picker.SelectedPath = AppSettings.NormalizeBridgeDirectory(folder.Text); } catch (Exception) { }
            if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath;
        };
        save.Click += (_, _) =>
        {
            try
            {
                // Reload persisted settings to preserve other options, leaving the running instance unchanged.
                var saved = AppSettings.Load();
                saved.BridgeDirectory = AppSettings.PrepareBridgeDirectory(folder.Text);
                saved.Save();
                SavedBridgeDirectory = saved.BridgeDirectory;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
            { MessageBox.Show(this, "設定を保存できません。\r\n" + ex.Message, "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        try
        {
            string path = AppSettings.NormalizeBridgeDirectory(folder.Text);
            preview.Text = "要求: " + Path.Combine(path, "request") + "\r\n応答: " + Path.Combine(path, "response");
            preview.ForeColor = SystemColors.ControlText;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        { preview.Text = ex.Message; preview.ForeColor = Color.Firebrick; }
    }
}
