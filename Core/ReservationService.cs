using System.Collections.Concurrent;

namespace iCallManager.Core;

/// <summary>All UIA objects live on one dedicated MTA thread, never on the WinForms thread.</summary>
public sealed class ReservationService : IDisposable
{
    private readonly BlockingCollection<Action> queue = new(32);
    private readonly Thread worker;
    private readonly Func<IReservationAdapter> factory;
    private readonly ITicketPrinter printer;
    private IReservationAdapter? adapter;
    private volatile bool operationsEnabled;
    private SyncSnapshot snapshot = new(null, false, [], "未同期");
    public SyncSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool OperationsEnabled { get => operationsEnabled; set => operationsEnabled = value; }

    public ReservationService(Func<IReservationAdapter> factory, ITicketPrinter? printer = null)
    {
        this.factory = factory;
        this.printer = printer ?? new UnconfiguredTicketPrinter();
        worker = new Thread(Run) { IsBackground = true, Name = "iCall UI Automation" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private void Run()
    {
        try { foreach (var work in queue.GetConsumingEnumerable()) work(); }
        finally { adapter?.Dispose(); }
    }

    private Task<T> Enqueue<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!queue.TryAdd(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                { completion.TrySetCanceled(cancellationToken); return; }
                try { completion.TrySetResult(action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            })) completion.TrySetException(new BridgeException("busy", "処理待ちが多いため実行できません。"));
        }
        catch (InvalidOperationException) { completion.TrySetException(new ObjectDisposedException(nameof(ReservationService))); }
        return completion.Task;
    }

    private IReadOnlyList<Reservation> Read()
    {
        try
        {
            adapter ??= factory();
            var rows = adapter.Read().ToArray();
            Volatile.Write(ref snapshot, new(DateTimeOffset.Now, true, rows, $"同期成功: {rows.Length}件"));
            return rows;
        }
        catch
        {
            Volatile.Write(ref snapshot, Snapshot with { IsCurrent = false, Status = "同期失敗（表示は前回の結果）" });
            throw;
        }
    }

    public Task<SyncSnapshot> SyncAsync(CancellationToken ct = default) => Enqueue(() => { Read(); return Snapshot; }, ct);
    public Task<string> DiagnoseAsync(CancellationToken ct = default) =>
        Enqueue(() => { adapter ??= factory(); return adapter.Diagnose(); }, ct);

    public Task<OperationResult> ExecuteAsync(BridgeRequest request, CancellationToken ct = default)
    {
        var queued = System.Diagnostics.Stopwatch.StartNew();
        OperationalLog.Write("request_queued", new { request.RequestId, request.Action });
        return Enqueue(() =>
        {
            double queueMs = queued.Elapsed.TotalMilliseconds;
            OperationalLog.Write("request_started", new { request.RequestId, request.Action, queueMs });
            OperationalLog.RequestId = request.RequestId;
            try
            {
                var result = Execute(request);
                OperationalLog.Write("request_completed", new { request.RequestId, request.Action, result.Code, queueMs, executionMs = queued.Elapsed.TotalMilliseconds - queueMs });
                return result;
            }
            finally { OperationalLog.RequestId = null; }
        }, ct);
    }

    private OperationResult Execute(BridgeRequest request)
    {
        try
        {
            if (request.Action == "find_candidates")
            {
                ReservationCandidateSearch.Validate(request);
                return ReservationCandidateSearch.Search(request, Read());
            }
            if (string.IsNullOrWhiteSpace(request.PatientId) || request.PatientId.Length > 100)
                return new(false, "invalid_request", "patientIdを指定してください。");
            if (request.Action is not ("find" or "arrived" or "link" or "print" or "assign"))
                return new(false, "unsupported_action", "操作はfind / find_candidates / arrived / link / print / assignです。");
            if (request.Action != "find" && !operationsEnabled)
                return new(false, "operations_disabled", "アプリで実操作を有効にしてください。");
            if (request.Action == "assign") return Assign(request);
            var matches = Read().Where(r => r.HasPatientIdentity && r.PatientId == request.PatientId).ToArray();
            if (matches.Length == 0) return new(false, "not_found", "現在の一覧に該当患者がいません。");
            if (matches.Length != 1) return new(false, "ambiguous_patient", "同じ患者IDが複数行あります。職員が確認してください。");
            var row = matches[0];
            if (request.Action == "find") return new(true, "found", "予約を取得しました。", row);
            if (request.ExpectedReceptionNo != row.ReceptionNo || request.ExpectedPatientName != row.PatientName)
                return new(false, "identity_changed", "find結果の受付番号・患者名と一致しません。再確認してください。");
            if (!operationsEnabled) return new(false, "operations_disabled", "実操作が無効になりました。");
            if (request.Action == "print") return printer.Print(row);
            if (request.Action == "link") return EnsureLinked(row);
            if (request.Action == "arrived" && !row.CanMarkArrived || request.Action == "link" && !row.CanLink)
                return new(false, "action_unavailable", "対象ボタンがありません、または操作できません。実行済みとは断定しません。", row);
            try { adapter!.Invoke(row, request.Action, () => operationsEnabled); }
            catch (BridgeException) { throw; }
            catch
            {
                Volatile.Write(ref snapshot, Snapshot with { IsCurrent = false, Status = "操作結果不明・職員確認が必要" });
                return new(false, "outcome_unknown", "操作中にエラーが発生しました。再送せずiCall画面を確認してください。", row);
            }
            // Invoke succeeding only confirms dispatch, not completion in iCall/Dynamics.
            Volatile.Write(ref snapshot, Snapshot with { IsCurrent = false, Status = "操作送信済み・次回同期待ち" });
            return new(true, "invoked", "ボタン操作を送信しました。処理完了はiCall／Dynamics側で確認してください。", row);
        }
        catch (BridgeException ex) { return new(false, ex.Code, ex.Message); }
        catch { return new(false, "automation_unavailable", "iCallの読取に失敗しました。ログイン状態・表示画面を確認してください。"); }
    }

