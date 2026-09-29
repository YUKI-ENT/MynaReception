using System.Text;
using System.Xml;
using System.Xml.Linq;
using ReceptionAgent;
using ReceptionAgent.Dynamics;
using ReceptionAgent.Face;

internal static class FaceTests
{
    public static async Task Run(Action<bool, string> check)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string Sample(string encoding) => $"<?xml version=\"1.0\" encoding=\"{encoding}\"?><XmlMsg><MessageHeader><SegmentOfResult>1</SegmentOfResult><MedicalInstitutionCode>0110012345</MedicalInstitutionCode></MessageHeader><MessageBody><ResultList><ResultOfQualificationConfirmation><NameKana>ﾃｽﾄ ﾀﾛｳ</NameKana><InsuredName>別人の保険名義</InsuredName><Birthdate>20241021</Birthdate></ResultOfQualificationConfirmation></ResultList><ProcessingResultStatus>1</ProcessingResultStatus></MessageBody></XmlMsg>";
        byte[] utf8 = Encoding.UTF8.GetBytes(Sample("UTF-8"));
        var face = FaceXmlParser.Parse(utf8, FaceXmlEncoding.Utf8);
        check(face.NameKana == "ﾃｽﾄ ﾀﾛｳ" && face.Birthdate == new DateOnly(2024, 10, 21), "face uses patient NameKana and Birthdate, not insured person's name");
        check(FaceXmlParser.Parse(Encoding.GetEncoding(932).GetBytes(Sample("Shift_JIS")), FaceXmlEncoding.ShiftJis) == face, "Shift_JIS face identity matches UTF-8");
        check(FaceXmlParser.Parse([0xEF, 0xBB, 0xBF, .. utf8], FaceXmlEncoding.Utf8) == face, "UTF-8 BOM accepted");
        try { FaceXmlParser.Parse(Encoding.ASCII.GetBytes(Sample("Shift_JIS").Replace("ﾃｽﾄ ﾀﾛｳ", "TEST").Replace("別人の保険名義", "OTHER")), FaceXmlEncoding.Utf8); check(false, "declaration mismatch"); }
        catch (InvalidDataException) { check(true, "configured encoding must match declaration"); }
        void Invalid(string content, string description)
        {
            try { FaceXmlParser.Parse(Encoding.UTF8.GetBytes(content), FaceXmlEncoding.Utf8); check(false, description); }
            catch (Exception ex) when (ex is InvalidDataException or XmlException) { check(true, description); }
        }
        Invalid(Sample("UTF-8").Replace("20241021", "202410（カット）"), "redacted birthdate not interpreted as real patient");
        Invalid(Sample("UTF-8").Replace("20241021", "20240230"), "invalid date rejected");
        Invalid(Sample("UTF-8").Replace("<NameKana>ﾃｽﾄ ﾀﾛｳ</NameKana>", ""), "missing NameKana rejected");
        Invalid(Sample("UTF-8").Replace("<ProcessingResultStatus>1", "<ProcessingResultStatus>2"), "failed qualification response rejected");
        var duplicate = XDocument.Parse(Sample("UTF-8"));
        var list = duplicate.Root!.Element("MessageBody")!.Element("ResultList")!;
        list.Add(new XElement(list.Elements().Single()));
        Invalid(duplicate.ToString(), "multiple XML patient results never pick first");
        Invalid("<!DOCTYPE XmlMsg [<!ENTITY x SYSTEM 'file:///must-not-read'>]><XmlMsg>&x;</XmlMsg>", "face XML external entities disabled");
        check(DynamicsBirthdate.Parse("令和", 6, 10, 21) == face.Birthdate, "Japanese era fields converted to western XML birthdate");
        check(DynamicsBirthdate.Parse("昭和", 64, 1, 7) == new DateOnly(1989, 1, 7) && DynamicsBirthdate.Parse("平成", 1, 1, 8) == new DateOnly(1989, 1, 8), "era transition boundary supported");
        try { DynamicsBirthdate.Parse("平成", 1, 1, 7); check(false, "era mismatch"); }
        catch (InvalidDataException) { check(true, "date outside named era rejected"); }
        try { DynamicsBirthdate.Parse("3", 6, 10, 21); check(false, "unknown era code"); }
        catch (InvalidDataException) { check(true, "numeric era not guessed"); }
        check(PatientNameMatcher.NormalizeKana(" がく　たろう ") == PatientNameMatcher.NormalizeKana("ｶﾞｸ ﾀﾛｳ"), "kana width, voiced marks, whitespace and hiragana normalized");
        var rows = new[] { new FacePatientCandidate("110", "試験太郎", "テスト　タロウ", face.Birthdate), new FacePatientCandidate("117", "試験太郎", "ﾃｽﾄﾀﾛｳ", face.Birthdate) };
        var matches = PatientNameMatcher.Match(face, rows);
        check(matches.Count == 1 && matches[0].PatientId == "11" && matches[0].RawChartNumbers.Length == 2, "same patient's chart branches grouped as one patient");
        check(PatientNameMatcher.Match(face, [.. rows, new("220", "試験太郎", "テストタロウ", face.Birthdate)]).Count == 2, "same name and birthday with two patient IDs stays ambiguous");
        check(PatientNameMatcher.Match(face, [rows[0] with { Birthdate = new DateOnly(2024, 10, 22) }, rows[1] with { NameKana = "テストジロウ" }]).Count == 0, "both kana and birthdate must match");
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-face-tests-" + Guid.NewGuid().ToString("N"));
        new AgentSettings { FaceXmlDirectory = root, FaceEncoding = FaceXmlEncoding.ShiftJis }.Save(root);
        var loaded = AgentSettings.Load(root);
        check(loaded.FaceEncoding == FaceXmlEncoding.ShiftJis && loaded.FaceXmlDirectory == root && loaded.OqsRoot == "", "face settings persist without requiring paused Bulk OQS configuration");
        string path = Path.Combine(root, "OQSsiquc01res_face_synthetic.xml");
        File.WriteAllBytes(path, utf8);
        check(await FaceXmlParser.LoadAsync(path, FaceXmlEncoding.Utf8, CancellationToken.None) == face && File.ReadAllBytes(path).SequenceEqual(utf8), "shared face read preserves source XML");
    }
}
