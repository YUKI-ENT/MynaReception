using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ReceptionAgent.Oqs.ReferenceNumber;

public sealed record RegistrationOutcome(bool Success, string Code);

public static class ReferenceNumberResultParser
{
    public static string Fingerprint(ReferenceRegistrationTarget target) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { target.PatientId, target.InsurerNumber.Trim(),
            target.InsuredCardSymbol, target.InsuredIdentificationNumber, target.InsuredBranchNumber }))));

    public static XDocument Load(Stream input)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    private static XElement Header(XDocument xml, ReferenceRegistrationJob job, RegistrationEntry entry)
    {
        if (xml.Root?.Name != "XmlMsg") throw new InvalidDataException("OQS応答のルートが不正です。");
        var header = xml.Root.Element("MessageHeader") ?? throw new InvalidDataException("応答ヘッダーがありません。");
        if (Value(header, "MedicalInstitutionCode") != job.InstitutionCode || Value(header, "ArbitraryFileIdentifier") != entry.Identifier)
            throw new InvalidDataException("応答の医療機関または任意識別子が一致しません。処理を保留しました。");
        return header;
    }
    public static string UploadReception(XDocument xml, ReferenceRegistrationJob job, RegistrationEntry entry)
    {
        var header = Header(xml, job, entry);
        var body = xml.Root!.Element("MessageBody") ?? throw new InvalidDataException("受付結果を確認してください。");
        // An accepted upload is not evidence of successful patient registration.
        if (Value(header, "ErrorCode") != "" || Value(body, "ProcessingResultStatus") is not ("" or "1"))
            throw new InvalidDataException("受付応答にエラーがあります。元の応答XMLを確認してください。");
        string reception = Value(body, "ReceptionNumber");
        if (string.IsNullOrWhiteSpace(reception) || reception.Length > 100)
            throw new InvalidDataException("受付番号を取得できません。応答を確認してください。");
        return reception;
    }
    public static RegistrationOutcome Final(XDocument xml, ReferenceRegistrationJob job, RegistrationEntry entry)
    {
        var header = Header(xml, job, entry);
        if (Value(header, "ReceptionNumber") != entry.ReceptionNumber || Value(header, "ErrorCode") != "")
            throw new InvalidDataException("最終応答の受付番号またはエラーを確認してください。");
        var units = xml.Root!.Element("MessageBody")?.Elements("BulkRegistrationUnit").ToArray() ?? [];
        // This implementation submits exactly one patient per request. Never pick the first of several results.
        if (units.Length != 1) throw new InvalidDataException("最終結果が未確定、または件数が一致しません。応答XMLを確認してください。");
        var info = units[0].Element("ReferenceNumberRegistrationInfo") ?? throw new InvalidDataException("結果の患者情報がありません。");
        var target = new ReferenceRegistrationTarget(Value(info, "ReferenceNumber"), Value(info, "InsurerNumber"),
            Value(info, "InsuredCardSymbol"), Value(info, "InsuredIdentificationNumber"), Value(info, "InsuredBranchNumber"));
        if (Fingerprint(target) != entry.InsuranceFingerprint)
            throw new InvalidDataException("結果の患者・保険情報が要求と一致しません。");
        string status = Value(units[0], "ProcessingResultStatus");
        if (status is not ("1" or "2")) throw new InvalidDataException("未知の処理結果です。成功として扱いません。");
        return new RegistrationOutcome(status == "1", Value(units[0], "ProcessingResultCode"));
    }
    private static string Value(XElement parent, string name)
    {
        var elements = parent.Elements(name).ToArray();
        if (elements.Length > 1) throw new InvalidDataException("応答項目が重複しています。");
        return elements.SingleOrDefault()?.Value ?? "";
    }
}
