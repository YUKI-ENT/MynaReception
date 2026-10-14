using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ReceptionAgent;
using ReceptionAgent.Kiosk;

static class Program
{
    static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Field(MainForm form, string name, object value) => typeof(MainForm).GetField(name, PrivateInstance)!.SetValue(form, value);
    static object? Field(MainForm form, string name) => typeof(MainForm).GetField(name, PrivateInstance)!.GetValue(form);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    static async Task Main()
    {
        await RunFlowEditor();
        await RunFormBuilder();
        await RunReceiptPreview();
        RunReceiptAlignment();
        await RunPosStyleEditor();
        await RunAgentSettingsLayout();
        await RunStartupAndList(false);
        await RunStartupAndList(true);
        await RunStartupAndList(true, true);
        await RunAutomationSettings();
        await Run(false, false);
        await Run(true, false);
        await Run(false, true);
        Console.WriteLine("Windows lifecycle tests passed (isolated synthetic stores).");
    }
    static Task RunReceiptPreview()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var type = typeof(MainForm).Assembly.GetType("ReceptionAgent.KioskReceiptForm", true)!;
                foreach (int width in new[] { 58, 80 })
                {
                    using var form = (Form)Activator.CreateInstance(type, [new KioskReceipt("synthetic", "0012345", "受付 太郎 長い名前の折り返し確認", "ウケツケ タロウ", "0037", DateTimeOffset.Now), true, new PosPrintOptions { PaperWidthMm = width }])!;
                    using var image = (Bitmap)((Bitmap)type.GetField("preview", PrivateInstance)!.GetValue(form)!).Clone();
                    Check(image.Width == width * 10 && image.Height > 500, $"{width}mm dual receipt preview wraps Japanese text without an installed printer");
                    var path = Environment.GetEnvironmentVariable("KIOSK_RECEIPT_SCREENSHOT");
                    if (!string.IsNullOrEmpty(path)) image.Save(Path.ChangeExtension(path, $"{width}.png"), System.Drawing.Imaging.ImageFormat.Png);
                    var custom = new PosPrintOptions { PaperWidthMm = width };
                    custom.PatientStyles[0] = new() { FontName = "Meiryo", FontSizePt = 28, Bold = true, SpaceAfterMm = 5 };
                    using var styledForm = (Form)Activator.CreateInstance(type, [new KioskReceipt("synthetic", "0012345", "受付 太郎 長い名前の折り返し確認", "ウケツケ タロウ", "0037", DateTimeOffset.Now), true, custom])!;
                    var styledImage = (Bitmap)type.GetField("preview", PrivateInstance)!.GetValue(styledForm)!;
                    Check(styledImage.Height > image.Height, $"{width}mm custom font size and spacing increase measured receipt length");
                    if (!string.IsNullOrEmpty(path)) styledImage.Save(Path.ChangeExtension(path, $"styled-{width}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    static Task RunPosStyleEditor()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var original = new PosPrintOptions();
                var type = typeof(MainForm).Assembly.GetType("ReceptionAgent.PosPrintSettingsForm", true)!;
                using var form = (Form)Activator.CreateInstance(type, [original])!;
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                form.Show(); Application.DoEvents();
                IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));
                var grids = Descendants(form).OfType<DataGridView>().ToArray();
                Check(grids.Length == 2 && grids.All(g => g.Columns.Count == 7), "POS editor exposes text, font, size, bold, spacing, alignment and border");
                var grid = grids.Single(g => g.AccessibleName == "患者用の印刷項目");
                grid.Rows[0].Cells[2].Value = 17f; grid.Rows[0].Cells[3].Value = true; grid.Rows[0].Cells[4].Value = 4f;
                grid.Rows[0].Cells[5].Value = PosTextAlignment.Right;
                var staffGrid = grids.Single(g => g.AccessibleName == "スタッフ用の印刷項目");
                Check((bool)staffGrid.Rows[0].Cells[6].Value!, "staff handoff row starts with border enabled");
                staffGrid.Rows[0].Cells[0].Value = "受付スタッフにお渡しください";
                Check(original.PatientStyles.Count == 0, "style edits leave original settings untouched until apply");
                var binding = (System.ComponentModel.IBindingList)grid.DataSource!;
                var added = binding.AddNew()!;
                added.GetType().GetProperty("Text")!.SetValue(added, "追記");
                grid.CurrentCell = grid.Rows[0].Cells[0];
                grid.Parent!.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == "行を下へ").PerformClick();
                Descendants(form).OfType<Button>().Single(b => b.Text == "適用").PerformClick();
                var saved = (PosPrintOptions)type.GetProperty("Options", PrivateInstance)!.GetValue(form)!;
                Check(saved.PatientTemplate.Split('\n')[1] == "{医院名}" && saved.PatientStyles[1].FontSizePt == 17 && saved.PatientStyles[1].Bold && saved.PatientStyles[1].SpaceAfterMm == 4, "moving an item preserves its custom style on apply");
                Check(saved.PatientStyles[1].Alignment == PosTextAlignment.Right, "moving an item preserves selected text alignment on apply");
                Check(saved.StaffTemplate.StartsWith("受付スタッフにお渡し下さい") && saved.StaffStyles[0].Boxed == true, "edited handoff wording and border survive apply");
                Check(saved.StyleFor(true, 0, saved.StaffTemplate.Split('\n')[0]).FontSizePt == saved.StyleFor(false, 0, saved.PatientTemplate.Split('\n')[0]).FontSizePt, "staff font matching uses updated patient values");
                Check(saved.PatientTemplate.EndsWith("追記"), "new editable receipt item is included on apply");
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    static void RunReceiptAlignment()
    {
        var draw = typeof(MainForm).Assembly.GetType("ReceptionAgent.PosReceiptLayout", true)!.GetMethod("Draw")!;
        foreach (float width in new[] { 204f, 291f })
        {
            var positions = new List<int>();
            foreach (var alignment in new[] { PosTextAlignment.Left, PosTextAlignment.Center, PosTextAlignment.Right })
            {
                using var bitmap = new Bitmap(320, 120); bitmap.SetResolution(100, 100);
                using var g = Graphics.FromImage(bitmap); g.Clear(Color.White);
                var options = new PosPrintOptions { PatientTemplate = "{名前}" };
                options.PatientStyles[0] = new() { Alignment = alignment };
                draw.Invoke(null, [g, width, options, new KioskReceipt("test", "11", "名前", "ナマエ", "1", DateTimeOffset.Now), false, false, false]);
                int left = bitmap.Width;
                for (int x = 0; x < bitmap.Width; x++)
                    for (int y = 0; y < bitmap.Height; y++)
                        if (bitmap.GetPixel(x, y).R < 128) left = Math.Min(left, x);
                Check(left < bitmap.Width, $"{alignment} receipt alignment renders visible glyphs");
                positions.Add(left);
            }
            Check(positions[0] < positions[1] && positions[1] < positions[2], $"receipt glyphs move from left to center to right at width {width}");
        }
    }
    static Task RunAgentSettingsLayout()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "agent-settings-layout-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var form = new AgentSettingsForm(root);
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                form.Show(); Application.DoEvents();
                IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));
                foreach (int width in new[] { 850, 720 })
                {
                    form.Width = width; Application.DoEvents();
                    var buttons = Descendants(form).OfType<Button>().Where(b => b.Text == "参照…").ToArray();
                    Check(buttons.Length == 6, "settings exposes six folder selectors");
                    Check(buttons.All(b => b.Right <= b.Parent!.ClientSize.Width && b.Parent.Right <= b.Parent.Parent!.ClientSize.Width), $"folder browse buttons fit settings at width {width}");
                }
                var startup = Descendants(form).OfType<CheckBox>().Single(c => c.Text == "アプリ起動時に監視を開始する");
                startup.Checked = true;
                Descendants(form).OfType<Button>().Single(b => b.Text == "保存").PerformClick();
                Check(AgentSettings.Load(root).StartMonitoringOnLaunch, "startup switch saves from settings form");
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    static Task RunStartupAndList(bool autoStart, bool offlineOqs = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? error = null;
            string root = Path.Combine(Path.GetTempPath(), "agent-startup-" + Guid.NewGuid().ToString("N"));
            try
            {
                string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
                new AgentSettings { FaceXmlDirectory = Folder("face"), FaceTrashDirectory = Folder("trash"),
                    ICallRequestDirectory = Folder("req"), ICallResponseDirectory = Folder("res"),
                    StartMonitoringOnLaunch = autoStart, ReconcileReferenceNumber = false,
                    AutoRegisterReferenceNumber = offlineOqs, OqsRoot = offlineOqs ? Folder("oqs") : "" }.Save(root);
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                new KioskOptions { Port = port }.Save(root);
                var store = new ReceptionAgent.Reception.CaptureStore(Path.Combine(root, "Reception"));
                store.Capture(new ReceptionAgent.Reception.CaptureRecord { FileName = "synthetic.xml", ContentHash = "synthetic",
                    GeneratedAt = DateTime.Today, Stage = ReceptionAgent.Reception.CaptureStage.Completed }, [1]);
                using var form = (MainForm)Activator.CreateInstance(typeof(MainForm), PrivateInstance, null, [root], null)!;
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        if (Field(form, "webStartTask") is Task webStart) await webStart;
                        Check((Field(form, "monitor") is not null) == autoStart, $"launch monitoring follows saved option {autoStart}");
                        var grid = (DataGridView)Field(form, "grid")!;
                        Check(grid.Rows.Count == 1 && grid.Columns["氏名"].Visible && grid.Columns["状態"].Visible, "main list retains patient and status columns");
                        Check(new[] { "ファイル名", "ファイル作成日時", "XML生成日時", "取得日時", "再検証カルテ番号", "再検証" }.All(c => !grid.Columns[c].Visible), "main list hides filename and diagnostic columns");
                        if (offlineOqs)
                        {
                            var monitor = (ReceptionAgent.Reception.FaceCaptureMonitor)Field(form, "monitor")!;
                            var deadline = DateTime.UtcNow.AddSeconds(8);
                            while (!monitor.CaptureStatus.Contains("アクセス待ち") && DateTime.UtcNow < deadline) await Task.Delay(50);
                            Check(!monitor.FoldersAccessible && monitor.CaptureStatus.Contains("アクセス待ち"), "startup with offline OQS enters access wait without a blocking dialog");
                            Check(!(bool)typeof(MainForm).GetProperty("MonitoringReady", PrivateInstance)!.GetValue(form)!, "Kiosk is unavailable while monitoring folders are offline");
                            Directory.CreateDirectory(Path.Combine(root, "oqs", "req")); Directory.CreateDirectory(Path.Combine(root, "oqs", "res"));
                            while (!monitor.FoldersAccessible && DateTime.UtcNow < deadline) await Task.Delay(50);
                            Check(monitor.FoldersAccessible && (bool)typeof(MainForm).GetProperty("MonitoringReady", PrivateInstance)!.GetValue(form)!, "recovery automatically enables Kiosk without restarting the UI monitor");
                        }
                        form.Close();
                    }
                    catch (Exception ex) { error = ex; form.Close(); }
                };
                Application.Run(form);
                if (error is not null) throw error;
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    static Task RunAutomationSettings()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "kiosk-auto-form-" + Guid.NewGuid().ToString("N"));
            try
            {
                var type = typeof(MainForm).Assembly.GetType("ReceptionAgent.KioskAutomationSettingsForm", true)!;
                using var form = (Form)Activator.CreateInstance(type, [root])!;
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                form.Show(); Application.DoEvents();
                IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
                var controls = Descendants(form).ToArray();
                var switches = controls.OfType<CheckBox>().ToArray();
                Check(switches.Length == 3 && switches.All(c => !c.Checked), "automatic settings UI exposes three independent OFF switches");
                var printer = controls.OfType<ComboBox>().Single();
                Check(!printer.Enabled, "printer selection disabled when auto-print OFF");
                switches.Single(c => c.Text.Contains("自動発券")).Checked = true; Application.DoEvents();
                Check(printer.Enabled, "58mm auto-print enables printer selection");
                switches.Single(c => c.Text.Contains("自動発券")).Checked = false;
                switches.Single(c => c.Text.Contains("連携を自動")).Checked = true;
                controls.OfType<Button>().Single(c => c.Text == "保存").PerformClick(); Application.DoEvents();
                Check(KioskOptions.Load(root).Automation.AutoLink && !KioskOptions.Load(root).Automation.AutoPrint,
                    "automation form saves switches without sending to a printer");
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    static Task RunFlowEditor()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var source = new KioskFlow();
                var type = typeof(MainForm).Assembly.GetType("ReceptionAgent.KioskFlowAdvancedEditorForm", throwOnError: true)!;
                using var editor = (Form)Activator.CreateInstance(type, [source])!;
                editor.ShowInTaskbar = false; editor.StartPosition = FormStartPosition.Manual; editor.Location = new(-32000, -32000);
                editor.Show(); Application.DoEvents();
                IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
                var controls = Descendants(editor).ToArray();
                var pages = controls.OfType<DataGridView>().Single(g => g.DataSource is System.ComponentModel.BindingList<KioskFlowPage>);
                pages.CurrentCell = pages.Rows[2].Cells[0]; Application.DoEvents();
                var choices = controls.OfType<DataGridView>().Single(g => g.DataSource is System.ComponentModel.BindingList<KioskFlowChoice>);
                var routes = controls.OfType<DataGridView>().Single(g => g.DataSource is System.ComponentModel.BindingList<KioskFlowRoute>);
                Check(choices.Enabled && choices.Rows.Count >= 2 && routes.Enabled &&
                    ((System.ComponentModel.BindingList<KioskFlowChoice>)choices.DataSource!).Count == 2,
                    "flow editor binds selected question choices and routes");
                choices.Rows[0].Cells[1].Value = "予約しています"; choices.EndEdit(); Application.DoEvents();
                var draft = (KioskFlow)type.GetProperty("Flow")!.GetValue(editor)!;
                Check(draft.Page("reserved").Choices[0].Label == "予約しています" && source.Page("reserved").Choices[0].Label == "予約あり", "flow editor edits private draft without mutating original settings");
                pages.CurrentCell = pages.Rows[5].Cells[0]; Application.DoEvents();
                Check(!choices.Enabled && !routes.Enabled, "guidance page disables question and route grids");
                editor.Close();
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    static Task RunFormBuilder()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var source = new KioskFlow();
                var type = typeof(MainForm).Assembly.GetType("ReceptionAgent.KioskFlowEditorForm", true)!;
                using var form = (Form)Activator.CreateInstance(type, [source])!;
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                form.Show(); Application.DoEvents();
                T ControlField<T>(string name) => (T)type.GetField(name, PrivateInstance)!.GetValue(form)!;
                var list = ControlField<ListBox>("pages"); list.SelectedIndex = 2; Application.DoEvents();
                var choices = ControlField<DataGridView>("choices"); var field = ControlField<ComboBox>("field");
                Check(choices.Enabled && choices.Columns.Count == 2 && (string?)field.SelectedValue == "saysReserved", "form builder shows question choices and human-readable answer field");
                choices.Rows[0].Cells[0].Value = "予約しています"; choices.EndEdit();
                list.SelectedIndex = 1; Application.DoEvents();
                IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(Descendants(x)));
                Descendants(form).OfType<Button>().Single(b => b.Text == "外来区分を追加").PerformClick(); Application.DoEvents();
                var flow = (KioskFlow)type.GetProperty("Flow")!.GetValue(form)!;
                var clinic = flow.Pages.Single(p => p.Variable == "clinicClass");
                Check(flow.Page("birthday").NextPageId == clinic.Id && clinic.NextPageId == "reserved" && clinic.Choices.Select(c => c.Value).SequenceEqual(["general", "vaccination", "special"]), "clinic template inserts and connects an outpatient question automatically");
                flow.Validate();
                Check(flow.Page("reserved").Choices[0].Label == "予約しています" && source.Page("reserved").Choices[0].Label == "予約あり", "form builder saves question edits in private draft");
                var routes = ControlField<DataGridView>("routes"); list.SelectedItem = flow.Page("fever"); Application.DoEvents();
                Check(routes.Columns.Cast<DataGridViewColumn>().All(c => c is DataGridViewComboBoxColumn), "conditional routes use selections rather than typed page IDs");
                routes.Rows[0].Cells[0].Value = "clinicClass"; Application.DoEvents();
                routes.Rows[0].Cells[1].Value = "vaccination"; routes.EndEdit(); Application.DoEvents();
                list.SelectedItem = clinic; Application.DoEvents();
                Check(flow.Page("fever").Routes[0].Variable == "clinicClass" && flow.Page("fever").Routes[0].Value == "vaccination", "display condition selections persist to executable routing rules");
                flow.Validate();
                if (Environment.GetEnvironmentVariable("KIOSK_BUILDER_SCREENSHOT") is { Length: > 0 } screenshot)
                {
                    list.SelectedItem = clinic; Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
                }
                form.Close(); done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
    static async Task Run(bool activeMonitor, bool stalled)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? error = null;
            ThreadExceptionEventHandler onError = (_, e) => { error = e.Exception; Application.ExitThread(); };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += onError;
            CancellationTokenSource? monitorCancellation = null;
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-lifecycle-" + Guid.NewGuid().ToString("N"));
                using var portLease = new TcpListener(IPAddress.Loopback, 0); portLease.Start();
                int port = ((IPEndPoint)portLease.LocalEndpoint).Port; portLease.Stop();
                new KioskOptions { Port = port }.Save(root);
                using var form = (MainForm)Activator.CreateInstance(typeof(MainForm), PrivateInstance, null, [root], null)!;
                // Exercise the real WinForms message loop without a visible operator window.
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-32000, -32000);
                int ticks = 0, closed = 0, cancelled = 0;
                using var heartbeat = new System.Windows.Forms.Timer { Interval = 40 };
                heartbeat.Tick += (_, _) => ticks++;
                form.FormClosed += (_, _) => closed++;
                var duration = new Stopwatch();
                var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        if (Field(form, "webStartTask") is Task webStart) await webStart;
                        Check(Field(form, "kioskServer") is not null, "real Web server started before close");
                        if (activeMonitor)
                        {
                            monitorCancellation = new();
                            monitorCancellation.Token.Register(() => { cancelled++; work.TrySetCanceled(); });
                            Field(form, "cancellation", monitorCancellation); Field(form, "monitorTask", work.Task);
                        }
                        else if (stalled) Field(form, "monitorTask", work.Task);
                        heartbeat.Start(); duration.Start();
                        form.Close(); form.Close(); // Repeated clicks must join one shutdown.
                    }
                    catch (Exception ex) { error = ex; Application.ExitThread(); }
                };
                Application.Run(form);
                if (error is not null) throw error;
                Check(closed == 1, "repeated close requests close form exactly once");
                Check(Field(form, "kioskServer") is null, "Web server ownership cleared after shutdown");
                portLease.Start(); portLease.Stop();
                Check(true, "Web server listening port released after shutdown");
                if (activeMonitor) Check(cancelled == 1, "monitor cancellation requested exactly once");
                if (stalled)
                {
                    Check(duration.Elapsed >= TimeSpan.FromSeconds(7) && duration.Elapsed < TimeSpan.FromSeconds(12), "stalled background operation bounded by eight-second shutdown wait");
                    Check(ticks > 50, "WinForms message loop remains responsive during shutdown wait");
                    work.TrySetResult();
                }
                else Check(duration.Elapsed < TimeSpan.FromSeconds(4), "normal shutdown completes without UI deadlock");
                typeof(Form).GetMethod("OnFormClosed", PrivateInstance)!.Invoke(form, [new FormClosedEventArgs(CloseReason.UserClosing)]);
                Check(true, "repeated FormClosed does not cancel disposed token source");
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { monitorCancellation?.Dispose(); Application.ThreadException -= onError; }
        }) { IsBackground = true, Name = "ReceptionAgent lifecycle test STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
