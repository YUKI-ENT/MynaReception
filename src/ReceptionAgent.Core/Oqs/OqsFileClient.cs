using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using ReceptionAgent.Oqs.ReferenceNumber;

namespace ReceptionAgent.Oqs;

public sealed class OqsFileClient(string root, TimeSpan? timeout = null)
{
    public void ValidateFolders()
    {
        if (!Directory.Exists(Path.Combine(root, "req")) || !Directory.Exists(Path.Combine(root, "res")))
            throw new DirectoryNotFoundException("OQSのreq/resフォルダーを確認してください。自動作成は行いません。");
    }
    public void Submit(string name, byte[] bytes)
    {
        ValidateName(name);
        ValidateFolders();
        string path = Path.Combine(root, "req", name);
        // Do not replace a request or reuse a name whose response still exists.
        if (File.Exists(Path.Combine(root, "res", name.Replace("req_", "res_"))))
            throw new IOException("同じファイル名の応答が存在します。要求を送信せず停止しました。");
        string temporary = Path.Combine(root, "req", ".receptionagent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<XDocument> WaitAsync(string requestName, CancellationToken token)
    {
        ValidateName(requestName);
        string path = Path.Combine(root, "res", requestName.Replace("req_", "res_"));
        var watch = Stopwatch.StartNew();
        byte[]? previous = null;
        while (watch.Elapsed < (timeout ?? TimeSpan.FromMinutes(3)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("応答XMLが上限サイズを超えています。");
                using var memory = new MemoryStream();
                byte[] buffer = new byte[8192];
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("応答XMLが上限サイズを超えています。");
                    memory.Write(buffer, 0, count);
                }
                byte[] current = memory.ToArray();
                // Require two identical reads before parsing a producer's partially written file.
                if (previous is not null && previous.AsSpan().SequenceEqual(current))
                { memory.Position = 0; return ReferenceNumberResultParser.Load(memory); }
                previous = current;
            }
            catch (IOException) { previous = null; }
            catch (XmlException) { previous = null; }
            await Task.Delay(300, token).ConfigureAwait(false);
        }
        throw new TimeoutException("応答待ちを一時停止しました。送信済み要求は再送せず、再開時に同じ応答を確認します。");
    }
    private static void ValidateName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\AOQSmuimm0[12]req_[0-9]{12}\.xml\z"))
            throw new ArgumentException("要求ファイル名が不正です。");
    }
}
