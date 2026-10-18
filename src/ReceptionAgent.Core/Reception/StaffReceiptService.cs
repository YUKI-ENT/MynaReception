using ReceptionAgent.Kiosk;

namespace ReceptionAgent.Reception;

/// <summary>Unattended printing after staff-mode XML lookup, with durable protection against uncertain reprints.</summary>
public sealed class StaffReceiptService(CaptureStore store, Func<AgentSettings> settings,
    Action<KioskReceipt, PosPrintOptions> print, Func<bool> available, Func<string, bool> hasKioskJob,
    Func<DateTimeOffset>? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.Now;
    public async Task RecordManualPrintAsync(string id, CancellationToken token)
    {
        await store.WorkflowGate.WaitAsync(token);
        try
        {
            var record = store.Get(id);
            if (record.Conflict || record.OperationMode != ReceptionOperationMode.Staff) return;
            record.StaffReceiptState = StaffReceiptState.Completed;
            record.StaffReceiptSpooledAt = Now;
            record.StaffReceiptMessage = "手動で2枚を印刷キューへ送信済み";
            store.Save(record);
        }
        finally { store.WorkflowGate.Release(); }
    }
    public async Task RunOnceAsync(CancellationToken token)
    {
        if (!available() || !await gate.WaitAsync(0, token)) return;
        IDisposable? lease = null;
        try
        {
            try { lease = store.AcquireStaffReceiptLease(); } catch (IOException) { return; }
            foreach (var row in store.List(limit: int.MaxValue, date: DateOnly.FromDateTime(Now.LocalDateTime)).OrderBy(r => r.CapturedAt))
            {
                token.ThrowIfCancellationRequested();
                var config = settings();
                if (!available() || config.OperationMode != ReceptionOperationMode.Staff || !config.PosPrint.Enabled) return;
                if (row.OperationMode != ReceptionOperationMode.Staff || row.StaffReceiptState is StaffReceiptState.Completed or StaffReceiptState.NeedsReview) continue;
                await store.WorkflowGate.WaitAsync(token);
                try
                {
                    var record = store.Get(row.Id);
                    if (record.Conflict) continue;
                    if (record.StaffReceiptState == StaffReceiptState.Running)
                    {
                        record.StaffReceiptState = StaffReceiptState.NeedsReview;
                        record.StaffReceiptMessage = "発券結果不明／用紙と印刷キューを確認して手動で再印刷してください";
                        store.Save(record); continue;
                    }
                    if (record.StaffReceiptState != StaffReceiptState.None || !KioskReceipt.CanPrintStaff(record) ||
                        record.KioskAutomaticSessionId.Length > 0 || hasKioskJob(record.Id)) continue;
                    var receipt = KioskReceipt.CreateStaff(record);
                    token.ThrowIfCancellationRequested();
                    record.StaffReceiptState = StaffReceiptState.Running;
                    record.StaffReceiptMessage = "POS発券を送信しています";
                    store.Save(record); // Commit before contacting the spooler. Never blindly repeat after interruption.
                    try
                    {
                        print(receipt, config.PosPrint);
                        record.StaffReceiptState = StaffReceiptState.Completed;
                        record.StaffReceiptSpooledAt = Now;
                        record.StaffReceiptMessage = "2枚を印刷キューへ送信済み";
                        store.Log("staff_receipt_spooled", record.Id, new { copies = 2 });
                    }
                    catch (Exception ex)
                    {
                        record.StaffReceiptState = StaffReceiptState.NeedsReview;
                        record.StaffReceiptMessage = "発券要確認／用紙・印刷キューを確認: " + ex.Message;
                        store.Log("staff_receipt_review", record.Id, new { error = ex.GetType().Name });
                    }
                    store.Save(record);
                }
                finally { store.WorkflowGate.Release(); }
            }
        }
        finally { lease?.Dispose(); gate.Release(); }
    }
}
