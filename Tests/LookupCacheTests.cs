using iCallManager.Core;

internal static class LookupCacheTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var clock = new CacheClock();
        var patient = new Reservation("36", "00011", "テスト患者", "57760", true, true);
        using var adapter = new CacheAdapter { Rows = [patient] };
        using var service = new ReservationService(() => adapter, timeProvider: clock);
        var find = new BridgeRequest("cache-find", "find", "00011");
        check((await service.ExecuteAsync(find)).Code == "automation_unavailable" && adapter.Reads == 0,
            "uninitialized lookup never reads browser or reports missing reservation");
        await service.SyncAsync();
        adapter.Rows = [patient with { ReceptionNo = "37" }];
        check((await service.ExecuteAsync(find)).Reservation == patient && adapter.Reads == 1,
            "lookup uses last successful snapshot until next sync");

        adapter.BlockRead = true;
        var sync = service.SyncAsync();
        try
        {
            check(adapter.ReadStarted.Wait(TimeSpan.FromSeconds(5)), "background sync is reading browser");
            var lookup = await Task.Run(() => service.ExecuteAsync(find)).WaitAsync(TimeSpan.FromSeconds(1));
            check(lookup.Reservation == patient, "lookup completes while UIA worker is blocked");
        }
        finally { adapter.ReleaseRead.Set(); }
        await sync;
        adapter.BlockRead = false;
        check((await service.ExecuteAsync(find)).Reservation?.ReceptionNo == "37", "successful sync atomically replaces lookup data");

        adapter.FailRead = true;
        try { await service.SyncAsync(); } catch (InvalidOperationException) { }
        check(!service.Snapshot.IsCurrent && (await service.ExecuteAsync(find)).Success,
            "failed sync preserves recent valid data for lookup");
        clock.Now += TimeSpan.FromMinutes(2);
        check((await service.ExecuteAsync(find)).Code == "automation_unavailable", "expired cache never reports missing reservation");
        var candidates = new BridgeRequest("cache-candidates", "find_candidates")
        { PatientName = "架空", Birthdate = "1976-09-25" };
        check((await service.ExecuteAsync(candidates)).Code == "automation_unavailable", "candidate lookup also rejects expired cache");

        adapter.FailRead = false;
        adapter.Rows = [];
        await service.SyncAsync();
        check((await service.ExecuteAsync(find)).Code == "not_found", "verified empty sync replaces previous reservations");
        check((await service.ExecuteAsync(candidates)).Code == "no_candidates", "verified empty cache returns no candidates");

        // Morning and afternoon reception numbers belong to different lists.
        var morning = patient with { ReceptionNo = "1" };
        var remaining = patient with { PatientId = "00022", ReceptionNo = "2", InternalId = "57761" };
        var morningCandidate = patient with { PatientId = "-", ReceptionNo = "3",
            PatientName = "架空 (昭和51年9月25日)", InternalId = "57762" };
        adapter.Rows = [morning, remaining, morningCandidate];
        await service.SyncAsync();
        check((await service.ExecuteAsync(candidates)).Candidates?.Count == 1, "morning candidate is cached");
        adapter.Rows = [remaining];
        await service.SyncAsync();
        check((await service.ExecuteAsync(find)).Code == "not_found" &&
            (await service.ExecuteAsync(find with { PatientId = "00022" })).Reservation == remaining &&
            (await service.ExecuteAsync(candidates)).Code == "no_candidates",
            "guided patients disappear from both lookup paths while remaining reservations stay");
        adapter.Rows = [];
        await service.SyncAsync();
        check(service.Snapshot.Rows.Count == 0 &&
            (await service.ExecuteAsync(find with { PatientId = "00022" })).Code == "not_found",
            "midday clear removes all morning reservations");
        var afternoon = remaining with { ReceptionNo = "1", InternalId = "60001" };
        adapter.Rows = [afternoon];
        await service.SyncAsync();
        check((await service.ExecuteAsync(find)).Code == "not_found" &&
            (await service.ExecuteAsync(find with { PatientId = "00022" })).Reservation == afternoon &&
            service.Snapshot.Rows.Count == 1,
            "afternoon number one cannot retain or merge morning number one");
        adapter.Rows = [morning];
        await service.SyncAsync();
        adapter.Rows = [afternoon];
        await service.SyncAsync();
        check((await service.ExecuteAsync(find)).Code == "not_found" &&
            (await service.ExecuteAsync(find with { PatientId = "00022" })).Reservation == afternoon,
            "full replacement also handles session switch without observing empty list");

        clock.Now = new DateTimeOffset(2026, 10, 5, 23, 59, 59, TimeSpan.FromHours(9));
        adapter.Rows = [patient];
        await service.SyncAsync();
        clock.Now += TimeSpan.FromSeconds(2);
        check((await service.ExecuteAsync(find)).Code == "automation_unavailable", "previous-day cache is rejected even within lifetime");
        await service.SyncAsync();
        service.OperationsEnabled = true;
        adapter.Rows = [patient with { ReceptionNo = "99" }];
        check((await service.ExecuteAsync(new("cache-arrived", "arrived", "00011", "36", "テスト患者"))).Code == "identity_changed" && adapter.Invocations == 0,
            "real operation revalidates changed browser identity instead of cache");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await service.ExecuteAsync(find, cancelled.Token); check(false, "cancelled cache request"); }
        catch (OperationCanceledException) { check(true, "cancelled cache request"); }
        service.Dispose();
        try { await service.ExecuteAsync(find); check(false, "disposed cache request"); }
        catch (ObjectDisposedException) { check(true, "disposed cache request"); }
    }

    private sealed class CacheClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(9));
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("CacheTest", TimeSpan.FromHours(9), "CacheTest", "CacheTest");
    }

    private sealed class CacheAdapter : IReservationAdapter
    {
        public IReadOnlyList<Reservation> Rows { get; set; } = [];
        public int Reads { get; private set; }
        public int Invocations { get; private set; }
        public bool FailRead { get; set; }
        public bool BlockRead { get; set; }
        public ManualResetEventSlim ReadStarted { get; } = new();
        public ManualResetEventSlim ReleaseRead { get; } = new();
        public IReadOnlyList<Reservation> Read()
        {
            Reads++;
            if (BlockRead) { ReadStarted.Set(); ReleaseRead.Wait(); }
            if (FailRead) throw new InvalidOperationException();
            return Rows;
        }
        public void Invoke(Reservation expected, string action, Func<bool> mayInvoke) => Invocations++;
        public string Diagnose() => "cache-test";
        public void Dispose() { }
    }
}
