using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ReceptionAgent.Reception;

public sealed class CaptureStore
{
    internal SemaphoreSlim WorkflowGate { get; } = new(1, 1);
    private readonly string connectionString;
    private readonly string directory;
    public CaptureStore(string directory)
    {
        this.directory = directory; Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "reception.sqlite3"), Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS captures (id TEXT PRIMARY KEY, file_name TEXT NOT NULL COLLATE NOCASE UNIQUE, hash TEXT NOT NULL, data TEXT NOT NULL, xml BLOB NOT NULL, stage INTEGER NOT NULL, updated TEXT NOT NULL, conflict INTEGER NOT NULL DEFAULT 0); CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA synchronous=FULL;"; command.ExecuteNonQuery(); return connection;
    }
    public IDisposable AcquireMonitorLease() => new FileStream(Path.Combine(directory, "monitor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public bool Capture(CaptureRecord record, byte[] xml)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT hash FROM captures WHERE file_name=$name"; query.Parameters.AddWithValue("$name", record.FileName);
        var existing = query.ExecuteScalar() as string;
        if (existing is not null)
        {
            if (existing != record.ContentHash)
            {
                using var conflict = connection.CreateCommand(); conflict.Transaction = transaction;
                conflict.CommandText = "UPDATE captures SET conflict=1 WHERE file_name=$name"; conflict.Parameters.AddWithValue("$name", record.FileName); conflict.ExecuteNonQuery();
            }
            transaction.Commit(); return false;
        }
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO captures (id,file_name,hash,data,xml,stage,updated) VALUES ($id,$name,$hash,$data,$xml,$stage,$updated)";
        insert.Parameters.AddWithValue("$id", record.Id); insert.Parameters.AddWithValue("$name", record.FileName); insert.Parameters.AddWithValue("$hash", record.ContentHash);
        insert.Parameters.AddWithValue("$data", JsonSerializer.Serialize(record)); insert.Parameters.AddWithValue("$xml", xml);
        insert.Parameters.AddWithValue("$stage", (int)record.Stage); insert.Parameters.AddWithValue("$updated", record.UpdatedAt.ToString("O"));
        insert.ExecuteNonQuery(); transaction.Commit(); return true;
    }
    public void Save(CaptureRecord record)
    {
        record.UpdatedAt = DateTimeOffset.Now;
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE captures SET data=$data,stage=$stage,updated=$updated WHERE id=$id AND conflict=0";
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(record)); command.Parameters.AddWithValue("$stage", (int)record.Stage);
        command.Parameters.AddWithValue("$updated", record.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$id", record.Id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("取込記録が競合しています。同名XMLの内容を確認してください。");
    }
    public IReadOnlyList<DateOnly> ListDates()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT substr(json_extract(data,'$.GeneratedAt'),1,10) AS day FROM captures ORDER BY day DESC";
        using var reader = command.ExecuteReader(); var dates = new List<DateOnly>();
        while (reader.Read())
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return dates;
    }
    public IReadOnlyList<CaptureRecord> List(bool pendingOnly = false, int limit = 500, DateOnly? date = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (pendingOnly) conditions.Add("conflict=0 AND stage NOT IN ($done,$review)");
        if (date.HasValue)
        {
            conditions.Add("substr(json_extract(data,'$.GeneratedAt'),1,10)=$date");
            command.Parameters.AddWithValue("$date", date.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }
        command.CommandText = "SELECT data,conflict FROM captures " + (conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) + " " : "") + "ORDER BY updated " + (pendingOnly ? "ASC" : "DESC") + " LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (pendingOnly) { command.Parameters.AddWithValue("$done", (int)CaptureStage.Completed); command.Parameters.AddWithValue("$review", (int)CaptureStage.NeedsReview); }
        using var reader = command.ExecuteReader(); var records = new List<CaptureRecord>();
        while (reader.Read())
        {
            var record = JsonSerializer.Deserialize<CaptureRecord>(reader.GetString(0)) ?? throw new InvalidDataException("取込記録が不正です。");
            record.Conflict = reader.GetInt32(1) != 0; records.Add(record);
        }
        return records;
    }
    public CaptureRecord Get(string id)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT data,conflict FROM captures WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("取込記録がありません。");
        var record = JsonSerializer.Deserialize<CaptureRecord>(reader.GetString(0)) ?? throw new InvalidDataException("取込記録が不正です。");
        record.Conflict = reader.GetInt32(1) != 0;
        return record;
    }
    public byte[] ReadXml(string id)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT xml FROM captures WHERE id=$id AND conflict=0"; command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as byte[] ?? throw new InvalidDataException("保存XMLを読み取れません。");
    }
}
