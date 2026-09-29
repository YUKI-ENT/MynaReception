using System.Globalization;

namespace ReceptionAgent.Dynamics;

public static class DynamicsInstitutionCode
{
    public static string Build(object? prefecture, object? feeTable, object? institution) =>
        Part(prefecture, 2, "県番") + Part(feeTable, 1, "点数表") + Part(institution, 7, "医療機関コード");

    private static string Part(object? value, int width, string label)
    {
        string text = value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (value is decimal number && number == decimal.Truncate(number)) text = number.ToString("0", CultureInfo.InvariantCulture);
        if (text.Length == 0 || text.Length > width || text.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException($"医院情報の{label}を確認してください（半角数字{width}桁以内）。");
        return text.PadLeft(width, '0');
    }
}
