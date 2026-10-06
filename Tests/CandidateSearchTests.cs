using System.Text.Json;
using iCallManager.Bridge;
using iCallManager.Core;

internal static class CandidateSearchTests
{
    public static async Task Run(Action<bool, string> check)
    {
        bool Parse(string text, string expected) => ReservationCandidateSearch.TryParseNameAndBirthdate(text, out _, out var date)
            && date.ToString("yyyy-MM-dd") == expected;
        check(Parse("てすと はなこ （昭和５１年９月２５日）", "1976-09-25"), "candidate parses full width Japanese birthday");
        check(Parse("架空 花子 (平成元年1月8日)", "1989-01-08"), "candidate parses first era year");
        check(Parse("架空 花子 (2000年2月29日)", "2000-02-29"), "candidate parses Gregorian leap date");
        foreach (var text in new[] { "架空 (平成元年1月7日)", "架空 (昭和64年1月8日)", "架空 (令和元年4月30日)",
            "架空 (2001年2月29日)", "架空 (平成7年8月2", "架空 (51年9月25日)", "架空 (西暦元年1月1日)" })
            check(!ReservationCandidateSearch.TryParseNameAndBirthdate(text, out _, out _), "candidate rejects invalid or incomplete birthday: " + text);

        var adapter = new FakeAdapter { Rows = [
            new("10", "-", "架空花子 (昭和51年9月25日)", "1", true, false),
            new("11", "-", "てすと はなこ（昭和51年9月25日）", "2", true, false),
            new("12", "-", "べつ はなこ (昭和51年9月25日)", "3", true, false),
            new("13", "-", "てすと はなこ (昭和51年9月26日)", "4", true, false),
            new("14", "123", "てすと はなこ", "5", true, true),
            new("15", "-", "-", "6", false, true)
        ] };
        using var service = new ReservationService(() => adapter);
        await service.SyncAsync();
        var request = new BridgeRequest("candidate-1", "find_candidates")
        { PatientName = "架空 花子", NameKana = "テスト ハナコ", GivenName = "はなこ", Birthdate = "1976-09-25" };
        var withoutId = JsonSerializer.Deserialize<BridgeRequest>("""
            {"requestId":"no-id","action":"find_candidates","nameKana":"テスト ハナコ","birthdate":"1976-09-25"}
            """, AppSettings.Json)!;
        check((await service.ExecuteAsync(withoutId)).Candidates?.Count == 3, "JSON search accepts omitted patientId");
        var result = await service.ExecuteAsync(request);
        check(result.Success && result.Code == "candidates_found" && result.Reservation is null && result.Candidates?.Count == 3,
            "candidate search returns all exact birthday suggestions without selected identity");
        check(result.Candidates![0].NameMatch == "full_name" && result.Candidates[1].NameMatch == "full_name" &&
            result.Candidates[2].NameMatch == "given_name_suffix" && result.Candidates.All(c => c.PatientId is null && c.RequiresConfirmation),
            "candidate grades kanji, kana and explicit given-name suffix");
        var mismatch = await service.ExecuteAsync(request with { PatientName = "異なる氏名", NameKana = null, GivenName = null });
        check(mismatch.Candidates!.All(c => c.NameMatch == "birthdate_only"), "name mismatch retains birthday candidates for review");
        var empty = await service.ExecuteAsync(request with { Birthdate = "1976-09-27" });
        check(empty.Success && empty.Code == "no_candidates" && empty.Candidates?.Count == 0, "no suggestions is a successful read");
        int reads = adapter.Threads.Count;
        var invalid = await service.ExecuteAsync(request with { Birthdate = "1976/09/25" });
        var noName = await service.ExecuteAsync(request with { PatientName = null, NameKana = null, GivenName = null });
        check(invalid.Code == "invalid_request" && noName.Code == "invalid_request" && adapter.Threads.Count == reads,
            "invalid candidate request rejected before reading UI");
        adapter.ThrowOnRead = true;
        try { await service.SyncAsync(); } catch (InvalidOperationException) { }
        check((await service.ExecuteAsync(request)).Candidates?.Count == 3, "failed sync retains recent cached candidates");
        adapter.ThrowOnRead = false;
        check(adapter.Invocations == 0 && !service.OperationsEnabled, "candidate API works with operations disabled and never invokes buttons");

        var directory = Path.Combine(Path.GetTempPath(), "iCallCandidateTest-" + Guid.NewGuid().ToString("N"));
        using var bridge = new FileBridge(directory, Path.Combine(directory, "state"), service.ExecuteAsync, _ => { });
        var client = new FileICallBridgeClient(directory);
        var pending = client.SendAsync(request, TimeSpan.FromSeconds(5));
        for (int i = 0; i < 20 && !pending.IsCompleted; i++) { await Task.Delay(50); await bridge.PollAsync(); }
        var response = await pending;
        check(response.Candidates?.Count == 3 && response.PatientId is null && response.ReceptionNo is null && response.RequestFingerprint == request.Fingerprint(),
            "file API carries candidate array and verifies search fingerprint");
        check(request.Fingerprint() != (request with { Birthdate = "1976-09-26" }).Fingerprint(), "search keys participate in fingerprint");
        var old = new BridgeRequest("legacy", "find", "00011");
        var legacyJson = JsonSerializer.Serialize(new { old.RequestId, old.Action, old.PatientId, old.ExpectedReceptionNo, old.ExpectedPatientName }, AppSettings.Json);
        check(JsonSerializer.Serialize(old, AppSettings.Json) == legacyJson, "legacy request serialization and fingerprints remain compatible");
    }
}
