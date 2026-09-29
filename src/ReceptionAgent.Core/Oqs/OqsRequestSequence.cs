using System.Globalization;
using System.Text;

namespace ReceptionAgent.Oqs;

public sealed class OqsRequestSequence(string stateDirectory)
{
    public string Next(DateOnly day)
    {
        Directory.CreateDirectory(stateDirectory);
        // Serialize callers/processes independently from the atomically replaced sequence file.
        using var guard = new FileStream(Path.Combine(stateDirectory, "reference_request_sequence.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string path = Path.Combine(stateDirectory, "reference_request_sequence.txt");
        string date = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        int next = 1;
        if (File.Exists(path))
        {
            string saved = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (saved.Length != 12 || !DateOnly.TryParseExact(saved[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var previousDay) || !int.TryParse(saved[8..], NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
                throw new InvalidDataException("連番ファイルが不正です。初期化せず管理者が確認してください。");
            if (previousDay > day) throw new InvalidOperationException("前回の連番日付より時計が戻っています。");
            if (previousDay == day) next = number + 1;
        }
        if (next > 9999) throw new InvalidOperationException("当日の4桁連番を使い切りました。");
        string value = date + next.ToString("D4", CultureInfo.InvariantCulture);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(Encoding.UTF8.GetBytes(value)); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return value;
    }
}
