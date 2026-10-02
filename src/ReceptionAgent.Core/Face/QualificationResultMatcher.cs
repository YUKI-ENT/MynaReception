using System.Globalization;

namespace ReceptionAgent.Face;

public sealed record QualificationResultRow(string NameKana, string Birthdate, string RawChartNumber);

public static class QualificationResultMatcher
{
    public static FaceLookupResult Match(FaceIdentity face, IEnumerable<QualificationResultRow> rows)
    {
        var candidates = new List<FacePatientCandidate>();
        bool invalidChart = false;
        foreach (var row in rows)
        {
            if (!DateOnly.TryParseExact(row.Birthdate.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var birth) ||
                birth != face.Birthdate || PatientNameMatcher.NormalizeKanaForMatching(row.NameKana) != PatientNameMatcher.NormalizeKanaForMatching(face.NameKana)) continue;
            string chart = row.RawChartNumber.Trim();
            if (chart.Length == 0 || chart == "0") continue; // Not yet assigned.
            try { _ = Dynamics.DynamicsChartNumber.ToPatientId(chart); }
            catch (Dynamics.DynamicsPatientDataException) { invalidChart = true; continue; }
            candidates.Add(new(chart, face.PatientName ?? "", row.NameKana, birth));
        }
        var matches = PatientNameMatcher.Match(face, candidates);
        var selected = !invalidChart && matches.Count == 1 ? matches[0] : null;
        return new(matches, selected, false, invalidChart ? "要確認：WKOのカルテ番号に不正な値があります" :
            matches.Count == 0 ? "WKO確定カルテ番号待ち" : matches.Count > 1 ? "要確認：WKOの確定カルテ番号が複数患者に分かれています" :
            "WKO資格確認結果表示の確定カルテ番号で検証") { VerificationSource = PatientVerificationSource.QualificationResults };
    }
}
