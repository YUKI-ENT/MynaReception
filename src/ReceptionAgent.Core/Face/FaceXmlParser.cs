using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace ReceptionAgent.Face;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FaceXmlEncoding { Utf8, ShiftJis }
public sealed record FaceIdentity(string NameKana, DateOnly Birthdate, string InstitutionCode, FaceInsurance? Insurance = null, string? PatientName = null);

public static class FaceXmlParser
{
    public static FaceIdentity Parse(byte[] bytes, FaceXmlEncoding encoding)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("face XMLがサイズ上限を超えています。");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding decoder = encoding switch
        {
            FaceXmlEncoding.Utf8 => new UTF8Encoding(false, true),
            FaceXmlEncoding.ShiftJis => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new InvalidDataException("face XMLの文字コード設定が不正です。")
        };
        string text = decoder.GetString(bytes).TrimStart('\uFEFF');
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        if (xml.Declaration?.Encoding is { } declared && Encoding.GetEncoding(declared).CodePage != decoder.CodePage)
            throw new InvalidDataException("XML宣言と設定の文字コードが異なります。UTF-8／Shift_JIS設定を確認してください。");
        var root = xml.Root;
        if (root?.Name != "XmlMsg") throw new InvalidDataException("face XMLのルートが不正です。");
        var header = One(root, "MessageHeader"); var body = One(root, "MessageBody");
        if (One(header, "SegmentOfResult").Value != "1" || One(body, "ProcessingResultStatus").Value != "1")
            throw new InvalidDataException("正常終了した資格確認XMLではありません。");
        var record = One(One(body, "ResultList"), "ResultOfQualificationConfirmation");
        string name = One(record, "NameKana").Value.Trim();
        string birth = One(record, "Birthdate").Value.Trim();
        if (!DateOnly.TryParseExact(birth, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidDataException("Birthdateが有効な西暦8桁の日付ではありません。");
        if (name.Length > 200 || PatientNameMatcher.NormalizeKana(name).Length == 0)
            throw new InvalidDataException("NameKanaが空欄または長すぎます。");
        string institution = One(header, "MedicalInstitutionCode").Value.Trim();
        if (institution.Length != 10 || institution.Any(c => c is < '0' or > '9')) throw new InvalidDataException("XMLの医療機関コードが不正です。");
        return new(name, date, institution, new FaceInsurance(Optional(record, "InsurerNumber"), Optional(record, "InsuredCardSymbol") ?? "",
            Optional(record, "InsuredIdentificationNumber"), Optional(record, "InsuredBranchNumber")), Optional(record, "Name"));
    }
    private static string? Optional(XElement parent, string name)
    {
        var values = parent.Elements(name).ToArray();
        if (values.Length > 1) throw new InvalidDataException(name + "が重複しています。");
        return values.SingleOrDefault()?.Value;
    }
    private static XElement One(XElement parent, string name)
    {
        var values = parent.Elements(name).ToArray();
        return values.Length == 1 ? values[0] : throw new InvalidDataException(name + "を1件に特定できません。");
    }
    public static async Task<FaceIdentity> LoadAsync(string path, FaceXmlEncoding encoding, CancellationToken token)
    {
        byte[]? previous = null;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new MemoryStream();
                var buffer = new byte[8192]; int count;
                while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("face XMLがサイズ上限を超えています。");
                    output.Write(buffer, 0, count);
                }
                byte[] current = output.ToArray();
                if (previous is not null && previous.AsSpan().SequenceEqual(current)) return Parse(current, encoding);
                previous = current;
            }
            catch (IOException) { previous = null; }
            catch (XmlException) { previous = null; }
            await Task.Delay(300, token).ConfigureAwait(false);
        }
        throw new IOException("XMLを安定して読み取れません。書込み完了・移動先・文字コードを確認してください。");
    }
}
