using System.Data;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ReceptionAgent;

// All Access/DAO references stay on this STA; only managed snapshots leave it.
internal static class DynamicsComReader
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<Task<Control>> Dispatcher = new(Start);
    private static Task<Control> Start()
    {
        var ready = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new Control(); _ = control.Handle;
                var filter = new MessageFilter();
                Marshal.ThrowExceptionForHR(CoRegisterMessageFilter(filter, out var previous));
                try { ready.SetResult(control); Application.Run(); }
                finally { CoRegisterMessageFilter(previous, out _); Release(previous); GC.KeepAlive(filter); }
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "ReceptionAgent Dynamics STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task;
    }
    public static async Task<T> Dispatch<T>(Func<T> operation, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var control = await Dispatcher.Value.ConfigureAwait(false);
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            control.BeginInvoke(new Action(() =>
            {
                try { token.ThrowIfCancellationRequested(); completion.SetResult(operation()); }
                catch (Exception ex) { completion.SetException(ex); }
            }));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }
    public static Task<DataTable> ReadAsync(string sql, CancellationToken token) => Dispatch(() => Read(sql), token);
    private static object Attach()
    {
        Marshal.ThrowExceptionForHR(CLSIDFromProgID("Access.Application", out var clsid));
        int hr = GetActiveObject(ref clsid, IntPtr.Zero, out var application);
        if (hr < 0) throw new InvalidOperationException("起動中のDynamics（Access）に接続できません。「患者マスター」を開いてください。");
        return application!;
    }
    private static object Property(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, args)!;
    private static object Method(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, target, args)!;
    private static DataTable Read(string sql)
    {
        object? app = null, forms = null, form = null, database = null, recordset = null, fields = null;
        var table = new DataTable();
        try
        {
            app = Attach(); forms = Property(app, "Forms"); form = Property(forms, "Item", "患者マスター");
            // Access through the opened form confirms the intended application context. Never assign RecordSource.
            string recordSource = Convert.ToString(Property(form, "RecordSource")) ?? "";
            if (recordSource.Length == 0) throw new InvalidOperationException("患者マスターフォームのRecordSourceが空です。");
            table.ExtendedProperties["RecordSource"] = recordSource;
            database = Method(app, "CurrentDb");
            recordset = Method(database, "OpenRecordset", sql, 4, 4); // DAO snapshot, read-only
            fields = Property(recordset, "Fields"); int count = Convert.ToInt32(Property(fields, "Count"));
            for (int i = 0; i < count; i++)
            {
                object? field = null;
                try
                {
                    field = Property(fields, "Item", i);
                    var column = table.Columns.Add(Convert.ToString(Property(field, "Name"))!, typeof(object));
                    column.ExtendedProperties["DaoType"] = Convert.ToInt32(Property(field, "Type"));
                }
                finally { Release(field); }
            }
            while (!Convert.ToBoolean(Property(recordset, "EOF")))
            {
                var values = (Array)Method(recordset, "GetRows", 64);
                if (values.GetLength(1) == 0) throw new InvalidOperationException("DAOの読み取りが進みません。");
                for (int row = 0; row < values.GetLength(1); row++)
                {
                    if (table.Rows.Count >= 100) throw new InvalidOperationException("患者候補が多すぎます。取得条件を確認してください。");
                    var data = new object[count];
                    for (int col = 0; col < count; col++) data[col] = values.GetValue(col + values.GetLowerBound(0), row + values.GetLowerBound(1)) ?? DBNull.Value;
                    table.Rows.Add(data);
                }
            }
            return table;
        }
        catch (TargetInvocationException ex) { table.Dispose(); throw new InvalidOperationException("Dynamicsのフォーム・クエリを読み取れません: " + ex.InnerException?.Message, ex); }
        catch { table.Dispose(); throw; }
        finally
        {
            Release(fields);
            if (recordset is not null) { try { Method(recordset, "Close"); } catch { } Release(recordset); }
            // Do not close the borrowed CurrentDb or Access application.
            Release(database); Release(form); Release(forms); Release(app);
        }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string name, out Guid clsid);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? value);
    [DllImport("ole32.dll")] private static extern int CoRegisterMessageFilter(IOleMessageFilter? filter, out IOleMessageFilter? previous);
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int type, IntPtr caller, int ticks, IntPtr info);
        [PreserveSig] int RetryRejectedCall(IntPtr callee, int ticks, int rejectType);
        [PreserveSig] int MessagePending(IntPtr callee, int ticks, int pendingType);
    }
    private sealed class MessageFilter : IOleMessageFilter
    {
        public int HandleInComingCall(int type, IntPtr caller, int ticks, IntPtr info) => 0;
        public int RetryRejectedCall(IntPtr callee, int ticks, int rejectType) => rejectType == 2 && ticks < 1500 ? 100 : -1;
        public int MessagePending(IntPtr callee, int ticks, int pendingType) => 2;
    }
}
