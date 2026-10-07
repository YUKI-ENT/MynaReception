using ReceptionAgent.Face;
using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

/// <summary>Runs only jobs committed by an eligible Kiosk session. Side effects use durable action states.</summary>
public sealed class KioskAutomationService(KioskSessions sessions, CaptureStore captures,
    Func<FaceIdentity, CancellationToken, Task<FaceLookupResult>> lookup,
    Action<KioskReceipt, string> print, Func<bool> available, Func<DateTimeOffset>? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.Now;
    public async Task RunOnceAsync(CancellationToken token)
    {
        if (!available() || !await gate.WaitAsync(0, token)) return;
        try
        {
            using var lease = sessions.AcquireAutomationLease();
            foreach (var session in sessions.AutomationCandidates())
            {
                token.ThrowIfCancellationRequested();
                if (!available()) return;
                await ProcessAsync(session, token);
            }
        }
        finally { gate.Release(); }
    }
    private async Task ProcessAsync(KioskSession session, CancellationToken token)
    {
        var job = session.Automation!;
        void Save() => sessions.SaveAutomation(session.Id, session.Device, job);
        void Review(string message)
        {
            if (job.Link is KioskAutoActionState.Pending or KioskAutoActionState.Running) job.Link = KioskAutoActionState.NeedsReview;
            else if (job.Arrival is KioskAutoActionState.Pending or KioskAutoActionState.Running) job.Arrival = KioskAutoActionState.NeedsReview;
            else if (job.Print is KioskAutoActionState.Pending or KioskAutoActionState.Running) job.Print = KioskAutoActionState.NeedsReview;
            job.NeedsReview = true; job.Message = message; Save();
            captures.Log("kiosk_automation_review", session.CaptureId ?? "", new { sessionId = session.Id });
        }
        try
        {
            var capture = captures.Get(session.CaptureId!);
            if (!KioskAutomationEligibility.IsEligible(session, capture, Now))
            { Review("自動受付条件が変わりました。患者・予約・処理結果を職員が確認してください。"); return; }
            // Printing cannot be acknowledged atomically with a Windows spooler submission.
            // An interrupted print is never automatically sent again.
            if (job.Print == KioskAutoActionState.Running)
            { job.Print = KioskAutoActionState.NeedsReview; Review("発券結果が不明です。印刷キューと用紙を確認してから手動で再印刷してください。"); return; }
            string? action = job.Link is KioskAutoActionState.Pending or KioskAutoActionState.Running ? "link" :
                job.Arrival is KioskAutoActionState.Pending or KioskAutoActionState.Running ? "arrived" : null;
            if (action is not null)
            {
                var response = capture.Responses.LastOrDefault(r => r.RequestId == "ra-" + capture.Id + "-" + action);
                bool done = action == "link" && capture.DynamicsReceiptDirectory.Length > 0;
                if (response is not null)
                {
                    done = response.Success && (action == "link" ? response.Code is "linked" or "already_linked" or "invoked" : response.Code == "invoked");
                    if (!done) { Review("iCall " + response.Code + ": " + response.Message); return; }
                }
                if (done)
                {
                    if (action == "link") job.Link = KioskAutoActionState.Completed; else job.Arrival = KioskAutoActionState.Completed;
                    job.Message = action == "link" && capture.DynamicsReceiptDirectory.Length > 0
                        ? "直接連携の出力受付済み（Dynamics取込結果は未確認）" : "iCall " + action + " 完了";
                    Save(); return;
                }
                if (capture.Stage == CaptureStage.NeedsReview) { Review(capture.Status); return; }
                if (capture.PendingRequest is { } pending && pending.Action != action) return;
                if (capture.RetryAfter > Now) return;
                if (action == "link") job.Link = KioskAutoActionState.Running; else job.Arrival = KioskAutoActionState.Running;
                job.Message = action == "link" ? "自動連携処理中" : "自動来院確認処理中"; Save();
                var config = new AgentSettings { ReceptionLinkMode = job.LinkMode, DynamicsReceiptDirectory = job.ReceiptDirectory };
                var workflow = new ReceptionWorkflow(captures, lookup, config);
                if (capture.PendingRequest is null) await workflow.QueueKioskAsync(session, action, token);
                if (!available()) return;
                await workflow.StepByIdAsync(capture.Id, token);
                capture = captures.Get(capture.Id);
                if (capture.Stage == CaptureStage.NeedsReview) Review(capture.Status);
                // Observe the persisted response on the next pass, including responses handled by the monitor.
                return;
            }
            if (job.Print == KioskAutoActionState.Pending)
            {
                await captures.WorkflowGate.WaitAsync(token);
                try
                {
                    capture = captures.Get(capture.Id);
                    if (!available()) return;
                    if (!KioskAutomationEligibility.IsEligible(session, capture, Now))
                    { Review("発券前の患者・予約確認が必要です。"); return; }
                    var receipt = KioskReceipt.Create(session, capture);
                    token.ThrowIfCancellationRequested();
                    job.Print = KioskAutoActionState.Running; job.Message = "自動発券処理中"; Save();
                    print(receipt, job.PrinterName);
                    job.Print = KioskAutoActionState.Completed; job.Message = "発券を印刷キューへ送信済み"; Save();
                    captures.Log("kiosk_receipt_spooled", capture.Id, new { sessionId = session.Id, copies = 2 });
                }
                finally { captures.WorkflowGate.Release(); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // No unattended resend after an ambiguous side effect. Staff can inspect persisted results.
            Review("自動受付処理を確認してください: " + ex.Message);
        }
    }
}
