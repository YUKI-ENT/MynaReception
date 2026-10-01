using System.Data;
using System.Globalization;
using ReceptionAgent.Dynamics;
using ReceptionAgent.Face;

namespace ReceptionAgent;

internal static class DynamicsFacePatientFinder
{
    public static async Task<FaceLookupResult> FindAsync(FaceIdentity identity, CancellationToken token)
    {
        if (FaceReferencePatient.Resolve(identity) is { } referencePatient) return referencePatient;
        return await SearchAsync(identity, false, token);
    }
    public static Task<FaceLookupResult> VerifyAsync(FaceIdentity identity, CancellationToken token) => SearchAsync(identity, true, token);
    private static async Task<FaceLookupResult> SearchAsync(FaceIdentity identity, bool verifyInsurance, CancellationToken token)
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
            if (PatientNameMatcher.NormalizeKanaForMatching(kana) != PatientNameMatcher.NormalizeKanaForMatching(identity.NameKana)) continue;
            int Number(string key) => int.TryParse(Value(key), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                ? value : throw new InvalidDataException("Dynamicsの生年月日項目を読み取れません。");
            DateOnly date = DynamicsBirthdate.Parse(Value("年号"), Number("生年"), Number("月"), Number("日"));
            candidates.Add(new(Value("カルテ番号"), Value("氏名"), kana, date));
        }
        var matches = PatientNameMatcher.Match(identity, candidates);
        if (verifyInsurance)
        {
            var insurance = new List<FaceInsuranceCandidate>();
            var keys = matches.SelectMany(m => m.RawChartNumbers).Distinct().ToArray();
            if (keys.Length > 0 && PatientInsuranceMatcher.IsUsable(identity.Insurance))
            {
                if (keys.Length > 100) throw new InvalidDataException("再検証候補が多すぎます。");
                foreach (var key in keys) _ = DynamicsChartNumber.ToPatientId(key);
                string condition = string.Join(",", keys.Select(k => "'" + k + "'"));
                using var policies = await DynamicsComReader.ReadAsync("SELECT [カルテ番号], [保険者番号], [記号], [番号], [枝番] FROM [患者保険マスター] WHERE CStr([カルテ番号]) IN (" + condition + ")", token);
                foreach (DataRow row in policies.Rows)
                {
                    string Value(string key) => row[key] is DBNull ? "" : Convert.ToString(row[key], CultureInfo.InvariantCulture)?.Trim() ?? "";
                    insurance.Add(new(Value("カルテ番号"), new(Value("保険者番号"), Value("記号"), Value("番号"), Value("枝番"))));
                }
            }
            return PatientInsuranceMatcher.Verify(matches, identity.Insurance, insurance);
        }
        return new(matches, matches.Count == 1 ? matches[0] : null, false,
            matches.Count == 0 ? "PATIENT_NOT_FOUND" : matches.Count > 1 ? "PATIENT_AMBIGUOUS" : "氏名カナ（小書き文字の表記揺れを吸収）・生年月日で一意に一致");
    }
}
