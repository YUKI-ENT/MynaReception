using ReceptionAgent.Kiosk;
using System.Text.RegularExpressions;

namespace ReceptionAgent;

public sealed class PosPrintOptions
{
    public bool Enabled { get; set; }
    public string PrinterName { get; set; } = "POS-80C";
    public int PaperWidthMm { get; set; } = 80;
    public string ClinicName { get; set; } = "医院名";
    public string PatientTemplate { get; set; } = "{医院名}\n{日付時刻}\n予約番号\n{予約番号}\n{名前} 様\n{フリガナ}\n診察券番号　{診察券番号}";
    public string StaffTemplate { get; set; } = "スタッフにお渡し下さい\n番号\n{番号}\n{日付時刻}\n{名前} 様\n{フリガナ}\n診察券番号　{診察券番号}";
    public Dictionary<int, PosLineStyle> PatientStyles { get; set; } = [];
    public Dictionary<int, PosLineStyle> StaffStyles { get; set; } = [];
    public bool MatchStaffFontsToPatient { get; set; } = true;

    public PosLineStyle StyleFor(bool staff, int index, string line)
    {
        var styles = staff ? StaffStyles : PatientStyles;
        bool number = line.Contains("{番号}") || line.Contains("{予約番号}");
        var result = styles.TryGetValue(index, out var saved) ? saved.Copy() :
            new PosLineStyle { FontSizePt = number ? 20 : PaperWidthMm == 58 ? 10 : 11, Bold = number };
        result.Boxed ??= staff && line.Trim() == "スタッフにお渡し下さい";
        if (staff && MatchStaffFontsToPatient)
        {
            string Key(string text) => text.Replace("{番号}", "{予約番号}").Replace("{マイナ氏名}", "{名前}").Replace("{マイナフリガナ}", "{フリガナ}").Replace("予約番号", "番号").Trim();
            var lines = PatientTemplate.Replace("\r\n", "\n").Split('\n');
            int match = Array.FindIndex(lines, text => Key(text) == Key(line));
            if (match < 0)
            {
                string? token = Token.Matches(Key(line)).Select(m => m.Value).FirstOrDefault();
                if (token is not null) match = Array.FindIndex(lines, text => Key(text).Contains(token));
            }
            if (match < 0 && !number) match = Array.FindIndex(lines, text => !string.IsNullOrWhiteSpace(text) && !text.Contains("{番号}") && !text.Contains("{予約番号}"));
            if (match >= 0)
            {
                var patient = StyleFor(false, match, lines[match]);
                result.FontName = patient.FontName; result.FontSizePt = patient.FontSizePt;
            }
        }
        return result;
    }
    private static readonly Regex Token = new(@"\{[^{}]*\}");
    private static readonly string[] Tokens = ["{医院名}", "{日付時刻}", "{予約番号}", "{番号}", "{名前}", "{フリガナ}", "{マイナ氏名}", "{マイナフリガナ}", "{診察券番号}"];

    public void Validate()
    {
        if (PaperWidthMm is not (58 or 80)) throw new ArgumentException("POS用紙幅は58mmまたは80mmを選択してください。");
        if (Enabled && string.IsNullOrWhiteSpace(PrinterName)) throw new ArgumentException("POSプリンタを指定してください。");
        foreach (var entry in PatientStyles.Concat(StaffStyles))
        {
            if (entry.Key < 0) throw new ArgumentException("印刷行番号が不正です。");
            entry.Value.Validate();
        }
        foreach (string template in new[] { PatientTemplate, StaffTemplate })
        {
            if (string.IsNullOrWhiteSpace(template) || template.Length > 2000) throw new ArgumentException("印刷文面は1～2000文字で指定してください。");
            foreach (Match match in Token.Matches(template))
                if (!Tokens.Contains(match.Value)) throw new ArgumentException("未対応の差し込み項目: " + match.Value);
        }
    }

    public string Render(string template, KioskReceipt receipt) => Token.Replace(template, match => match.Value switch
    {
        "{医院名}" => ClinicName,
        "{日付時刻}" => receipt.ReceivedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),
        "{予約番号}" or "{番号}" => receipt.ReceptionNo,
        "{名前}" => receipt.Name,
        "{フリガナ}" => receipt.NameKana,
        "{マイナ氏名}" => !string.IsNullOrWhiteSpace(receipt.XmlName) ? receipt.XmlName : throw new InvalidOperationException("マイナXMLの漢字氏名が取得できません。印刷項目を確認してください。"),
        "{マイナフリガナ}" => !string.IsNullOrWhiteSpace(receipt.XmlNameKana) ? receipt.XmlNameKana : throw new InvalidOperationException("マイナXMLのフリガナが取得できません。印刷項目を確認してください。"),
        "{診察券番号}" => receipt.PatientId,
        _ => throw new ArgumentException("未対応の差し込み項目: " + match.Value)
    });
}

public sealed class PosLineStyle
{
    // null preserves the automatic border for the default handoff message in older settings.
    public bool? Boxed { get; set; }
    public PosLineStyle Copy() => (PosLineStyle)MemberwiseClone();
    public PosTextAlignment Alignment { get; set; } = PosTextAlignment.Center;
    public string FontName { get; set; } = "Yu Gothic";
    public float FontSizePt { get; set; } = 11;
    public bool Bold { get; set; }
    // Space after this item; wrapped lines inside the item use the font's natural line height.
    public float SpaceAfterMm { get; set; } = 1.27f;
    public void Validate()
    {
        if (!Enum.IsDefined(Alignment)) throw new ArgumentException("文字の配置は左寄せ・中央寄せ・右寄せから選択してください。");
        if (string.IsNullOrWhiteSpace(FontName)) throw new ArgumentException("フォントを選択してください。");
        if (!float.IsFinite(FontSizePt) || FontSizePt is < 6 or > 48) throw new ArgumentException("文字サイズは6～48ptで指定してください。");
        if (!float.IsFinite(SpaceAfterMm) || SpaceAfterMm is < 0 or > 20) throw new ArgumentException("行の後の余白は0～20mmで指定してください。");
    }
}

public enum PosTextAlignment { Center, Left, Right }
