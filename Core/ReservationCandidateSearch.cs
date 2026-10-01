using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace iCallManager.Core;

/// <summary>Read-only suggestions. A matching birthday never establishes patient identity.</summary>
public static class ReservationCandidateSearch
{
    private static readonly Regex NameAndDate = new(
        @"\A(?<name>.+?)\s*\(\s*(?<era>明治|大正|昭和|平成|令和|西暦)?\s*(?<year>[0-9]{1,4}|元)年\s*(?<month>[0-9]{1,2})月\s*(?<day>[0-9]{1,2})日\s*\)\s*\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static void Validate(BridgeRequest request)
    {
        if (!DateOnly.TryParseExact(request.Birthdate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var birth) || birth > DateOnly.FromDateTime(DateTime.Today))
            throw new BridgeException("invalid_request", "birthdateは実在する過去または当日の日付をyyyy-MM-ddで指定してください。");
        var names = new[] { request.PatientName, request.NameKana, request.GivenName };
        if (names.Any(n => n is not null && (n.Length > 100 || NormalizeName(n).Length == 0)) ||
            !names.Any(n => !string.IsNullOrWhiteSpace(n)))
            throw new BridgeException("invalid_request", "patientName / nameKana / givenNameのいずれかを100文字以内で指定してください。");
    }

    public static OperationResult Search(BridgeRequest request, IReadOnlyList<Reservation> rows)
    {
        Validate(request);
        var birth = DateOnly.ParseExact(request.Birthdate!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fullNames = new[] { request.PatientName, request.NameKana }.Where(n => n is not null)
            .Select(n => NormalizeName(n!)).ToArray();
        string given = NormalizeName(request.GivenName ?? "");
        var candidates = new List<ReservationCandidate>();
        foreach (var row in rows)
        {
            if (!TryParseNameAndBirthdate(row.PatientName, out var name, out var date) || date != birth) continue;
            string normalized = NormalizeName(name);
            // No guessed kanji readings or surname segmentation. Explicit givenName is only a suffix hint.
            string match = fullNames.Contains(normalized) ? "full_name" :
                given.Length > 0 && normalized.EndsWith(given, StringComparison.Ordinal) ? "given_name_suffix" : "birthdate_only";
            candidates.Add(new(row.ReceptionNo, string.IsNullOrWhiteSpace(row.PatientId) || row.PatientId == "-" ? null : row.PatientId,
                row.PatientName, name, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), match));
        }
        var sorted = candidates.OrderBy(c => c.NameMatch switch { "full_name" => 0, "given_name_suffix" => 1, _ => 2 }).ToArray();
        return new(true, sorted.Length == 0 ? "no_candidates" : "candidates_found",
            "現在の一覧の氏名欄に生年月日を含む予約のみ検索しました。候補は本人確認が必要です。該当なしでも予約なし・初診とは断定できません。")
        { Candidates = sorted };
    }

    public static string NormalizeName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        return new string(normalized.Where(c => !char.IsWhiteSpace(c))
            .Select(c => c is >= '\u3041' and <= '\u3096' ? (char)(c + 0x60) : c).ToArray());
    }

    public static bool TryParseNameAndBirthdate(string value, out string name, out DateOnly birth)
    {
        name = ""; birth = default;
        if (value.Length > 512) return false;
        var match = NameAndDate.Match(value.Normalize(NormalizationForm.FormKC));
        if (!match.Success) return false;
        string era = match.Groups["era"].Value, yearText = match.Groups["year"].Value;
        int year = yearText == "元" ? 1 : int.Parse(yearText, CultureInfo.InvariantCulture);
        var range = era switch
        {
            "明治" => (1867, new DateOnly(1868, 9, 8), new DateOnly(1912, 7, 29)),
            "大正" => (1911, new DateOnly(1912, 7, 30), new DateOnly(1926, 12, 24)),
            "昭和" => (1925, new DateOnly(1926, 12, 25), new DateOnly(1989, 1, 7)),
            "平成" => (1988, new DateOnly(1989, 1, 8), new DateOnly(2019, 4, 30)),
            "令和" => (2018, new DateOnly(2019, 5, 1), DateOnly.MaxValue),
            _ => (0, DateOnly.MinValue, DateOnly.MaxValue)
        };
        if (year == 0 || yearText == "元" && range.Item1 == 0 || range.Item1 == 0 && yearText.Length != 4) return false;
        try
        {
            birth = new DateOnly(year + range.Item1, int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException) { return false; }
        if (birth < range.Item2 || birth > range.Item3 || birth > DateOnly.FromDateTime(DateTime.Today)) return false;
        name = match.Groups["name"].Value.Trim();
        return NormalizeName(name).Length > 0;
    }
}