    private OperationResult EnsureLinked(Reservation expected)
    {
        if (expected.LinkButtonName == "〆")
            return new(true, "already_linked", "既に連携ONです。ボタンは押していません。", expected);
        if (expected.LinkButtonName != "")
            return new(false, "link_state_unverified", "連携ボタンの状態を確認できません。", expected);
        if (!expected.CanLink)
            return new(false, "action_unavailable", "連携ボタンを操作できません。", expected);
        bool dispatched = false;
        try
        {
            adapter!.Invoke(expected, "link", () => operationsEnabled);
            dispatched = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                var matches = Read().Where(r => r.PatientId == expected.PatientId).ToArray();
                if (matches.Length != 1 || matches[0].ReceptionNo != expected.ReceptionNo ||
                    matches[0].PatientName != expected.PatientName || matches[0].InternalId != expected.InternalId)
                    break;
                if (matches[0].LinkButtonName == "〆")
                    return new(true, "linked", "連携ボタンのON（〆）を確認しました。", matches[0]);
                Thread.Sleep(200);
            } while (clock.Elapsed < TimeSpan.FromSeconds(8));
        }
        catch (BridgeException ex) when (!dispatched && ex.Code != "outcome_unknown")
        {
            Volatile.Write(ref snapshot, Snapshot with { IsCurrent = false, Status = "連携未完了・画面確認が必要" });
            return new(false, ex.Code, ex.Message, expected);
        }
        catch { }
        Volatile.Write(ref snapshot, Snapshot with { IsCurrent = false, Status = "連携結果不明・画面確認が必要" });
        return new(false, "outcome_unknown", "連携ONを確認できません。自動で再押下せず、iCall画面を確認してください。", expected);
    }

    private OperationResult Assign(BridgeRequest request)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.PatientId, @"\A[0-9]{1,50}\z") ||
            request.ExpectedReceptionNo is not null && !System.Text.RegularExpressions.Regex.IsMatch(request.ExpectedReceptionNo, @"\A[0-9]+\z"))
            return new(false, "invalid_request", "カルテ番号・指定受付番号は半角数字で指定してください。");
        var rows = Read();
        var existing = rows.Where(r => r.PatientId == request.PatientId).ToArray();
        if (existing.Length == 1 && existing[0].HasPatientIdentity)
            return new(false, "already_reserved", "この患者は既に予約があります。findで取得してください。", existing[0]);
        var expected = DummyAssignment.Select(rows, request);
        try
        {
            var assigned = adapter!.AssignDummy(expected, request.PatientId, () => operationsEnabled);
            // Read again through the ordinary list path, independently of the popup result.
            var verifiedRows = Read();
            var verified = verifiedRows.Where(r => r.PatientId == request.PatientId).ToArray();
            if (verified.Length != 1 || !verified[0].HasPatientIdentity || verified[0].ReceptionNo != expected.ReceptionNo ||
                assigned.PatientId != request.PatientId || assigned.ReceptionNo != expected.ReceptionNo ||
                expected.InternalId != "" && verified[0].InternalId != expected.InternalId)
                throw new BridgeException("outcome_unknown", "割当後の一覧を確認できません。再送せず画面を確認してください。");
            return new(true, "assigned", "ダミー枠への患者割当を確認しました。", verified[0]);
        }
        catch (BridgeException ex)
        {
            Volatile.Write(ref snapshot, Snapshot with { IsCurrent = false, Status = "割当未完了・画面確認が必要" });
            return new(false, ex.Code, ex.Message);
        }
        catch
        {
            Volatile.Write(ref snapshot, Snapshot with { IsCurrent = false, Status = "割当結果不明・画面確認が必要" });
            return new(false, "outcome_unknown", "患者割当の結果が不明です。再送せず画面を確認してください。");
        }
    }

    public void Dispose()
    {
        operationsEnabled = false;
        queue.CompleteAdding();
        // UIA providers may hang; the background thread must not block application shutdown.
    }
}
