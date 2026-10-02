using System.Data;
using System.Globalization;
using ReceptionAgent.Face;

namespace ReceptionAgent;

internal static class DynamicsQualificationResultFinder
{
    public static async Task<FaceLookupResult> FindAsync(FaceIdentity face, CancellationToken token)
    {
        if (await DynamicsPatientProvider.ReadInstitutionCodeAsync(token) != face.InstitutionCode)
            throw new InvalidDataException("face XMLとDynamicsの医療機関コードが異なります。");
        // DISTINCT collapses accumulated duplicate qualification responses before the DAO 100-row limit.
        // CStr accommodates text/numeric yyyyMMdd fields without guessing a Date/Time conversion.
        string date = face.Birthdate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        using var table = await DynamicsComReader.ReadAsync(
            "SELECT DISTINCT [氏名カナ], [生年月日], [カルテ番号] FROM [WKO資格確認結果表示] WHERE CStr([生年月日] & '')='" + date + "'", token);
        var rows = table.Rows.Cast<DataRow>().Select(row =>
        {
            string Value(string field) => row[field] is DBNull ? "" : Convert.ToString(row[field], CultureInfo.InvariantCulture)?.Trim() ?? "";
            return new QualificationResultRow(Value("氏名カナ"), Value("生年月日"), Value("カルテ番号"));
        });
        return QualificationResultMatcher.Match(face, rows);
    }
}
