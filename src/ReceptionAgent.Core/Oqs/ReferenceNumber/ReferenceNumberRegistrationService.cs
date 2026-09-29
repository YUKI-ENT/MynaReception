using System.Globalization;
using System.Text;
using System.Xml;
using ReceptionAgent.Dynamics;

namespace ReceptionAgent.Oqs.ReferenceNumber;

public sealed record BulkProgress(string PatientId, RegistrationStage Stage, string Message);

public sealed class ReferenceNumberRegistrationService(RegistrationJobStore store, string sequenceDirectory,
    IDynamicsProvider? provider, TimeSpan? responseTimeout = null)
{
    public async Task RunAsync(Guid jobId, IProgress<BulkProgress>? progress, CancellationToken token)
    {
        using var lease = store.AcquireLease();
        var job = store.Load(jobId);
        var files = new OqsFileClient(job.OqsRoot, responseTimeout);
        files.ValidateFolders();
        var sequence = new OqsRequestSequence(sequenceDirectory);
        string Name(string phase) => "OQSmuimm" + phase + "req_" + sequence.Next(DateOnly.FromDateTime(DateTime.Now)) + ".xml";
        void Save(RegistrationEntry entry, string message)
        {
            entry.UpdatedAt = DateTimeOffset.Now;
            store.Save(job);
            progress?.Report(new(entry.PatientId, entry.Stage, message));
        }
        while (!job.IsFinished)
        {
            token.ThrowIfCancellationRequested();
            var entry = job.Entries.LastOrDefault(e => !e.IsTerminal);
            if (entry is null)
            {
                entry = new RegistrationEntry { PatientId = job.NextPatient.ToString(CultureInfo.InvariantCulture) };
                job.Entries.Add(entry);
                Save(entry, "患者・保険情報を取得中");
            }
            if (entry.Stage == RegistrationStage.Pending)
            {
                if (provider is null) throw new InvalidOperationException("Dynamicsの患者マスター取得が未接続です。保険情報取得クエリの確定が必要です。");
                DynamicsPatient? patient;
                try { patient = await provider.GetPatientAsync(entry.PatientId, token).ConfigureAwait(false); }
                catch (DynamicsPatientDataException ex)
                {
                    entry.Stage = RegistrationStage.Failed; entry.ResultCode = ex.Code;
                    Save(entry, "患者・保険情報の確認が必要です（未送信）"); continue;
                }
                if (patient is null) { entry.Stage = RegistrationStage.NotFound; Save(entry, "患者なし（登録成功には含めません）"); continue; }
                if (patient.Identity.PatientId != entry.PatientId) throw new InvalidDataException("Dynamicsの患者IDが要求と一致しません。");
                var insurance = patient.Insurance;
                var target = new ReferenceRegistrationTarget(entry.PatientId, insurance.InsurerNumber,
                    insurance.InsuredCardSymbol, insurance.InsuredIdentificationNumber, insurance.InsuredBranchNumber, insurance.IsPublicAssistance);
                byte[] request;
                try
                {
                    entry.Identifier = ReferenceNumberRequestBuilder.CreateIdentifier();
                    request = new ReferenceNumberRequestBuilder().Build(job.InstitutionCode, entry.Identifier, target);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    entry.Stage = RegistrationStage.Failed;
                    entry.ResultCode = insurance.IsPublicAssistance ? "PUBLIC_ASSISTANCE_UNSUPPORTED" : "INVALID_INSURANCE";
                    Save(entry, "保険情報の確認が必要です（未送信）");
                    continue;
                }
                entry.InsuranceFingerprint = ReferenceNumberResultParser.Fingerprint(target);
                entry.UploadFile = Name("01");
                token.ThrowIfCancellationRequested();
                // Persist intent BEFORE publishing. A crash or network failure must never cause an automatic duplicate registration.
                entry.Stage = RegistrationStage.UploadWaiting;
                Save(entry, "受付番号取得：登録要求を送信中");
                files.Submit(entry.UploadFile, request);
            }
            if (entry.Stage == RegistrationStage.UploadWaiting)
            {
                progress?.Report(new(entry.PatientId, entry.Stage, "受付番号取得：応答待ち"));
                var xml = await files.WaitAsync(entry.UploadFile, token).ConfigureAwait(false);
                entry.ReceptionNumber = ReferenceNumberResultParser.UploadReception(xml, job, entry);
                entry.Stage = RegistrationStage.DownloadReady;
                Save(entry, "受付番号取得済み");
            }
            if (entry.Stage == RegistrationStage.DownloadReady)
            {
                token.ThrowIfCancellationRequested();
                byte[] request = DownloadRequest(job.InstitutionCode, entry.ReceptionNumber);
                entry.DownloadFile = Name("02");
                entry.Stage = RegistrationStage.ResultWaiting;
                Save(entry, "処理要求中：登録結果を要求");
                files.Submit(entry.DownloadFile, request);
            }
            if (entry.Stage == RegistrationStage.ResultWaiting)
            {
                progress?.Report(new(entry.PatientId, entry.Stage, "処理要求中：最終結果待ち"));
                var xml = await files.WaitAsync(entry.DownloadFile, token).ConfigureAwait(false);
                var result = ReferenceNumberResultParser.Final(xml, job, entry);
                entry.ResultCode = result.Code;
                entry.Stage = result.Success ? RegistrationStage.Completed : RegistrationStage.Failed;
                Save(entry, result.Success ? "登録完了" : "登録失敗：結果コードを確認してください");
            }
        }
    }

    private static byte[] DownloadRequest(string institution, string reception)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var output = new MemoryStream();
        using (var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = Encoding.GetEncoding(932), Indent = true }))
        {
            xml.WriteStartDocument(false); xml.WriteStartElement("XmlMsg");
            xml.WriteStartElement("MessageHeader"); xml.WriteElementString("MedicalInstitutionCode", institution); xml.WriteEndElement();
            xml.WriteStartElement("MessageBody"); xml.WriteElementString("ReceptionNumber", reception); xml.WriteEndElement();
            xml.WriteEndElement(); xml.WriteEndDocument();
        }
        return output.ToArray();
    }
}
