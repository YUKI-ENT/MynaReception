using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ReceptionAgent.Oqs.ReferenceNumber;

public sealed class ReferenceNumberRequestBuilder
{
    private static readonly Encoding ShiftJis = CreateEncoding();
    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static string CreateIdentifier() => DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];

    public byte[] Build(string medicalInstitutionCode, string arbitraryFileIdentifier, ReferenceRegistrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.IsPublicAssistance)
            throw new NotSupportedException("公費の要求XML形式は未確認です。健康保険情報を指定してください。");
        RequireDigits(medicalInstitutionCode, "医療機関コード");
        RequireDigits(target.PatientId, "枝番なし患者ID");
        RequireDigits(target.InsurerNumber, "保険者番号");
        if (string.IsNullOrWhiteSpace(target.InsuredIdentificationNumber))
            throw new ArgumentException("被保険者証番号を入力してください。");
        RequireDigits(target.InsuredBranchNumber, "枝番");
        if (medicalInstitutionCode.Length != 10 || target.PatientId.Length > 50 || target.InsurerNumber.Length > 8 ||
            target.InsuredCardSymbol.Length > 20 || target.InsuredIdentificationNumber.Length > 20 || target.InsuredBranchNumber.Length != 2)
            throw new ArgumentException("桁数を確認してください（医療機関10桁、患者ID50桁以内、保険者8桁以内、記号・番号20文字以内、枝番2桁）。");
        if (string.IsNullOrWhiteSpace(arbitraryFileIdentifier))
            throw new ArgumentException("任意ファイル識別子を指定してください。");
        if (arbitraryFileIdentifier.Length > 50) throw new ArgumentException("任意ファイル識別子は50文字以内です。");
        // XmlWriter otherwise escapes unrepresentable characters as numeric entities.
        // Reject them before writing so operators can check unsupported insurance symbols.
        foreach (string value in new[] { medicalInstitutionCode, arbitraryFileIdentifier, target.ReferenceNumber,
            target.InsurerNumber, target.InsuredCardSymbol, target.InsuredIdentificationNumber, target.InsuredBranchNumber })
            ShiftJis.GetByteCount(value);
        using var output = new MemoryStream();
        using (var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = ShiftJis, Indent = true, CloseOutput = false }))
        {
            xml.WriteStartDocument(false);
            xml.WriteStartElement("XmlMsg");
            xml.WriteStartElement("MessageHeader");
            xml.WriteElementString("MedicalInstitutionCode", medicalInstitutionCode);
            xml.WriteElementString("ArbitraryFileIdentifier", arbitraryFileIdentifier);
            xml.WriteEndElement();
            xml.WriteStartElement("MessageBody");
            xml.WriteStartElement("ReferenceNumberRegistrationInfo");
            xml.WriteElementString("ReferenceNumber", target.ReferenceNumber);
            xml.WriteElementString("InsurerNumber", target.InsurerNumber.PadLeft(8, ' '));
            xml.WriteElementString("InsuredCardSymbol", target.InsuredCardSymbol);
            xml.WriteElementString("InsuredIdentificationNumber", target.InsuredIdentificationNumber);
            xml.WriteElementString("InsuredBranchNumber", target.InsuredBranchNumber);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return output.ToArray();
    }

    public static string Preview(byte[] xml) => ShiftJis.GetString(xml);
    private static void RequireDigits(string value, string label)
    {
        if (string.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"\A[0-9]+\z"))
            throw new ArgumentException(label + "を半角数字で入力してください。先頭の0は保持されます。");
    }
}
