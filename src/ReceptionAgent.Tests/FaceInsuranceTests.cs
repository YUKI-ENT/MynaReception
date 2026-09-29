using System.Text;
using ReceptionAgent.Face;

internal static class FaceInsuranceTests
{
    public static void Run(Action<bool, string> check)
    {
        var birth = new DateOnly(2000, 1, 2);
        var first = new FacePatientMatch("11", "試験", "テスト", birth, ["110", "117"]);
        var second = new FacePatientMatch("22", "試験", "テスト", birth, ["220"]);
        FacePatientMatch[] candidates = [first, second];
        var info = new FaceInsurance("01234567", "記号01", "000123", "02");
        var matched = PatientInsuranceMatcher.Resolve(candidates, info, [new("110", info with { Number = "old" }), new("117", info), new("220", info with { Branch = "03" })]);
        check(matched.Selected?.PatientId == "11" && matched.Selected.RawChartNumbers.SequenceEqual(["117"]) && matched.InsuranceChecked, "duplicate name/date resolved using all four insurance fields and matching chart branch");
        check(PatientInsuranceMatcher.Resolve(candidates, info, [new("220", info)]).Selected?.PatientId == "22", "insurance can select second candidate instead of first");
        check(PatientInsuranceMatcher.Resolve(candidates, info, [new("110", info), new("117", info)]).Selected?.PatientId == "11", "multiple matching insurance rows within one patient remain one identity");
        var ambiguous = PatientInsuranceMatcher.Resolve(candidates, info, [new("110", info), new("220", info)]);
        check(ambiguous.Selected is null && ambiguous.Candidates.Count == 2, "two patients with same insurance remain unresolved with original candidates");
        check(PatientInsuranceMatcher.Resolve(candidates, info, []).Selected is null, "changed or absent Dynamics insurance does not choose a patient");
        foreach (var missing in new FaceInsurance?[] { null, info with { InsurerNumber = "" }, info with { Number = null }, info with { Branch = "" } })
            check(PatientInsuranceMatcher.Resolve(candidates, missing, [new("110", info)]).Selected is null, "missing required XML insurance does not partially match");
        foreach (var mismatch in new[] { info with { InsurerNumber = "11234567" }, info with { Symbol = "記号02" }, info with { Number = "123" }, info with { Branch = "01" } })
            check(PatientInsuranceMatcher.Resolve(candidates, info, [new("110", mismatch)]).Selected is null, "every insurance field is required, including leading zeros and branch");
        check(PatientInsuranceMatcher.Resolve(candidates, info, [new("110", info with { Branch = "03" }), new("117", info with { Number = "other" })]).Selected is null, "fields from separate insurance rows are never combined");
        check(PatientInsuranceMatcher.Resolve(candidates, info, [new("330", info)]).Selected is null, "insurance outside name/date candidates cannot select a patient");
        var normalized = info with { InsurerNumber = "０１２３４５６７", Symbol = " 記号０１ ", Branch = "2" };
        check(PatientInsuranceMatcher.Resolve(candidates, info, [new("117", normalized)]).Selected?.PatientId == "11", "width and outer spaces normalized, known numeric branch formatted to two digits");
        check(PatientInsuranceMatcher.Resolve(candidates, info with { Symbol = "" }, [new("110", info with { Symbol = null })]).Selected?.PatientId == "11", "legitimately absent card symbol can match empty database symbol");
        var unique = PatientInsuranceMatcher.Resolve([first], null, []);
        check(unique.Selected == first && !unique.InsuranceChecked, "unique name/date preserves existing behavior without insurance lookup");
        string xml = "<XmlMsg><MessageHeader><SegmentOfResult>1</SegmentOfResult><MedicalInstitutionCode>0110012345</MedicalInstitutionCode></MessageHeader><MessageBody><ProcessingResultStatus>1</ProcessingResultStatus><ResultList><ResultOfQualificationConfirmation><NameKana>テスト</NameKana><Birthdate>20000102</Birthdate><InsurerNumber>01234567</InsurerNumber><InsuredCardSymbol>記号01</InsuredCardSymbol><InsuredIdentificationNumber>000123</InsuredIdentificationNumber><InsuredBranchNumber>02</InsuredBranchNumber></ResultOfQualificationConfirmation></ResultList></MessageBody></XmlMsg>";
        var parsed = FaceXmlParser.Parse(Encoding.UTF8.GetBytes(xml), FaceXmlEncoding.Utf8);
        check(parsed.Insurance == info, "face parser extracts patient insurance quartet without losing leading zeros");
        check(PatientInsuranceMatcher.Resolve(candidates, parsed.Insurance, [new("117", info)]).Selected?.PatientId == "11", "parsed XML insurance feeds patient disambiguation");
        try { FaceXmlParser.Parse(Encoding.UTF8.GetBytes(xml.Replace("</InsurerNumber>", "</InsurerNumber><InsurerNumber>99999999</InsurerNumber>")), FaceXmlEncoding.Utf8); check(false, "duplicate field"); }
        catch (InvalidDataException) { check(true, "duplicate insurance XML field is rejected"); }
    }
}
