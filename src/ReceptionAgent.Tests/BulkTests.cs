using System.Xml.Linq;
using ReceptionAgent.Dynamics;
using ReceptionAgent.Oqs;
using ReceptionAgent.Oqs.ReferenceNumber;

internal static class BulkTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-bulk-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "req")); Directory.CreateDirectory(Path.Combine(root, "res"));
        var store = new RegistrationJobStore(Path.Combine(root, "jobs"));
        var job = new ReferenceRegistrationJob { InstitutionCode = "0000000000", OqsRoot = root, From = 11, To = 13 };
        using (store.AcquireLease()) store.Save(job);
        using (store.AcquireLease())
        {
            try { using var second = store.AcquireLease(); check(false, "exclusive job lease"); }
            catch (IOException) { check(true, "concurrent runner rejected"); }
        }
        var provider = new SyntheticProvider();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var responder = Respond(root, stop.Token);
        await new ReferenceNumberRegistrationService(store, root, provider, TimeSpan.FromSeconds(4)).RunAsync(job.Id, null, stop.Token);
        stop.Cancel();
        try { await responder; } catch (OperationCanceledException) { }
        var saved = store.Load(job.Id);
        check(saved.Entries.Select(e => e.Stage).SequenceEqual(new[] { RegistrationStage.Completed, RegistrationStage.Failed, RegistrationStage.NotFound }), "range records success, server error, missing patient separately");
        check(saved.IsFinished && saved.LastSuccessful == "11" && saved.LastProcessed == 13, "processed checkpoint never implies successful registration");
        check(saved.Entries[1].ResultCode == "TEST_ERROR", "patient result code persisted");
        string json = File.ReadAllText(store.FilePath(job.Id));
        check(!json.Contains("98765432") && !json.Contains("PRIVATE_NUMBER") && !json.Contains("SYNTHETIC_ERROR_MESSAGE"), "journal does not duplicate insurance or server free text");
        check(Directory.GetFiles(Path.Combine(root, "res")).Length == 4, "OQS response files preserved");
        int requests = Directory.GetFiles(Path.Combine(root, "req"), "*.xml").Length;
        await new ReferenceNumberRegistrationService(store, root, null).RunAsync(job.Id, null, CancellationToken.None);
        check(Directory.GetFiles(Path.Combine(root, "req"), "*.xml").Length == requests, "completed range does not resubmit on resume");

        // Simulate a crash after upload intent, with no provider and no req file remaining.
        var pending = new ReferenceRegistrationJob { InstitutionCode = job.InstitutionCode, OqsRoot = root, From = 15, To = 15 };
        var target = Target("15");
        var entry = new RegistrationEntry { PatientId = "15", Stage = RegistrationStage.UploadWaiting,
            Identifier = "crash-afi", InsuranceFingerprint = ReferenceNumberResultParser.Fingerprint(target), UploadFile = "OQSmuimm01req_202601010001.xml" };
        pending.Entries.Add(entry);
        using (store.AcquireLease()) store.Save(pending);
        try
        {
            await new ReferenceNumberRegistrationService(store, root, null, TimeSpan.FromMilliseconds(400)).RunAsync(pending.Id, null, CancellationToken.None);
            check(false, "missing response timeout");
        }
        catch (TimeoutException) { check(true, "missing response pauses without marking failed"); }
        check(store.Load(pending.Id).Entries[0].Stage == RegistrationStage.UploadWaiting && Directory.GetFiles(Path.Combine(root, "req"), "*.xml").Length == requests, "ambiguous submission is not automatically repeated");

        var final = FinalXml(job.InstitutionCode, entry.Identifier, "reception15", target, "1");
        entry.ReceptionNumber = "reception15";
        check(ReferenceNumberResultParser.Final(final, pending, entry).Success, "final result matched to institution, AFI, reception and insurance");
        final.Root!.Element("MessageHeader")!.Element("ArbitraryFileIdentifier")!.Value = "other-request";
        ExpectInvalid(() => ReferenceNumberResultParser.Final(final, pending, entry), "wrong AFI rejected", check);
        final = FinalXml(job.InstitutionCode, entry.Identifier, "reception15", target with { InsuredBranchNumber = "01" }, "1");
        ExpectInvalid(() => ReferenceNumberResultParser.Final(final, pending, entry), "wrong insurance branch rejected", check);
        final = FinalXml(job.InstitutionCode, entry.Identifier, "reception15", target, "9");
        ExpectInvalid(() => ReferenceNumberResultParser.Final(final, pending, entry), "unknown result never treated as success", check);
        final = FinalXml(job.InstitutionCode, entry.Identifier, "reception15", target, "1");
        final.Root!.Element("MessageBody")!.Add(new XElement(final.Root.Element("MessageBody")!.Element("BulkRegistrationUnit")!));
        ExpectInvalid(() => ReferenceNumberResultParser.Final(final, pending, entry), "duplicate patient results rejected", check);
        try
        {
            using var xml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE XmlMsg [<!ENTITY x SYSTEM 'file:///not-read'>]><XmlMsg>&x;</XmlMsg>"));
            ReferenceNumberResultParser.Load(xml); check(false, "DTD rejected");
        }
        catch (System.Xml.XmlException) { check(true, "XML external entities disabled"); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await new ReferenceNumberRegistrationService(store, root, null).RunAsync(pending.Id, null, canceled.Token); check(false, "cancel"); }
        catch (OperationCanceledException) { check(true, "cancellation keeps durable upload state"); }
        check(store.Load(pending.Id).Entries[0].Stage == RegistrationStage.UploadWaiting, "cancel preserves result-wait state");
        string responsePath = Path.Combine(root, "res", "OQSmuimm01res_202601010001.xml");
        File.WriteAllText(responsePath, "<XmlMsg>");
        var delayedWrite = Task.Run(async () => { await Task.Delay(400); File.WriteAllText(responsePath, "<XmlMsg><MessageBody/></XmlMsg>"); });
        var stable = await new OqsFileClient(root, TimeSpan.FromSeconds(3)).WaitAsync(entry.UploadFile, CancellationToken.None);
        await delayedWrite;
        check(stable.Root!.Element("MessageBody") is not null, "partial producer write retried until stable XML");
        try { new OqsFileClient(root).Submit(entry.UploadFile, [1]); check(false, "collision"); }
        catch (IOException) { check(true, "existing response blocks filename reuse"); }
        File.WriteAllText(store.FilePath(pending.Id), "broken-json");
        try { store.Load(pending.Id); check(false, "corrupt state"); }
        catch (System.Text.Json.JsonException) { check(true, "corrupt job not silently reset"); }
    }
    private static void ExpectInvalid(Action action, string name, Action<bool, string> check)
    { try { action(); check(false, name); } catch (InvalidDataException) { check(true, name); } }
    private static ReferenceRegistrationTarget Target(string id) => new(id, "98765432", "TEST", "PRIVATE_NUMBER", "00");
    private sealed class SyntheticProvider : IDynamicsProvider
    {
        public Task<DynamicsPatient?> GetPatientAsync(string id, CancellationToken token = default)
        {
            var t = Target(id);
            return Task.FromResult<DynamicsPatient?>(id == "13" ? null : new(new(id + "0", id), new(t.InsurerNumber, t.InsuredCardSymbol, t.InsuredIdentificationNumber, t.InsuredBranchNumber, false)));
        }
    }
    private static XDocument FinalXml(string institution, string afi, string reception, ReferenceRegistrationTarget t, string status) => new(
        new XElement("XmlMsg", new XElement("MessageHeader", new XElement("MedicalInstitutionCode", institution), new XElement("ArbitraryFileIdentifier", afi), new XElement("ReceptionNumber", reception)),
            new XElement("MessageBody", new XElement("BulkRegistrationUnit",
                new XElement("ReferenceNumberRegistrationInfo", new XElement("ReferenceNumber", t.PatientId), new XElement("InsurerNumber", t.InsurerNumber), new XElement("InsuredCardSymbol", t.InsuredCardSymbol), new XElement("InsuredIdentificationNumber", t.InsuredIdentificationNumber), new XElement("InsuredBranchNumber", t.InsuredBranchNumber)),
                new XElement("ProcessingResultStatus", status), new XElement("ProcessingResultCode", status == "1" ? "" : "TEST_ERROR"), new XElement("ProcessingResultMessage", "SYNTHETIC_ERROR_MESSAGE")))));
    private static async Task Respond(string root, CancellationToken token)
    {
        var uploads = new Dictionary<string, XDocument>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            foreach (string file in Directory.GetFiles(Path.Combine(root, "req"), "*.xml"))
            {
                string response = Path.Combine(root, "res", Path.GetFileName(file).Replace("req_", "res_"));
                if (File.Exists(response)) continue;
                using var input = File.OpenRead(file); var request = ReferenceNumberResultParser.Load(input);
                var header = request.Root!.Element("MessageHeader")!;
                if (Path.GetFileName(file).Contains("01req"))
                {
                    string patient = request.Root.Element("MessageBody")!.Element("ReferenceNumberRegistrationInfo")!.Element("ReferenceNumber")!.Value;
                    string reception = "reception" + patient; uploads.Add(reception, request);
                    new XDocument(new XElement("XmlMsg", new XElement(header), new XElement("MessageBody", new XElement("ReceptionNumber", reception)))).Save(response);
                }
                else
                {
                    string reception = request.Root.Element("MessageBody")!.Element("ReceptionNumber")!.Value;
                    var upload = uploads[reception]; var h = upload.Root!.Element("MessageHeader")!;
                    string patient = upload.Root.Element("MessageBody")!.Element("ReferenceNumberRegistrationInfo")!.Element("ReferenceNumber")!.Value;
                    FinalXml(h.Element("MedicalInstitutionCode")!.Value, h.Element("ArbitraryFileIdentifier")!.Value, reception, Target(patient), patient == "12" ? "2" : "1").Save(response);
                }
            }
            await Task.Delay(30, token);
        }
    }
}
