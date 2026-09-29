using System.Data;
using System.Globalization;
using ReceptionAgent.Dynamics;
using ReceptionAgent.Face;

namespace ReceptionAgent;

internal static class DynamicsFacePatientFinder
{
    public static async Task<FaceLookupResult> FindAsync(FaceIdentity identity, CancellationToken token)
    {
        if (await DynamicsPatientProvider.ReadInstitutionCodeAsync(token) != identity.InstitutionCode)
            throw new InvalidDataException("face XMLとDynamicsの医療機関コードが異なります。");
        var birth = identity.Birthdate;
        // Numeric literals only. Kana comparison stays in managed code so width and spacing are handled consistently.
        string sql = FormattableString.Invariant($"SELECT [カルテ番号], [氏名], [フリガナ], [年号], [生年], [月], [日] FROM [患者マスター] WHERE [月]={birth.Month} AND [日]={birth.Day} AND [生年] IN ({birth.Year},{DynamicsBirthdate.JapaneseYear(birth)})");
        using var rows = await DynamicsComReader.ReadAsync(sql, token);
        var candidates = new List<FacePatientCandidate>();
        foreach (DataRow row in rows.Rows)
        {
            string Value(string key) => row[key] is DBNull ? "" : Convert.ToString(row[key], CultureInfo.InvariantCulture)?.Trim() ?? "";
            string kana = Value("フリガナ");
            if (PatientNameMatcher.NormalizeKana(kana) != PatientNameMatcher.NormalizeKana(identity.NameKana)) continue;
            int Number(string key) => int.TryParse(Value(key), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                ? value : throw new InvalidDataException("Dynamicsの生年月日項目を読み取れません。");
            DateOnly date = DynamicsBirthdate.Parse(Value("年号"), Number("生年"), Number("月"), Number("日"));
            candidates.Add(new(Value("カルテ番号"), Value("氏名"), kana, date));
        }
        var matches = PatientNameMatcher.Match(identity, candidates);
        if (matches.Count < 2 || !PatientInsuranceMatcher.IsUsable(identity.Insurance))
            return PatientInsuranceMatcher.Resolve(matches, identity.Insurance, []);
        var insuranceCandidates = new List<FaceInsuranceCandidate>();
        foreach (var match in matches)
        {
            token.ThrowIfCancellationRequested();
            if (match.RawChartNumbers.Any(n => n.Length == 0 || n.Any(c => c is < '0' or > '9')))
                throw new InvalidDataException("候補のカルテ番号が不正です。");
            string keys = string.Join(",", match.RawChartNumbers.Select(n => "'" + n + "'"));
            using var insurance = await DynamicsComReader.ReadAsync("SELECT [カルテ番号], [保険者番号], [記号], [番号], [枝番] FROM [患者保険マスター] WHERE CStr([カルテ番号]) IN (" + keys + ")", token);
            foreach (DataRow row in insurance.Rows)
            {
                string Value(string key) => row[key] is DBNull ? "" : Convert.ToString(row[key], CultureInfo.InvariantCulture)?.Trim() ?? "";
                insuranceCandidates.Add(new(Value("カルテ番号"), new(Value("保険者番号"), Value("記号"), Value("番号"), Value("枝番"))));
            }
        }
        return PatientInsuranceMatcher.Resolve(matches, identity.Insurance, insuranceCandidates);
    }
}
