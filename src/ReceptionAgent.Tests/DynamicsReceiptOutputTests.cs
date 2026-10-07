using System.Text;
using ReceptionAgent.Reception;

internal static class DynamicsReceiptOutputTests
{
    public static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "receipt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = new DynamicsReceiptOutput();
            var date = new DateTime(2026, 9, 27, 21, 2, 16);
            string line = DynamicsReceiptOutput.Format("11", "結城　和央", date, date, "001");
            check(line == "11,0,0,0,0,0,結城　和央,2026/09/27,21:02:16,受付:001\r\n", "receipt exact format");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(932);
            string receipt = Path.Combine(directory, "receipt.txt"), temporary = Path.Combine(directory, "receipt.tmp");
            output.Enqueue(directory, "a", line);
            check(File.ReadAllBytes(receipt).SequenceEqual(encoding.GetBytes(line)), "receipt CP932 without BOM");
            output.Enqueue(directory, "b", line);
            output.Enqueue(directory, "c", line);
            output.Enqueue(directory, "b", line);
            check(File.ReadAllText(receipt, encoding) == line, "existing receipt not changed");
            check(File.ReadAllText(temporary, encoding) == line + line, "multiple patients accumulated without duplicate");
            File.Delete(receipt);
            new DynamicsReceiptOutput().Flush(directory);
            check(File.ReadAllText(receipt, encoding) == line + line && !File.Exists(temporary), "pending batch renamed after deletion and restart");
            File.Delete(receipt);
            new DynamicsReceiptOutput().Enqueue(directory, "a", line);
            check(!File.Exists(receipt), "consumed patient not exported again");
            foreach (string invalid in new[] { "", "患者,氏名", "患者\r\n氏名" })
            {
                try { DynamicsReceiptOutput.Format("11", invalid, date, date, "001"); check(false, "invalid receipt field"); }
                catch (InvalidDataException) { check(true, "invalid receipt field rejected"); }
            }
            try { output.Enqueue(directory, "emoji", DynamicsReceiptOutput.Format("11", "患者😀", date, date, "001")); check(false, "unrepresentable CP932"); }
            catch (EncoderFallbackException) { check(true, "unrepresentable CP932 rejected without data loss"); }
            string settingsDirectory = Path.Combine(directory, "settings");
            var settings = new ReceptionAgent.AgentSettings { ReceptionLinkMode = ReceptionLinkMode.DirectOutput, DynamicsReceiptDirectory = directory };
            settings.Save(settingsDirectory);
            var loaded = ReceptionAgent.AgentSettings.Load(settingsDirectory);
            check(loaded.ReceptionLinkMode == ReceptionLinkMode.DirectOutput && loaded.DynamicsReceiptDirectory == directory, "receipt settings round trip");
            settings.ReceptionLinkMode = ReceptionLinkMode.None; settings.Save(settingsDirectory);
            check(ReceptionAgent.AgentSettings.Load(settingsDirectory).ReceptionLinkMode == ReceptionLinkMode.None, "disabled mode round trip");
            var store = new CaptureStore(Path.Combine(directory, "store"));
            var patient = new ReceptionAgent.Face.FacePatientMatch("11", "結城　和央", "ユウキ カズオ", new DateOnly(1980, 1, 1), ["110"]);
            var record = new CaptureRecord { FileName = "test.xml", ContentHash = "test", GeneratedAt = DateTime.Today,
                Stage = CaptureStage.Completed, ReceptionNo = "001", ReservationPatientName = patient.Name,
                Lookup = new([patient], patient, false, "test") };
            store.Capture(record, []);
            var workflow = new ReceptionWorkflow(store, (_, _) => throw new Exception("lookup should not run"), settings);
            try { workflow.QueueManualAsync(record.Id, "link", CancellationToken.None).GetAwaiter().GetResult(); check(false, "disabled workflow"); }
            catch (InvalidDataException) { check(true, "disabled mode prevents workflow dispatch"); }
            settings.ReceptionLinkMode = ReceptionLinkMode.DirectOutput;
            workflow.QueueManualAsync(record.Id, "link", CancellationToken.None).GetAwaiter().GetResult();
            var saved = store.Get(record.Id);
            check(saved.PendingRequest is null && saved.Stage == CaptureStage.Completed && saved.DynamicsReceiptDirectory == directory,
                "direct workflow persists output without iCall request");
            try { workflow.QueueManualAsync(record.Id, "link", CancellationToken.None).GetAwaiter().GetResult(); check(false, "duplicate workflow"); }
            catch (InvalidDataException) { check(true, "direct workflow prevents repeat dispatch"); }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
