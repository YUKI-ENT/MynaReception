using ReceptionAgent;
using ReceptionAgent.Face;
using ReceptionAgent.Oqs.ReferenceNumber;
using ReceptionAgent.Reception;

internal static class MonitorRecoveryTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "monitor-recovery-" + Guid.NewGuid().ToString("N"));
        var settings = new AgentSettings { FaceXmlDirectory = Path.Combine(root, "face"), FaceTrashDirectory = Path.Combine(root, "trash"),
            ICallRequestDirectory = Path.Combine(root, "req"), ICallResponseDirectory = Path.Combine(root, "res"),
            OqsRoot = Path.Combine(root, "oqs"), AutoRegisterReferenceNumber = true, ReconcileReferenceNumber = false };
        foreach (string folder in new[] { settings.FaceTrashDirectory, settings.ICallRequestDirectory, settings.ICallResponseDirectory }) Directory.CreateDirectory(folder);
        settings.ValidateMonitoring();
        check(!Directory.Exists(settings.OqsRoot), "monitoring configuration validates without contacting or creating an offline OQS share");
        var store = new CaptureStore(Path.Combine(root, "db"));
        var registration = new SingleReferenceRegistrationService(Path.Combine(root, "registration"));
        Task<FaceLookupResult> Lookup(FaceIdentity face, CancellationToken token) => throw new Exception("XML reference should bypass lookup");
        var monitor = new FaceCaptureMonitor(store, settings, Lookup, registration);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var run = monitor.RunAsync(stop.Token);
        async Task Until(Func<bool> condition)
        {
            while (!condition())
            {
                if (run.IsCompleted) await run;
                await Task.Delay(100, stop.Token);
            }
        }
        try
        {
            await Until(() => monitor.CaptureStatus.Contains("アクセス待ち"));
            check(!monitor.FoldersAccessible && !run.IsCompleted, "monitor stays running when the source folder is unavailable at startup");
            Directory.CreateDirectory(settings.FaceXmlDirectory);
            await Until(() => monitor.CaptureStatus.Contains(Path.Combine(settings.OqsRoot, "req")));
            check(!monitor.FoldersAccessible && !run.IsCompleted, "automatic registration waits for OQS folders without ending monitoring");
            Directory.CreateDirectory(Path.Combine(settings.OqsRoot, "req")); Directory.CreateDirectory(Path.Combine(settings.OqsRoot, "res"));
            string name = $"OQSsiquc01res_face_recovery_{DateTime.Now:yyyyMMddHHmmss}.xml";
            const string xml = "<XmlMsg><MessageHeader><SegmentOfResult>1</SegmentOfResult><MedicalInstitutionCode>0110012345</MedicalInstitutionCode></MessageHeader><MessageBody><ResultList><ResultOfQualificationConfirmation><Name>試験太郎</Name><NameKana>ﾃｽﾄ ﾀﾛｳ</NameKana><Birthdate>20241021</Birthdate><ReferenceNumber>0011</ReferenceNumber></ResultOfQualificationConfirmation></ResultList><ProcessingResultStatus>1</ProcessingResultStatus></MessageBody></XmlMsg>";
            await File.WriteAllTextAsync(Path.Combine(settings.FaceXmlDirectory, name), xml);
            await Until(() => monitor.FoldersAccessible && store.List().Count == 1);
            check(!run.IsCompleted, "folder recovery automatically resumes XML capture on the original monitoring task");
            await Until(() => store.List().Single().PendingRequest is not null);
            string requestId = store.List().Single().PendingRequest!.RequestId;
            Directory.Move(settings.FaceXmlDirectory, settings.FaceXmlDirectory + ".offline");
            await Until(() => !monitor.FoldersAccessible);
            check(monitor.CaptureStatus.Contains("アクセス待ち") && !run.IsCompleted, "source disconnect pauses access without stopping the monitor");
            Directory.Move(settings.FaceXmlDirectory + ".offline", settings.FaceXmlDirectory);
            await Until(() => monitor.FoldersAccessible);
            check(store.List().Count == 1 && store.List().Single().PendingRequest?.RequestId == requestId, "reconnect preserves captured XML and the pending request ID without duplicate processing");
            Directory.Move(settings.ICallResponseDirectory, settings.ICallResponseDirectory + ".offline");
            await Until(() => !monitor.FoldersAccessible);
            check(!run.IsCompleted, "iCall response disconnect also enters automatic access wait");
            Directory.Move(settings.ICallResponseDirectory + ".offline", settings.ICallResponseDirectory);
            await Until(() => monitor.FoldersAccessible);
            check(!run.IsCompleted && store.List().Single().PendingRequest?.RequestId == requestId, "iCall folder recovery retains the same pending operation");
            Directory.Move(settings.FaceXmlDirectory, settings.FaceXmlDirectory + ".offline");
            await Until(() => !monitor.FoldersAccessible);
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
            check(run.IsCompleted, "operator stop cancels folder retry promptly and prevents automatic resumption");
        }
        finally
        {
            stop.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
    }
}
