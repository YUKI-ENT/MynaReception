using System.Text.Json;
using iCallManager.Bridge;
using iCallManager.Core;

internal static class LinkTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var first = new Reservation("1", "1", "手入力の氏名", "90001", true, true) { LinkButtonName = "" };
        var second = first with { ReceptionNo = "2", InternalId = "90002" };
        var request = new BridgeRequest("link-by-reception", "link") { ReceptionNo = "1" };
        var adapter = new Adapter { Rows = [first, second] };
        using var service = new ReservationService(() => adapter);
        check((await service.ExecuteAsync(request)).Code == "operations_disabled" && adapter.Invocations == 0,
            "reception-only link honors operations disabled");
        service.OperationsEnabled = true;
        var result = await service.ExecuteAsync(request);
        check(result.Success && result.Code == "linked" && result.Reservation?.ReceptionNo == "1" && adapter.Invocations == 1 &&
            adapter.Rows[1].LinkButtonName == "", "link selects and confirms only requested reception despite duplicate card IDs");
        check((await service.ExecuteAsync(request)).Code == "already_linked" && adapter.Invocations == 1,
            "reception-only already ON never toggles OFF");
        foreach (var identity in new[] { ("", ""), ("-", "手入力"), ("", "初診") })
        {
            adapter.Rows = [first with { PatientId = identity.Item1, PatientName = identity.Item2 }];
            check((await service.ExecuteAsync(request with { ExpectedPatientName = "別表記" })).Code == "linked",
                "link permits initial visit without a card or matching caller name");
        }
        adapter.Rows = [first, second];
        check((await service.ExecuteAsync(request with { ReceptionNo = null, ExpectedReceptionNo = "1", PatientId = "1" })).Success,
            "legacy expectedReceptionNo and matching optional card still work for link");
        int before = adapter.Invocations;
        check((await service.ExecuteAsync(request with { PatientId = "other" })).Code == "identity_changed" && adapter.Invocations == before,
            "optional mismatching link card never dispatches");
        check((await service.ExecuteAsync(request with { ExpectedReceptionNo = "2" })).Code == "invalid_request",
            "conflicting link reception fields are rejected");
        foreach (string? no in new string?[] { null, "", "１", "-1", "x" })
            check((await service.ExecuteAsync(request with { ReceptionNo = no })).Code == "invalid_request",
                "link requires a numeric reception number");
        check((await service.ExecuteAsync(request with { ReceptionNo = "9" })).Code == "not_found", "missing link reception never falls back to card");
        adapter.Rows = [first, first];
        check((await service.ExecuteAsync(request)).Code == "ambiguous_reception" && adapter.Invocations == before,
            "duplicate reception blocks link before dispatch");
        adapter.Rows = [first with { InternalId = "" }];
        check((await service.ExecuteAsync(request)).Code == "identity_missing", "link requires internal booking identity");
        adapter.Rows = [first with { CanLink = false }];
        check((await service.ExecuteAsync(request)).Code == "action_unavailable", "disabled link is not dispatched");
        adapter.Rows = [first with { LinkButtonName = "unexpected" }];
        check((await service.ExecuteAsync(request)).Code == "link_state_unverified", "unknown link state is not clicked");
        adapter.Rows = [first with { PatientId = "-", PatientName = "-", HasAssignmentButton = true }];
        check((await service.ExecuteAsync(request)).Code == "action_unavailable", "unassigned dummy is never linked");
        adapter.Rows = [first with { PatientId = "", PatientName = "", HasAssignmentButton = true, LinkButtonName = "〆" }];
        check((await service.ExecuteAsync(request)).Code == "action_unavailable",
            "empty-ID dummy is not reported as a linked patient even when its link button is ON");
        adapter.Rows = [first];
        adapter.BeforeInvoke = () => adapter.Rows = [first with { InternalId = "reused" }];
        check((await service.ExecuteAsync(request)).Code == "identity_changed" && adapter.Invocations == before,
            "reused reception with changed internal ID cannot link");
        adapter.BeforeInvoke = null;
        adapter.Rows = [first];
        adapter.AfterInvoke = () => adapter.Rows = [adapter.Rows[0] with { PatientName = "氏名修正済み" }, second];
        check((await service.ExecuteAsync(request)).Code == "linked",
            "link confirmation uses reception and internal ID despite edited name and duplicate patient");
        adapter.AfterInvoke = () => adapter.Rows = [adapter.Rows[0] with { InternalId = "changed-after-dispatch" }];
        adapter.Rows = [first];
        before = adapter.Invocations;
        check((await service.ExecuteAsync(request)).Code == "outcome_unknown" && adapter.Invocations == before + 1,
            "changed booking after dispatch is unknown with no repeated click");
        adapter.AfterInvoke = null;
        adapter.FailAfterDispatch = true;
        adapter.Rows = [first];
        before = adapter.Invocations;
        var uncertain = await service.ExecuteAsync(request);
        check(uncertain.Code == "outcome_unknown" && uncertain.Message.Contains("phase=button_dispatch") &&
            uncertain.Message.Contains("InvalidOperationException") && adapter.Invocations == before + 1,
            "uncertain link dispatch never toggles again");
        adapter.FailAfterDispatch = false;
        adapter.Rows = [first];

        string root = Path.Combine(Path.GetTempPath(), "icall-link-" + Guid.NewGuid().ToString("N"));
        using var bridge = new FileBridge(Path.Combine(root, "bridge"), Path.Combine(root, "state"), service.ExecuteAsync, _ => { });
        const string json = """{"requestId":"link-file-1","action":"link","receptionNo":"1"}""";
        string path = Path.Combine(root, "bridge", "request", "link-file-1.json");
        before = adapter.Invocations;
        File.WriteAllText(path, json);
        await bridge.PollAsync();
        var reply = JsonSerializer.Deserialize<BridgeResponse>(File.ReadAllText(Path.Combine(root, "bridge", "response", "link-file-1.json")), AppSettings.Json)!;
        check(reply.Success && reply.Code == "linked" && reply.ReceptionNo == "1", "minimal reception-only link JSON works through file API");
        File.WriteAllText(path, json);
        await bridge.PollAsync();
        check(adapter.Invocations == before + 1, "same link file replays response without toggling again");
    }

    private sealed class Adapter : IReservationAdapter
    {
        public IReadOnlyList<Reservation> Rows { get; set; } = [];
        public int Invocations { get; private set; }
        public Action? BeforeInvoke { get; set; }
        public Action? AfterInvoke { get; set; }
        public bool FailAfterDispatch { get; set; }
        public IReadOnlyList<Reservation> Read() => Rows;
        public void Invoke(Reservation expected, string action, Func<bool> mayInvoke)
        {
            BeforeInvoke?.Invoke();
            ReceptionTarget.Verify(Rows, expected);
            if (!mayInvoke()) throw new BridgeException("operations_disabled", "disabled");
            Invocations++;
            Rows = Rows.Select(r => r.ReceptionNo == expected.ReceptionNo ? r with { LinkButtonName = "〆" } : r).ToArray();
            AfterInvoke?.Invoke();
            if (FailAfterDispatch) throw new InvalidOperationException("uncertain dispatch");
        }
        public string Diagnose() => "synthetic";
        public void Dispose() { }
    }
}
