using System.Text;

namespace ReceptionAgent.Face;

public sealed record FaceInsurance(string? InsurerNumber, string? Symbol, string? Number, string? Branch);
public sealed record FaceInsuranceCandidate(string RawChartNo, FaceInsurance Insurance);
public enum PatientVerificationSource { NameAndInsurance, QualificationResults }
public sealed record FaceLookupResult(IReadOnlyList<FacePatientMatch> Candidates, FacePatientMatch? Selected, bool InsuranceChecked, string Message)
{
    public PatientVerificationSource VerificationSource { get; init; }
}

public static class PatientInsuranceMatcher
{
    public static FaceLookupResult Verify(IReadOnlyList<FacePatientMatch> candidates, FaceInsurance? xmlInsurance, IEnumerable<FaceInsuranceCandidate> insuranceRows)
    {
        if (!IsUsable(xmlInsurance)) return new(candidates, null, true, "再検証待ち：XMLの保険情報が不足しています。");
        var expected = Normalize(xmlInsurance!);
        var charts = insuranceRows.Where(r => IsUsable(r.Insurance) && Normalize(r.Insurance) == expected)
            .Select(r => r.RawChartNo).ToHashSet(StringComparer.Ordinal);
        var matches = candidates.Select(c => c with { RawChartNumbers = c.RawChartNumbers.Where(charts.Contains).ToArray() })
            .Where(c => c.RawChartNumbers.Length > 0).ToArray();
        return matches.Length == 1 ? new(matches, matches[0], true, "Dynamicsの氏名カナ・生年月日・保険4項目で再検証")
            : new(candidates, null, true, matches.Length == 0 ? "再検証待ち：カルテまたは保険情報が未一致" : "要確認：保険情報が一致する患者も複数");
    }
    private static string Clean(string? value) => (value ?? "").Normalize(NormalizationForm.FormKC).Trim();
    private static FaceInsurance Normalize(FaceInsurance value)
    {
        string branch = Clean(value.Branch);
        if (branch.Length == 1 && char.IsAsciiDigit(branch[0])) branch = "0" + branch;
        return new(Clean(value.InsurerNumber), Clean(value.Symbol), Clean(value.Number), branch);
    }
    public static bool IsUsable(FaceInsurance? value)
    {
        if (value is null) return false;
        var normalized = Normalize(value);
        return normalized.InsurerNumber!.Length is > 0 and <= 8 && normalized.InsurerNumber.All(char.IsAsciiDigit) &&
            normalized.Number!.Length > 0 && normalized.Branch!.Length == 2 && normalized.Branch.All(char.IsAsciiDigit);
    }
    public static FaceLookupResult Resolve(IReadOnlyList<FacePatientMatch> candidates, FaceInsurance? xmlInsurance, IEnumerable<FaceInsuranceCandidate> insuranceRows)
    {
        if (candidates.Count < 2) return new(candidates, candidates.SingleOrDefault(), false, candidates.Count == 0 ? "氏名カナ・生年月日の一致なし" : "氏名カナ・生年月日で特定");
        if (!IsUsable(xmlInsurance)) return new(candidates, null, true, "特定できず：XMLの保険情報が不足しています。");
        var expected = Normalize(xmlInsurance!);
        // Match all four fields on one insurance row. Never combine fields from different chart branches.
        var matchingCharts = insuranceRows.Where(r => IsUsable(r.Insurance) && Normalize(r.Insurance) == expected)
            .Select(r => r.RawChartNo).ToHashSet(StringComparer.Ordinal);
        var matchingPatients = candidates.Select(c => c with { RawChartNumbers = c.RawChartNumbers.Where(matchingCharts.Contains).ToArray() })
            .Where(c => c.RawChartNumbers.Length > 0).ToArray();
        return matchingPatients.Length == 1 ? new(candidates, matchingPatients[0], true, "保険情報の4項目一致で特定")
            : new(candidates, null, true, matchingPatients.Length == 0 ? "特定できず：保険情報が一致する候補がありません。" : "特定できず：保険情報が一致する患者IDも複数あります。");
    }
}
