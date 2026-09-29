using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReceptionAgent.Oqs.ReferenceNumber;

public enum RegistrationStage { Pending, UploadWaiting, DownloadReady, ResultWaiting, Completed, Failed, NotFound }

public sealed class RegistrationEntry
{
    public string PatientId { get; set; } = "";
    public RegistrationStage Stage { get; set; }
    public string Identifier { get; set; } = "";
    public string InsuranceFingerprint { get; set; } = "";
    public string UploadFile { get; set; } = "";
    public string DownloadFile { get; set; } = "";
    public string ReceptionNumber { get; set; } = "";
    public string ResultCode { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    [JsonIgnore] public bool IsTerminal => Stage is RegistrationStage.Completed or RegistrationStage.Failed or RegistrationStage.NotFound;
}

public sealed class ReferenceRegistrationJob
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string InstitutionCode { get; set; } = "";
    public string OqsRoot { get; set; } = "";
    public long From { get; set; }
    public long To { get; set; }
    public string InsuranceBranchField { get; set; } = "枝番";
    public bool RemoveChartBranchDigit { get; set; } = true;
    public List<RegistrationEntry> Entries { get; set; } = [];
    [JsonIgnore] public long NextPatient => From + Entries.Count(e => e.IsTerminal);
    [JsonIgnore] public bool IsFinished => NextPatient > To;
    [JsonIgnore] public long? LastProcessed => Entries.LastOrDefault(e => e.IsTerminal) is { } e ? long.Parse(e.PatientId) : null;
    [JsonIgnore] public string LastSuccessful => Entries.LastOrDefault(e => e.Stage == RegistrationStage.Completed)?.PatientId ?? "—";

    public void Validate()
    {
        if (Version != 1 || From < 1 || To < From || To > 999999999 || To - From >= 100000)
            throw new InvalidDataException("範囲は1～999999999、1回最大100000番号です。履歴の版・範囲を確認してください。");
        if (InstitutionCode.Length != 10 || InstitutionCode.Any(c => c is < '0' or > '9') || !Path.IsPathFullyQualified(OqsRoot))
            throw new InvalidDataException("医療機関コード（10桁）とOQSの絶対パスを指定してください。");
        if (Entries.Count > To - From + 1) throw new InvalidDataException("履歴件数が範囲を超えています。");
        for (int i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            if (entry.PatientId != (From + i).ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                !Enum.IsDefined(entry.Stage) || (i < Entries.Count - 1 && !entry.IsTerminal))
                throw new InvalidDataException("履歴の患者順序が不正です。自動初期化は行いません。");
            if (entry.Stage is RegistrationStage.UploadWaiting or RegistrationStage.DownloadReady or RegistrationStage.ResultWaiting or RegistrationStage.Completed)
            {
                if (!ValidName(entry.UploadFile, "01") || string.IsNullOrWhiteSpace(entry.Identifier) || entry.InsuranceFingerprint.Length != 64)
                    throw new InvalidDataException("送信履歴の識別情報が不足しています。");
            }
            if (entry.Stage is RegistrationStage.DownloadReady or RegistrationStage.ResultWaiting or RegistrationStage.Completed)
                if (string.IsNullOrWhiteSpace(entry.ReceptionNumber)) throw new InvalidDataException("受付番号がありません。");
            if (entry.Stage is RegistrationStage.ResultWaiting or RegistrationStage.Completed)
                if (!ValidName(entry.DownloadFile, "02")) throw new InvalidDataException("結果要求の履歴が不正です。");
        }
    }

    private static bool ValidName(string name, string phase) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "\\AOQSmuimm" + phase + "req_[0-9]{12}\\.xml\\z");
}

public sealed class RegistrationJobStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public IDisposable AcquireLease()
    {
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "bulk.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public string FilePath(Guid id) => Path.Combine(directory, id.ToString("N") + ".json");
    public ReferenceRegistrationJob Load(Guid id)
    {
        var job = JsonSerializer.Deserialize<ReferenceRegistrationJob>(File.ReadAllBytes(FilePath(id)), Options)
            ?? throw new InvalidDataException("登録履歴を読み取れません。");
        if (job.Id != id) throw new InvalidDataException("履歴IDが一致しません。");
        job.Validate();
        // No requests/results exist yet: update the draft to the user-confirmed mapping.
        // Preserve mappings of started runs so a legacy mismatch cannot silently change identity.
        if (job.Entries.Count == 0)
        { job.InsuranceBranchField = "枝番"; job.RemoveChartBranchDigit = true; }
        return job;
    }
    public IEnumerable<Guid> ListIds() => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
            .Where(x => Guid.TryParseExact(x, "N", out _)).Select(x => Guid.ParseExact(x!, "N"))
        : [];
    // Caller holds AcquireLease for the entire read-modify-write operation / run.
    public void Save(ReferenceRegistrationJob job)
    {
        job.Validate();
        Directory.CreateDirectory(directory);
        string path = FilePath(job.Id), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, job, Options); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
