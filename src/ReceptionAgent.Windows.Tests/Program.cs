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
                using var form = (Form)Activator.CreateInstance(type, [new KioskReceipt("synthetic", "12345", "受付 太郎", "ウケツケ タロウ", "37", DateTimeOffset.Now), true])!;
                using var image = (Bitmap)((Bitmap)type.GetField("preview", PrivateInstance)!.GetValue(form)!).Clone();
                Check(image.Width == 580 && image.Height == 1100, "58mm dual receipt preview renders without an installed printer");
                var path = Environment.GetEnvironmentVariable("KIOSK_RECEIPT_SCREENSHOT");
                if (!string.IsNullOrEmpty(path)) image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
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
