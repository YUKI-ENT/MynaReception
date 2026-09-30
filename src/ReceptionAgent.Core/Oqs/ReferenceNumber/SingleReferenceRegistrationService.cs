using System.Text.Json;
using System.Xml.Linq;
using ReceptionAgent.Reception;

namespace ReceptionAgent.Oqs.ReferenceNumber;

public enum ReferenceRegistrationState { Pending, RequestCreating, RequestSubmitted, ResultWaiting, Completed, Failed, Cancelled }

public sealed class SingleReferenceRegistrationJob
{
    public string CaptureId { get; set; } = "";
    public string InstitutionCode { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string OqsRoot { get; set; } = "";
    public ReferenceRegistrationTarget Target { get; set; } = new("", "", "", "", "");
    public string RequestFileName { get; set; } = "";
    public string ResponseFileName => RequestFileName.Replace("req_", "res_");
    public ReferenceRegistrationState State { get; set; }
    public string SegmentOfResult { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string ProcessingResultStatus { get; set; } = "";
    public string ProcessingResultCode { get; set; } = "";
    public string ProcessingResultMessage { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SingleReferenceRegistrationService(string directory, TimeSpan? timeout = null)
{
    private readonly SemaphoreSlim registrationGate = new(1, 1);
    private string JobPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("取込IDが不正です。");
        return Path.Combine(directory, id + ".json");
    }
    public SingleReferenceRegistrationJob? Load(string id)
    {
        string path = JobPath(id);
        return File.Exists(path) ? ReadJob(path)
            ?? throw new InvalidDataException("登録履歴が不正です。") : null;
    }
    private static SingleReferenceRegistrationJob? ReadJob(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<SingleReferenceRegistrationJob>(input);
    }
    private void Save(SingleReferenceRegistrationJob job)
    {
        job.UpdatedAt = DateTimeOffset.Now;
        string path = JobPath(job.CaptureId), temp = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, job); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static ReferenceRegistrationTarget TargetFrom(CaptureRecord record)
    {
        if (record.Conflict || record.Lookup?.Selected is not { } patient || record.Face is not { } face)
            throw new InvalidDataException("患者が一意に特定された行を選択してください。");
        if (record.Lookup.Candidates.Count != 1) throw new InvalidDataException("患者候補が複数のため登録できません。");
        if (!string.IsNullOrWhiteSpace(face.ReferenceNumber))
            throw new InvalidDataException("face XMLには照会番号が登録済みです。");
        var insurance = face.Insurance ?? throw new InvalidDataException("face XMLに資格情報がありません。");
        return new(patient.PatientId, (insurance.InsurerNumber ?? "").Trim(), insurance.Symbol ?? "",
            insurance.Number ?? "", insurance.Branch ?? "");
    }
    public async Task<SingleReferenceRegistrationJob> RegisterAsync(CaptureRecord record, string oqsRoot, CancellationToken token)
    {
        await registrationGate.WaitAsync(token);
        try { return await RegisterCoreAsync(record, oqsRoot, token).ConfigureAwait(false); }
        finally { registrationGate.Release(); }
    }
    private async Task<SingleReferenceRegistrationJob> RegisterCoreAsync(CaptureRecord record, string oqsRoot, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        // This lease also prevents duplicate submissions from another ReceptionAgent process.
        using var lease = new FileStream(Path.Combine(directory, "registration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var job = Load(record.Id);
        if (job?.State is ReferenceRegistrationState.Completed or ReferenceRegistrationState.Failed) return job;
        if (job is null)
        {
            var target = TargetFrom(record);
            var files = new OqsFileClient(oqsRoot, timeout);
            files.ValidateFolders();
            string identifier = ReferenceNumberRequestBuilder.CreateIdentifier();
            byte[] xml = new ReferenceNumberRequestBuilder().Build(record.Face!.InstitutionCode, identifier, target, singlePatient: true);
            token.ThrowIfCancellationRequested();
            job = new() { CaptureId = record.Id, Target = target, InstitutionCode = record.Face.InstitutionCode,
                Identifier = identifier, OqsRoot = oqsRoot, RequestFileName = "OQSsiimm01req_" +
                new OqsRequestSequence(directory).Next(DateOnly.FromDateTime(DateTime.Now)) + ".xml",
                State = ReferenceRegistrationState.RequestSubmitted, Message = "要求保存済み／結果確認待ち" };
            // Persist intent and a local completed XML before publication. Never resend after uncertainty.
            string work = Path.Combine(directory, "Work"); Directory.CreateDirectory(work);
            using (var output = new FileStream(Path.Combine(work, job.RequestFileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(xml); output.Flush(true); }
            Save(job);
            try { files.Submit(job.RequestFileName, xml); }
            catch (Exception ex)
            { job.Message = "送信状態の確認が必要（再送しません）: " + ex.Message; Save(job); throw; }
        }
        try
        {
            job.State = ReferenceRegistrationState.ResultWaiting; job.Message = "単件登録の応答待ち"; Save(job);
            var xml = await new OqsFileClient(job.OqsRoot, timeout).WaitAsync(job.RequestFileName, token).ConfigureAwait(false);
            ApplyResult(xml, job);
        }
        catch (OperationCanceledException) { job.Message = "待機中断／送信済み要求は再送せず結果確認を再開できます"; Save(job); throw; }
        catch (Exception ex) { job.Message = "結果未確定／再確認可能: " + ex.Message; Save(job); throw; }
        Save(job);
        return job;
    }
    private static void ApplyResult(XDocument xml, SingleReferenceRegistrationJob job)
    {
        ParseResult(xml, job);
        job.Message = job.State == ReferenceRegistrationState.Completed ? "照会番号登録完了" :
            "照会番号登録失敗: " + string.Join(" ", new[] { job.ErrorCode, job.ErrorMessage,
                job.ProcessingResultCode, job.ProcessingResultMessage }.Where(v => !string.IsNullOrWhiteSpace(v)));
        if (job.State == ReferenceRegistrationState.Failed && job.Message.EndsWith(": "))
            job.Message += "SegmentOfResult=" + job.SegmentOfResult + "／ProcessingResultStatus=" + job.ProcessingResultStatus;
    }
    public async Task RefreshResultsAsync(CancellationToken token)
    {
        if (!await registrationGate.WaitAsync(0, token)) return;
        try { await RefreshResultsCoreAsync(token).ConfigureAwait(false); }
        finally { registrationGate.Release(); }
    }
    private async Task RefreshResultsCoreAsync(CancellationToken token)
    {
        if (!Directory.Exists(directory)) return;
        FileStream lease;
        try { lease = new FileStream(Path.Combine(directory, "registration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; } // A manual submission/result wait owns this registration history.
        using (lease)
        {
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                var job = ReadJob(path) ?? throw new InvalidDataException("登録履歴が不正です。");
                if (Path.GetFileName(path) != job.CaptureId + ".json") throw new InvalidDataException("登録履歴の取込IDが一致しません。");
                if (job.State is not (ReferenceRegistrationState.RequestSubmitted or ReferenceRegistrationState.ResultWaiting)) continue;
                if (!File.Exists(Path.Combine(job.OqsRoot, "res", job.ResponseFileName))) continue;
                string previousMessage = job.Message;
                try
                {
                    var xml = await new OqsFileClient(job.OqsRoot, TimeSpan.FromSeconds(1)).WaitAsync(job.RequestFileName, token).ConfigureAwait(false);
                    ApplyResult(xml, job);
                    Save(job);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    job.Message = "結果未確定／再確認可能: " + ex.Message;
                    if (previousMessage != job.Message) Save(job);
                }
            }
        }
    }
    public static void ParseResult(XDocument xml, SingleReferenceRegistrationJob job)
    {
        if (xml.Root?.Name != "XmlMsg" || xml.Root.Elements("MessageHeader").Count() != 1)
            throw new InvalidDataException("単件登録応答の構造を確認してください。");
        string Value(string name, bool required = false)
        {
            var values = xml.Root.Descendants(name).ToArray();
            if (values.Length > 1 || required && values.Length != 1)
                throw new InvalidDataException(name + "を1件に特定できません。");
            return values.SingleOrDefault()?.Value.Trim() ?? "";
        }
        // Exact response filename plus institution and identifier must identify this request.
        if (Value("MedicalInstitutionCode", true) != job.InstitutionCode || Value("ArbitraryFileIdentifier", true) != job.Identifier)
            throw new InvalidDataException("単件登録応答の医療機関・識別子が要求と一致しません。");
        string reference = Value("ReferenceNumber");
        if (reference != "" && reference != job.Target.ReferenceNumber)
            throw new InvalidDataException("応答の照会番号が要求と一致しません。");
        job.SegmentOfResult = Value("SegmentOfResult", true);
        job.ErrorCode = Value("ErrorCode"); job.ErrorMessage = Value("ErrorMessage");
        job.ProcessingResultStatus = Value("ProcessingResultStatus");
        job.ProcessingResultCode = Value("ProcessingResultCode"); job.ProcessingResultMessage = Value("ProcessingResultMessage");
        if (job.SegmentOfResult != "1" || job.ErrorCode != "")
        { job.State = ReferenceRegistrationState.Failed; return; }
        if (job.ProcessingResultStatus is not ("1" or "2"))
            throw new InvalidDataException("登録処理結果が未確定または未知です。成功扱いにしません。");
        job.State = job.ProcessingResultStatus == "1" ? ReferenceRegistrationState.Completed : ReferenceRegistrationState.Failed;
    }
}
