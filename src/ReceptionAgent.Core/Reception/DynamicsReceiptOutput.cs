using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ReceptionAgent.Reception;

public enum ReceptionLinkMode { ICall, DirectOutput, None }

/// <summary>Durable, idempotent outbox. receipt.txt belongs to Dynamics and is never overwritten.</summary>
public sealed class DynamicsReceiptOutput
{
    private sealed record Entry(string Id, string Line);
    private sealed class Journal
    {
        public List<Entry> Pending { get; set; } = [];
        public List<Entry> Batch { get; set; } = [];
        public bool Ready { get; set; }
        public HashSet<string> Accepted { get; set; } = [];
    }
    public static string Format(string patientId, string patientName, DateTime receptionDate, DateTime sentAt, string receptionNo)
    {
        if (string.IsNullOrWhiteSpace(patientId) || patientId.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException("枝番なしのカルテ番号が不正です。");
        foreach (string field in new[] { patientName, receptionNo })
            if (string.IsNullOrWhiteSpace(field) || field.IndexOfAny([',', '\r', '\n']) >= 0)
                throw new InvalidDataException("氏名・受付番号に空欄、カンマ、改行は使用できません。");
        string number = receptionNo.StartsWith("受付:", StringComparison.Ordinal) ? receptionNo : "受付:" + receptionNo;
        return string.Join(",", patientId, "0", "0", "0", "0", "0", patientName,
            receptionDate.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), sentAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture), number) + "\r\n";
    }
    private static Encoding Encoding932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public void Enqueue(string directory, string id, string line)
    {
        // Validate encoding before accepting the request.
        Encoding932().GetBytes(line);
        WithJournal(directory, (journal, save) =>
        {
            Recover(directory, journal, save);
            if (journal.Accepted.Add(id)) { journal.Pending.Add(new(id, line)); save(); }
            Publish(directory, journal, save);
        });
    }
    public void Flush(string directory) => WithJournal(directory, (journal, save) =>
    {
        Recover(directory, journal, save);
        Publish(directory, journal, save);
    });
    private static void WithJournal(string directory, Action<Journal, Action> action)
    {
        directory = AgentSettings.NormalizeRoot(directory);
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.Combine(directory, "receipt.agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string path = Path.Combine(directory, "receipt.agent.json");
        var journal = File.Exists(path) ? JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("受付出力の管理ファイルが不正です。") : new Journal();
        void Save()
        {
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, journal); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        action(journal, Save);
    }
    private static void WriteTemporary(string directory, IEnumerable<Entry> entries)
    {
        byte[] bytes = Encoding932().GetBytes(string.Concat(entries.Select(e => e.Line)));
        using var stream = new FileStream(Path.Combine(directory, "receipt.tmp"), FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    private static void Recover(string directory, Journal journal, Action save)
    {
        if (journal.Batch.Count == 0) return;
        string temporary = Path.Combine(directory, "receipt.tmp"), target = Path.Combine(directory, "receipt.txt");
        if (!journal.Ready)
        {
            WriteTemporary(directory, journal.Batch);
            journal.Ready = true; save();
        }
        if (File.Exists(temporary))
        {
            if (File.Exists(target)) return;
            try { File.Move(temporary, target); }
            catch (IOException) when (File.Exists(target)) { return; }
        }
        // Ready + no temporary file means the rename already succeeded, even if Dynamics consumed it.
        journal.Batch.Clear(); journal.Ready = false; save();
    }
    private static void Publish(string directory, Journal journal, Action save)
    {
        if (journal.Batch.Count > 0 || journal.Pending.Count == 0) return;
        WriteTemporary(directory, journal.Pending);
        if (File.Exists(Path.Combine(directory, "receipt.txt"))) return;
        journal.Batch = journal.Pending; journal.Pending = []; journal.Ready = false; save();
        Recover(directory, journal, save);
    }
}
