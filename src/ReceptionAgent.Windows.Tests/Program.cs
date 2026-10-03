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
        await Run(false, false);
        await Run(true, false);
        await Run(false, true);
        Console.WriteLine("Windows lifecycle tests passed (isolated synthetic stores).");
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
