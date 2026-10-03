using System.Text.Json;

namespace ReceptionAgent;

public sealed class AgentSettings
{
    public string OqsRoot { get; set; } = "";
    public string FaceXmlDirectory { get; set; } = "";
    public string FaceTrashDirectory { get; set; } = "";
    public string ICallRequestDirectory { get; set; } = "";
    public string ICallResponseDirectory { get; set; } = "";
    public bool OpenResponseFolderOnMonitorStart { get; set; }
    public bool AutoRegisterReferenceNumber { get; set; }
    public bool ReconcileReferenceNumber { get; set; } = true;
    public bool MarkArrived { get; set; }
    public bool LinkReservation { get; set; }
    public Face.FaceXmlEncoding FaceEncoding { get; set; } = Face.FaceXmlEncoding.Utf8;
    public static AgentSettings Load(string directory)
    {
        string path = Path.Combine(directory, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("設定ファイルを読み取れません。") : new();
    }
    public void Save(string directory)
    {
        string normalized = string.IsNullOrWhiteSpace(OqsRoot) ? "" : NormalizeRoot(OqsRoot);
        if (AutoRegisterReferenceNumber && normalized.Length == 0) throw new ArgumentException("自動照会番号登録にはOQS連携フォルダーを指定してください。");
        string faceDirectory = string.IsNullOrWhiteSpace(FaceXmlDirectory) ? "" : NormalizeRoot(FaceXmlDirectory);
        string OptionalPath(string value) => string.IsNullOrWhiteSpace(value) ? "" : NormalizeRoot(value);
        string trash = OptionalPath(FaceTrashDirectory), request = OptionalPath(ICallRequestDirectory), response = OptionalPath(ICallResponseDirectory);
        if (request.Length > 0 && string.Equals(request, response, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("requestとresponseには別のフォルダーを指定してください。");
        if (!Enum.IsDefined(FaceEncoding)) throw new InvalidDataException("face XML文字コードの設定が不正です。");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json"), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new AgentSettings { OqsRoot = normalized, FaceXmlDirectory = faceDirectory, FaceEncoding = FaceEncoding,
                AutoRegisterReferenceNumber = AutoRegisterReferenceNumber, ReconcileReferenceNumber = ReconcileReferenceNumber, FaceTrashDirectory = trash, ICallRequestDirectory = request, ICallResponseDirectory = response,
                OpenResponseFolderOnMonitorStart = OpenResponseFolderOnMonitorStart, MarkArrived = MarkArrived, LinkReservation = LinkReservation }, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temp, path, true); OqsRoot = normalized; FaceXmlDirectory = faceDirectory;
            FaceTrashDirectory = trash; ICallRequestDirectory = request; ICallResponseDirectory = response;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string NormalizeRoot(string value)
    {
        string root = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!Path.IsPathFullyQualified(root) || root.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || root.IndexOfAny(['*', '?']) >= 0)
            throw new ArgumentException("フォルダーを絶対パスまたはUNCパスで指定してください。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    public void ValidateMonitoring()
    {
        if (AutoRegisterReferenceNumber) new Oqs.OqsFileClient(NormalizeRoot(OqsRoot)).ValidateFolders();
        NormalizeRoot(FaceXmlDirectory); NormalizeRoot(FaceTrashDirectory);
        NormalizeRoot(ICallRequestDirectory); NormalizeRoot(ICallResponseDirectory);
        if (string.Equals(NormalizeRoot(ICallRequestDirectory), NormalizeRoot(ICallResponseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("requestとresponseには別のフォルダーを指定してください。");
        if (!Enum.IsDefined(FaceEncoding)) throw new InvalidDataException("文字コード設定が不正です。");
    }
}
