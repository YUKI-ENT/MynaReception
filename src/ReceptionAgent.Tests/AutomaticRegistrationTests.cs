using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ReceptionAgent;
using ReceptionAgent.Face;
using ReceptionAgent.ICall;
using ReceptionAgent.Oqs.ReferenceNumber;
using ReceptionAgent.Reception;

internal static class AutomaticRegistrationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-auto-reference-" + Guid.NewGuid().ToString("N"));
        var settings = new AgentSettings { OqsRoot = Path.Combine(root, "oqs"), FaceXmlDirectory = Path.Combine(root, "face"),
            FaceTrashDirectory = Path.Combine(root, "trash"), ICallRequestDirectory = Path.Combine(root, "icall", "req"),
            ICallResponseDirectory = Path.Combine(root, "icall", "res") };
        foreach (string folder in new[] { Path.Combine(settings.OqsRoot, "req"), Path.Combine(settings.OqsRoot, "res"),
            settings.FaceXmlDirectory, settings.FaceTrashDirectory, settings.ICallRequestDirectory, settings.ICallResponseDirectory })
            Directory.CreateDirectory(folder);
        check(!settings.AutoRegisterReferenceNumber, "automatic reference registration defaults OFF");
        settings.AutoRegisterReferenceNumber = true; settings.Save(root);
        check(AgentSettings.Load(root).AutoRegisterReferenceNumber, "automatic registration setting persists ON");
        try { new AgentSettings { AutoRegisterReferenceNumber = true }.Save(Path.Combine(root, "invalid")); check(false, "missing automatic OQS folder"); }
        catch (ArgumentException) { check(true, "automatic registration setting requires OQS folder"); }
        string xml = "<XmlMsg><MessageHeader><MedicalInstitutionCode>0110012345</MedicalInstitutionCode><SegmentOfResult>1</SegmentOfResult></MessageHeader><MessageBody><ProcessingResultStatus>1</ProcessingResultStatus><ResultList><ResultOfQualificationConfirmation><Name>試験患者</Name><NameKana>テスト</NameKana><Birthdate>20000101</Birthdate><InsurerNumber>12345678</InsurerNumber><InsuredIdentificationNumber>001</InsuredIdentificationNumber><InsuredBranchNumber>00</InsuredBranchNumber></ResultOfQualificationConfirmation></ResultList></MessageBody></XmlMsg>";
        var face = FaceXmlParser.Parse(Encoding.UTF8.GetBytes(xml), FaceXmlEncoding.Utf8);
        var patient = new FacePatientMatch("11", "試験患者", "テスト", face.Birthdate, ["110"]);
        var candidate = new CaptureRecord { AutoRegisterReferenceNumber = true, PatientIdentifiedByDynamics = true,
            GeneratedAt = DateTime.Now, Face = face, Lookup = new([patient], patient, false, "一致") };
        check(AutomaticReferenceRegistration.IsEligible(candidate), "identified Dynamics patient eligible without reservation number");
        candidate.AutoRegisterReferenceNumber = false;
        check(!AutomaticReferenceRegistration.IsEligible(candidate), "OFF record never automatically submits");
        candidate.AutoRegisterReferenceNumber = true; candidate.PatientIdentifiedByDynamics = false;
        check(!AutomaticReferenceRegistration.IsEligible(candidate), "reference-derived patient never automatically registers");
        candidate.PatientIdentifiedByDynamics = true; candidate.Face = face with { ReferenceNumber = "11" };
        check(!AutomaticReferenceRegistration.IsEligible(candidate), "existing XML ReferenceNumber prevents automatic registration");
        candidate.Face = face; candidate.Lookup = new([patient, patient with { PatientId = "22" }], patient, false, "旧候補");
        check(!AutomaticReferenceRegistration.IsEligible(candidate), "multiple candidates never automatically register");
        candidate.Lookup = new([patient], patient, false, "一致"); candidate.GeneratedAt = DateTime.Today.AddDays(-1);
        check(!AutomaticReferenceRegistration.IsEligible(candidate), "previous-day patient never automatically registers");

        var store = new CaptureStore(Path.Combine(root, "db"));
        var registration = new SingleReferenceRegistrationService(Path.Combine(root, "jobs"), TimeSpan.FromSeconds(3));
        int lookups = 0;
        Task<FaceLookupResult> Lookup(FaceIdentity f, CancellationToken token)
        { lookups++; return Task.FromResult(new FaceLookupResult([patient], patient, false, "Dynamics一致")); }
        var monitor = new FaceCaptureMonitor(store, settings, Lookup, registration);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var run = monitor.RunAsync(stop.Token);
        string filename = $"OQSsiquc01res_face_auto_{DateTime.Now:yyyyMMddHHmmss}.xml";
        await File.WriteAllTextAsync(Path.Combine(settings.FaceXmlDirectory, filename), xml);
        CaptureRecord? received = null;
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                foreach (string path in Directory.GetFiles(settings.ICallRequestDirectory, "*.json"))
                {
                    var request = JsonSerializer.Deserialize<ICallRequest>(await File.ReadAllTextAsync(path), ICallFileClient.Json)!;
                    string responsePath = Path.Combine(settings.ICallResponseDirectory, request.RequestId + ".json");
                    if (!File.Exists(responsePath))
                        await File.WriteAllTextAsync(responsePath, JsonSerializer.Serialize(new ICallResponse(request.RequestId, false, "not_found",
                            "模擬予約なし", request.PatientId, null, null, DateTimeOffset.Now, request.Fingerprint()), ICallFileClient.Json));
                }
                foreach (string path in Directory.GetFiles(Path.Combine(settings.OqsRoot, "req"), "*.xml"))
                {
                    using var input = File.OpenRead(path); var request = ReferenceNumberResultParser.Load(input);
                    string responsePath = Path.Combine(settings.OqsRoot, "res", Path.GetFileName(path).Replace("req_", "res_"));
                    if (!File.Exists(responsePath))
                    {
                        var reply = new XDocument(new XElement("XmlMsg",
                            new XElement("MessageHeader", new XElement("MedicalInstitutionCode", "0110012345"),
                                new XElement("ArbitraryFileIdentifier", request.Descendants("ArbitraryFileIdentifier").Single().Value),
                                new XElement("SegmentOfResult", "1")),
                            new XElement("MessageBody", new XElement(request.Descendants("ReferenceNumberRegistrationInfo").Single()),
                                new XElement("ProcessingResultStatus", "1"))));
                        await File.WriteAllTextAsync(responsePath, reply.ToString(), Encoding.UTF8);
                    }
                }
                received = store.List().SingleOrDefault();
                if (received is not null && received.Stage == CaptureStage.Completed &&
                    registration.Load(received.Id)?.State == ReferenceRegistrationState.Completed) break;
                await Task.Delay(100, stop.Token);
            }
            check(lookups == 1 && received!.PatientIdentifiedByDynamics && received.AutoRegisterReferenceNumber &&
                received.RegistrationOqsRoot == settings.OqsRoot && received.AutomaticRegistrationAttempted,
                "monitor snapshots auto setting and triggers registration after Dynamics lookup");
            check(received!.ReceptionNo == "" && received.ReservationFoundAtReception == false &&
                registration.Load(received.Id)!.State == ReferenceRegistrationState.Completed,
                "reservation not_found does not prevent successful automatic OQS registration");
            check(received.ArrivalResult == "未要求" && received.LinkResult == "未要求" &&
                Directory.GetFiles(Path.Combine(settings.OqsRoot, "req"), "*.xml").Length == 1,
                "automatic reference registration leaves arrival/link manual and submits once");
        }
        finally { stop.Cancel(); try { await run; } catch (OperationCanceledException) { } }
        check(!await new AutomaticReferenceRegistration(store, registration).ProcessNextAsync(default),
            "restart does not repeat attempted/completed automatic registration");

        var invalid = new CaptureRecord { FileName = "invalid-auto.xml", ContentHash = "synthetic-invalid", GeneratedAt = DateTime.Now,
            Encoding = FaceXmlEncoding.Utf8, AutoRegisterReferenceNumber = true, RegistrationOqsRoot = settings.OqsRoot,
            PatientIdentifiedByDynamics = true, Face = face, Lookup = new([patient], patient, false, "一致"), Stage = CaptureStage.PatientIdentified };
        store.Capture(invalid, Encoding.UTF8.GetBytes(xml.Replace("<InsuredIdentificationNumber>001</InsuredIdentificationNumber>", "")));
        var worker = new AutomaticReferenceRegistration(store, registration);
        check(await worker.ProcessNextAsync(default), "automatic worker handles invalid face insurance");
        var failed = store.Get(invalid.Id);
        check(failed.AutomaticRegistrationAttempted && failed.AutomaticRegistrationError != "" &&
            failed.Stage == CaptureStage.PatientIdentified && registration.Load(invalid.Id) is null,
            "pre-submission registration failure records error without changing reception state");
        check(!await worker.ProcessNextAsync(default) && Directory.GetFiles(Path.Combine(settings.OqsRoot, "req"), "*.xml").Length == 1,
            "invalid insurance does not generate repeated automatic registration attempts");
        settings.AutoRegisterReferenceNumber = false; settings.Save(root);
        check(!AgentSettings.Load(root).AutoRegisterReferenceNumber, "automatic registration setting persists OFF");
    }
}
