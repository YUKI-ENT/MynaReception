using System.Globalization;

namespace ReceptionAgent.Dynamics;

public static class DynamicsBirthdate
{
    public static int JapaneseYear(DateOnly birthdate)
    {
        try { return new JapaneseCalendar().GetYear(birthdate.ToDateTime(TimeOnly.MinValue)); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("検索対象の生年月日が和暦変換の対応範囲外です。"); }
    }
    public static DateOnly Parse(string era, int year, int month, int day)
    {
        try
        {
            if (era.Trim() == "西暦") return new DateOnly(year, month, day);
            var calendar = new JapaneseCalendar();
            var culture = new CultureInfo("ja-JP"); culture.DateTimeFormat.Calendar = calendar;
            int id = calendar.Eras.SingleOrDefault(e => culture.DateTimeFormat.GetEraName(e) == era.Trim());
            if (id == 0) throw new InvalidDataException("Dynamicsの年号の対応が未確認です。年号が数値コードの場合は対応表が必要です。");
            DateTime date = calendar.ToDateTime(year, month, day, 0, 0, 0, 0, id);
            if (calendar.GetEra(date) != id || calendar.GetYear(date) != year) throw new InvalidDataException("Dynamicsの生年月日が元号の期間と一致しません。");
            return DateOnly.FromDateTime(date);
        }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Dynamicsの生年月日が不正です。", ex); }
    }
}
