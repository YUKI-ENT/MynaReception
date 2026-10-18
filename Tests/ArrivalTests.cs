using System.Text.Json;
using iCallManager.Bridge;
using iCallManager.Core;
using iCallManager.ICall;

internal static class ArrivalTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var first = new Reservation("1", "1", "手入力の氏名", "90001", true, false);
        var other = first with { ReceptionNo = "2", InternalId = "90002" };
        var request = new BridgeRequest("arrive-by-reception", "arrived") { ReceptionNo = "1" };
        var adapter = new Adapter { Rows = [first, other] };
        using var service = new ReservationService(() => adapter);
        check((await service.ExecuteAsync(request)).Code == "operations_disabled" && adapter.Invocations == 0,
            "reception-only arrival respects read-only mode");
        service.OperationsEnabled = true;
        var result = await service.ExecuteAsync(request);
        check(result.Success && result.Code == "invoked" && result.Reservation == first && adapter.Invocations == 1,
            "reception alone selects correct booking despite duplicate patient IDs");
        check((await service.ExecuteAsync(request with { ExpectedPatientName = "完全に違う入力名" })).Success,
            "arrival ignores caller name instead of requiring exact spelling");
        check((await service.ExecuteAsync(request with { PatientId = "1" })).Success,
            "matching optional card is accepted");
        int before = adapter.Invocations;
        check((await service.ExecuteAsync(request with { PatientId = "9" })).Code == "identity_changed" && adapter.Invocations == before,
            "mismatching optional card never clicks");
        foreach (string? no in new string?[] { null, "", "-1", "１", "x", new string('1', 51) })
            check((await service.ExecuteAsync(request with { ReceptionNo = no })).Code == "invalid_request",
                "arrival requires a valid numeric reception number");
        check((await service.ExecuteAsync(request with { ExpectedReceptionNo = "2" })).Code == "invalid_request",
            "conflicting reception fields never choose either booking");
        check((await service.ExecuteAsync(request with { ReceptionNo = "99" })).Code == "not_found",
            "missing reception never falls back to patient identity");
        check((await service.ExecuteAsync(request with { ReceptionNo = null, ExpectedReceptionNo = "1" })).Success,
            "legacy expectedReceptionNo is supported without requiring card or name");
        adapter.Rows = [first, first];
        before = adapter.Invocations;
        check((await service.ExecuteAsync(request)).Code == "ambiguous_reception" && adapter.Invocations == before,
            "duplicate reception number never clicks an arbitrary row");
        adapter.Rows = [first with { PatientId = "", PatientName = "" }];
        check((await service.ExecuteAsync(request)).Success && adapter.Last?.PatientId == "",
            "arrival supports an initial-visit row without card or patient name");
        adapter.Rows = [first with { PatientId = "-", PatientName = "手入力" }];
        check((await service.ExecuteAsync(request)).Success, "placeholder card does not block reception-based arrival");
        adapter.Rows = [first with { CanMarkArrived = false }];
        check((await service.ExecuteAsync(request)).Code == "action_unavailable", "missing or disabled arrival button never clicks");
        adapter.Rows = [first with { InternalId = "" }];
        check((await service.ExecuteAsync(request)).Code == "identity_missing", "unverifiable internal booking ID blocks arrival");
        adapter.Rows = [first];
        adapter.BeforeInvoke = () => adapter.Rows = [first with { InternalId = "new-booking" }];
        before = adapter.Invocations;
        check((await service.ExecuteAsync(request)).Code == "identity_changed" && adapter.Invocations == before,
            "reused reception number with another internal ID is rejected immediately before dispatch");
        adapter.Rows = [first];
        adapter.BeforeInvoke = () => adapter.Rows = [first with { PatientName = "修正した氏名" }];
        check((await service.ExecuteAsync(request)).Success,
            "same internal booking tolerates name editing before arrival dispatch");
        adapter.Rows = [first];
        adapter.BeforeInvoke = () => adapter.Rows = [first with { PatientId = "different" }];
        check((await service.ExecuteAsync(request)).Code == "identity_changed", "changed assigned card at dispatch is rejected");
        adapter.BeforeInvoke = null;
        adapter.Rows = [first];
        adapter.FailAfterDispatch = true;
        before = adapter.Invocations;
        check((await service.ExecuteAsync(request)).Code == "outcome_unknown" && adapter.Invocations == before + 1,
            "uncertain arrival dispatch never retries the button");
        adapter.FailAfterDispatch = false;

        // Both supported IE table layouts must retain an operable no-card arrival.
        var flat = DiagnosticFixture.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ie-flat-anonymized.txt"));
        var cells = flat.Descendants().Single(n => n.AutomationId == "tableList").Children;
        cells[19] = new() { Role = 29, Name = "" };
        cells[20] = new() { Role = 29, Name = "" };
        foreach (int index in new[] { 27, 29, 31 })
            for (int i = 0; i < cells[index].Children.Count; i++)
            {
                var button = cells[index].Children[i];
                if (button.ControlType == 50000) cells[index].Children[i] = new()
                { ControlType = 50000, Name = button.Name, AutomationId = button.AutomationId, Enabled = button.Enabled };
            }
        var parsed = RowParser.Parse(flat, new())[1].Reservation;
        check(parsed.CanMarkArrived && parsed.CanLink && parsed.InternalId == "90002",
            "real flat IE layout permits no-card no-name arrival while preserving internal booking ID");
        UiNode Cell(string name) => new() { Role = 29, Name = name };
        var header = new UiNode { Role = 28, Children = { Cell("受付番号"), Cell("診察券番号"), Cell("患者名") } };
        var buttons = Cell("");
        buttons.Children.AddRange([new() { ControlType = 50000, Name = "来院確認", Enabled = true },
            new() { ControlType = 50000, AutomationId = "chk90001", Enabled = true }]);
        var row = new UiNode { Role = 28, Children = { Cell("1"), Cell(""), Cell(""), buttons } };
        var table = new UiNode { Role = 24, Children = { header, row } };
        var generic = RowParser.Parse(table, new())[0].Reservation;
        check(generic.CanMarkArrived && generic.CanLink && generic.InternalId == "90001",
            "generic row layout permits no-card arrival and link by reception");

        string root = Path.Combine(Path.GetTempPath(), "icall-arrival-" + Guid.NewGuid().ToString("N"));
        using var bridge = new FileBridge(Path.Combine(root, "bridge"), Path.Combine(root, "state"), service.ExecuteAsync, _ => { });
        string json = """{"requestId":"arrival-file-1","action":"arrived","receptionNo":"1"}""";
        string path = Path.Combine(root, "bridge", "request", "arrival-file-1.json");
        before = adapter.Invocations;
        File.WriteAllText(path, json);
        await bridge.PollAsync();
        var reply = JsonSerializer.Deserialize<BridgeResponse>(File.ReadAllText(Path.Combine(root, "bridge", "response", "arrival-file-1.json")), AppSettings.Json)!;
        var decoded = JsonSerializer.Deserialize<BridgeRequest>(json, AppSettings.Json)!;
        check(reply.Success && reply.Code == "invoked" && reply.ReceptionNo == "1" && reply.RequestFingerprint == decoded.Fingerprint(),
            "minimal reception-only JSON reaches file API and returns target reception");
        File.WriteAllText(path, json);
        await bridge.PollAsync();
        check(adapter.Invocations == before + 1, "same reception-only file replays response without another arrival click");
        check(decoded.Fingerprint() != (decoded with { ReceptionNo = "2" }).Fingerprint(),
            "reception number participates in request fingerprint");
    }

    private sealed class Adapter : IReservationAdapter
    {
        public IReadOnlyList<Reservation> Rows { get; set; } = [];
        public int Invocations { get; private set; }
        public Reservation? Last { get; private set; }
        public Action? BeforeInvoke { get; set; }
        public bool FailAfterDispatch { get; set; }
        public IReadOnlyList<Reservation> Read() => Rows;
        public void Invoke(Reservation expected, string action, Func<bool> mayInvoke)
        {
            BeforeInvoke?.Invoke();
            Last = ReceptionTarget.Verify(Rows, expected);
            if (!mayInvoke()) throw new BridgeException("operations_disabled", "disabled");
            Invocations++;
            if (FailAfterDispatch) throw new InvalidOperationException("uncertain dispatch");
        }
        public string Diagnose() => "synthetic";
        public void Dispose() { }
    }
}
