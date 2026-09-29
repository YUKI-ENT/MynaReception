using System.Data;
using System.Globalization;
using ReceptionAgent.Dynamics;

namespace ReceptionAgent;

internal sealed class DynamicsPatientProvider(string branchField, bool removeLastDigit, string expectedInstitutionCode) : IDynamicsProvider
{
    public static async Task<string> ReadInstitutionCodeAsync(CancellationToken token)
    {
        using var info = await DynamicsComReader.ReadAsync("SELECT [県番], [点数表], [医療機関コード] FROM [医院情報]", token);
        if (info.Rows.Count != 1) throw new InvalidDataException("医院情報を1件に特定できません。Dynamicsの医院設定を確認してください。");
        var row = info.Rows[0];
        return DynamicsInstitutionCode.Build(row["県番"], row["点数表"], row["医療機関コード"]);
    }
    public static async Task<string> DiagnoseAsync()
    {
        using var patient = await DynamicsComReader.ReadAsync("SELECT * FROM [患者マスター] WHERE 1=0", CancellationToken.None);
        using var insurance = await DynamicsComReader.ReadAsync("SELECT * FROM [患者保険マスター] WHERE 1=0", CancellationToken.None);
        string Fields(DataTable t) => string.Join("\r\n", t.Columns.Cast<DataColumn>().Select(c => $"  {c.ColumnName}  (DAO型 {c.ExtendedProperties["DaoType"]})"));
        return "Dynamics COM接続：成功\r\n患者マスターフォーム：開いています\r\n患者データの読み取り：0件（項目定義のみ）\r\n\r\nフォームRecordSource:\r\n" + patient.ExtendedProperties["RecordSource"] +
            "\r\n\r\n患者マスターの項目:\r\n" + Fields(patient) + "\r\n\r\n患者保険マスターの項目:\r\n" + Fields(insurance);
    }
    public async Task ValidateAsync(CancellationToken token)
    {
        if (await ReadInstitutionCodeAsync(token) != expectedInstitutionCode)
            throw new InvalidOperationException("Dynamicsの医療機関コードが保存範囲と異なります。接続先を確認してください。");
        if (branchField != "枝番" || !removeLastDigit)
            throw new InvalidOperationException("旧履歴の項目対応が確定仕様と異なります。未処理の範囲は再保存し、処理済み履歴は管理者が確認してください。");
        using var schema = await DynamicsComReader.ReadAsync("SELECT * FROM [患者保険マスター] WHERE 1=0", token);
        foreach (string field in new[] { "カルテ番号", "保険者番号", "記号", "番号", branchField })
            if (!schema.Columns.Contains(field)) throw new InvalidOperationException($"患者保険マスターに [{field}] がありません。COM診断で項目を確認してください。");
        using var patient = await DynamicsComReader.ReadAsync("SELECT [カルテ番号] FROM [患者マスター] WHERE 1=0", token);
    }
    public async Task<DynamicsPatient?> GetPatientAsync(string patientId, CancellationToken cancellationToken = default)
    {
        if (patientId.Length == 0 || patientId.Any(c => c is < '0' or > '9')) throw new ArgumentException("患者番号が不正です。");
        await ValidateAsync(cancellationToken);
        // CStr accommodates both numeric and text chart columns. Values come only from validated digits.
        string[] keys = Enumerable.Range(0, 10).Select(n => "'" + patientId + n.ToString(CultureInfo.InvariantCulture) + "'").ToArray();
        string condition = "CStr(P.[カルテ番号]) IN (" + string.Join(",", keys) + ")";
        using var patients = await DynamicsComReader.ReadAsync("SELECT P.[カルテ番号] FROM [患者マスター] AS P WHERE " + condition, cancellationToken);
        if (patients.Rows.Count == 0) return null;
        string sql = "SELECT P.[カルテ番号] AS RawChartNo, H.[保険者番号] AS Insurer, H.[記号] AS CardSymbol, H.[番号] AS CardNumber, H.[枝番] AS CardBranch " +
            "FROM [患者マスター] AS P INNER JOIN [患者保険マスター] AS H ON P.[カルテ番号]=H.[カルテ番号] WHERE " + condition;
        using var rows = await DynamicsComReader.ReadAsync(sql, cancellationToken);
        var candidates = new List<DynamicsPatient>();
        foreach (DataRow row in rows.Rows)
        {
            string Value(string key) => row[key] == DBNull.Value ? "" : Convert.ToString(row[key], CultureInfo.InvariantCulture)?.Trim() ?? "";
            string raw = Value("RawChartNo");
            string id = DynamicsChartNumber.ToPatientId(raw);
            if (id != patientId) throw new DynamicsPatientDataException("PATIENT_ID_MISMATCH");
            if (Value("Insurer").Length == 0) continue;
            string branch = Value("CardBranch");
            // Numeric zero is a known value; DBNull/empty must never become 00.
            if (row["CardBranch"] is not string && branch.Length is > 0 and < 2) branch = branch.PadLeft(2, '0');
            candidates.Add(new(new(raw, id), new(Value("Insurer"), Value("CardSymbol"), Value("CardNumber"), branch, false)));
        }
        var distinct = candidates.DistinctBy(c => c.Insurance).ToArray();
        if (distinct.Length == 0) throw new DynamicsPatientDataException("NO_HEALTH_INSURANCE");
        if (distinct.Length != 1) throw new DynamicsPatientDataException("AMBIGUOUS_INSURANCE");
        return distinct[0];
    }
}
