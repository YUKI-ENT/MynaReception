using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReceptionAgent.ICall;

internal static class ICallRequestContractTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ra-icall-contract-" + Guid.NewGuid().ToString("N"));
        string requestDirectory = Path.Combine(root, "request"), responseDirectory = Path.Combine(root, "response");
        Directory.CreateDirectory(requestDirectory); Directory.CreateDirectory(responseDirectory);
        foreach (var action in new[] { "arrived", "link" })
        foreach (var patientId in new[] { "", "00123" })
        {
            var request = new ICallRequest("ra-" + Guid.NewGuid().ToString("N") + "-" + action, action, patientId) { ReceptionNo = "7" };
            var send = new ICallFileClient().SendAsync(request, requestDirectory, responseDirectory, TimeSpan.FromSeconds(5), default);
            string json = await File.ReadAllTextAsync(Path.Combine(requestDirectory, request.RequestId + ".json"));
            var manager = JsonSerializer.Deserialize<iCallManager.Core.BridgeRequest>(json, iCallManager.Core.AppSettings.Json)!;
            check(manager.Action == action && manager.ReceptionNo == "7" && manager.PatientId == patientId &&
                manager.ExpectedReceptionNo is null && manager.ExpectedPatientName is null,
                "ReceptionAgent wire request uses reception number without name constraint: " + action);
            check(manager.Fingerprint() == request.Fingerprint(), "manager and agent agree on reception request fingerprint: " + action);
            var response = new iCallManager.Core.BridgeResponse(request.RequestId, true, action == "link" ? "linked" : "invoked",
                "synthetic", patientId, "7", "iCallで編集された氏名", DateTimeOffset.Now, manager.Fingerprint());
            await File.WriteAllTextAsync(Path.Combine(responseDirectory, request.RequestId + ".json"), JsonSerializer.Serialize(response, iCallManager.Core.AppSettings.Json));
            check((await send).Success, "agent accepts manager reception response with edited display name: " + action);
            var restored = JsonSerializer.Deserialize<ICallRequest>(JsonSerializer.Serialize(request, ICallFileClient.Json), ICallFileClient.Json)!;
            check(restored.ReceptionNo == "7" && restored.Fingerprint() == request.Fingerprint(),
                "saved reception request survives restart without identity or fingerprint changes");
        }

        var legacy = new { RequestId = "ra-" + Guid.NewGuid().ToString("N") + "-link", Action = "link", PatientId = "00123",
            ExpectedReceptionNo = "7", ExpectedPatientName = "保存済み患者名" };
        string legacyJson = JsonSerializer.Serialize(legacy, ICallFileClient.Json);
        var restoredLegacy = JsonSerializer.Deserialize<ICallRequest>(legacyJson, ICallFileClient.Json)!;
        check(restoredLegacy.ReceptionNo is null && restoredLegacy.Fingerprint() == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyJson))),
            "existing pending request preserves original fingerprint and request body on upgrade");
        var legacySend = new ICallFileClient().SendAsync(restoredLegacy, requestDirectory, responseDirectory, TimeSpan.FromSeconds(5), default);
        var legacyResponse = new iCallManager.Core.BridgeResponse(legacy.RequestId, true, "already_linked", "synthetic", legacy.PatientId,
            "7", "編集済み氏名", DateTimeOffset.Now, restoredLegacy.Fingerprint());
        await File.WriteAllTextAsync(Path.Combine(responseDirectory, legacy.RequestId + ".json"), JsonSerializer.Serialize(legacyResponse, ICallFileClient.Json));
        check((await legacySend).Code == "already_linked", "legacy pending operation response is accepted without rewriting the request");

        foreach (var action in new[] { "arrived", "link" })
        {
            var request = new ICallRequest("ra-" + Guid.NewGuid().ToString("N") + "-" + action, action) { ReceptionNo = "7" };
            var send = new ICallFileClient().SendAsync(request, requestDirectory, responseDirectory, TimeSpan.FromSeconds(5), default);
            var response = new iCallManager.Core.BridgeResponse(request.RequestId, true, action == "link" ? "linked" : "invoked", "synthetic", "",
                "8", "別の受付", DateTimeOffset.Now, request.Fingerprint());
            await File.WriteAllTextAsync(Path.Combine(responseDirectory, request.RequestId + ".json"), JsonSerializer.Serialize(response, ICallFileClient.Json));
            try { await send; check(false, "wrong reception response rejected"); }
            catch (InvalidDataException) { check(true, "matching fingerprint cannot hide wrong operation response reception: " + action); }
        }

        var failed = new ICallRequest("ra-" + Guid.NewGuid().ToString("N") + "-link", "link") { ReceptionNo = "7" };
        var failedSend = new ICallFileClient().SendAsync(failed, requestDirectory, responseDirectory, TimeSpan.FromSeconds(5), default);
        var failure = new iCallManager.Core.BridgeResponse(failed.RequestId, false, "outcome_unknown", "synthetic", "", null, null,
            DateTimeOffset.Now, failed.Fingerprint());
        await File.WriteAllTextAsync(Path.Combine(responseDirectory, failed.RequestId + ".json"), JsonSerializer.Serialize(failure, ICallFileClient.Json));
        check((await failedSend).Code == "outcome_unknown", "unknown outcome without reception is preserved for review");
    }
}
