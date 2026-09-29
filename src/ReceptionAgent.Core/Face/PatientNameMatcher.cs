using System.Text;
using ReceptionAgent.Dynamics;

namespace ReceptionAgent.Face;

public sealed record FacePatientCandidate(string RawChartNo, string Name, string NameKana, DateOnly Birthdate);
public sealed record FacePatientMatch(string PatientId, string Name, string NameKana, DateOnly Birthdate, string[] RawChartNumbers);

public static class PatientNameMatcher
{
    public static string NormalizeKana(string text) => new(text.Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c)).Select(c => c is >= '\u3041' and <= '\u3096' ? (char)(c + 0x60) : c).ToArray());

    public static IReadOnlyList<FacePatientMatch> Match(FaceIdentity identity, IEnumerable<FacePatientCandidate> candidates)
    {
        string kana = NormalizeKana(identity.NameKana);
        if (kana.Length == 0) throw new InvalidDataException("フリガナが空欄です。");
        return candidates.Where(c => c.Birthdate == identity.Birthdate && NormalizeKana(c.NameKana) == kana)
            .GroupBy(c => DynamicsChartNumber.ToPatientId(c.RawChartNo))
            .Select(g => new FacePatientMatch(g.Key, g.First().Name, g.First().NameKana, identity.Birthdate,
                g.Select(c => c.RawChartNo).Distinct().Order().ToArray())).OrderBy(c => c.PatientId).ToArray();
    }
}
