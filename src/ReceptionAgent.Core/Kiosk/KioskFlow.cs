using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReceptionAgent.Kiosk;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KioskPageKind { Card, Birthday, Choice, Lookup, Guidance }
public sealed class KioskFlowChoice
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
}
public sealed class KioskFlowRoute
{
    public string Variable { get; set; } = "";
    public string Value { get; set; } = "";
    public string NextPageId { get; set; } = "";
}
public sealed class KioskFlowPage
{
    public string Id { get; set; } = "";
    public KioskPageKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string ButtonLabel { get; set; } = "次へ";
    public string Variable { get; set; } = "";
    public string NextPageId { get; set; } = "";
    public KioskNextStep NextStep { get; set; } = KioskNextStep.StaffHelp;
    public List<KioskFlowChoice> Choices { get; set; } = [];
    public List<KioskFlowRoute> Routes { get; set; } = [];
}
public sealed record KioskFlowAnswer(string PageId, string? ChoiceId = null, int? Month = null, int? Day = null);
public sealed class KioskAnswerField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
}
public sealed class KioskFlow
{
    public List<KioskAnswerField> Fields { get; set; } = [new() { Key = "saysReserved", Label = "予約の回答" },
        new() { Key = "hasFever", Label = "発熱の回答" }, new() { Key = "clinicClass", Label = "外来区分" }];
    public string FieldLabel(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Label ?? key switch
    { "month" => "誕生月", "day" => "誕生日", "category" => "受付の照会結果", "hasChart" => "カルテの有無", "actualReservation" => "実際の予約有無", _ => key };
    public string ValueLabel(string key, string value) => Pages.Where(p => p.Kind == KioskPageKind.Choice && p.Variable == key)
        .SelectMany(p => p.Choices).FirstOrDefault(c => c.Value == value)?.Label ?? value;
    public string StartTitle { get; set; } = "マイナ受付";
    public string StartMessage { get; set; } = "マイナンバーカードまたはスマートフォンをご用意ください。";
    public string StartButton { get; set; } = "マイナ受付開始";
    public string FirstPageId { get; set; } = "card";
    public List<KioskFlowPage> Pages { get; set; } =
    [
        new() { Id = "card", Kind = KioskPageKind.Card, Title = "資格確認をお願いします", Message = "顔認証カードリーダーにマイナンバーカードあるいはスマートフォンをセットし、認証してください。",
            ButtonLabel = "資格確認が終わりました・次へ", NextPageId = "birthday" },
        new() { Id = "birthday", Kind = KioskPageKind.Birthday, Title = "お誕生日の月日を教えてください", Message = "資格確認の結果と照合するために使用します。", NextPageId = "reserved" },
        new() { Id = "reserved", Kind = KioskPageKind.Choice, Title = "本日の予約はありますか？", Variable = "saysReserved", NextPageId = "fever",
            Choices = [new() { Id = "yes", Label = "予約あり", Value = "true" }, new() { Id = "no", Label = "予約なし", Value = "false" }] },
        new() { Id = "fever", Kind = KioskPageKind.Choice, Title = "本日37.5℃以上の熱はありましたか？", Variable = "hasFever", NextPageId = "lookup",
            Choices = [new() { Id = "yes", Label = "あった", Value = "true" }, new() { Id = "no", Label = "なかった", Value = "false" }],
            Routes = [new() { Variable = "hasFever", Value = "true", NextPageId = "feverHelp" }, new() { Variable = "saysReserved", Value = "false", NextPageId = "noReservation" }] },
        new() { Id = "lookup", Kind = KioskPageKind.Lookup, Title = "予約を確認しています", Message = "この画面でお待ちください。", NextPageId = "newPatient",
            Routes = [new() { Variable = "category", Value = "ReturningWithReservation", NextPageId = "reservedDone" },
                new() { Variable = "category", Value = "ReturningWithoutReservation", NextPageId = "noReservation" }] },
        new() { Id = "reservedDone", Kind = KioskPageKind.Guidance, Title = "予約を確認しました", Message = "職員の案内をお待ちください。", NextStep = KioskNextStep.Finish },
        new() { Id = "noReservation", Kind = KioskPageKind.Guidance, Title = "窓口へお越しください", Message = "職員が予約をお取りします。受付窓口へお声掛けください。" },
        new() { Id = "newPatient", Kind = KioskPageKind.Guidance, Title = "窓口で確認します", Message = "問診票や医療証などを職員が確認します。受付窓口へお越しください。" },
        new() { Id = "feverHelp", Kind = KioskPageKind.Guidance, Title = "職員へお声掛けください", Message = "発熱について職員が確認します。受付へお声掛けください。" }
    ];
    public KioskFlowPage Page(string id) => Pages.Single(p => p.Id == id);
    public string Next(KioskFlowPage page, IReadOnlyDictionary<string, string> variables) =>
        page.Routes.FirstOrDefault(r => variables.TryGetValue(r.Variable, out var value) && value == r.Value)?.NextPageId ?? page.NextPageId;
    public static bool IsSystemVariable(string name) => name is "category" or "hasChart" or "actualReservation";
    public void Validate()
    {
        bool Id(string value) => value is not null && Regex.IsMatch(value, @"\A[a-zA-Z][a-zA-Z0-9_]{0,49}\z");
        if (Fields is null || Fields.Count > 50 || Fields.Any(f => f is null || !Id(f.Key) || IsSystemVariable(f.Key) || f.Key is "month" or "day" || string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 100) ||
            Fields.Select(f => f.Key).Distinct().Count() != Fields.Count)
            throw new ArgumentException("回答の保存項目を確認してください。表示名100文字以内、保存キーは重複なしです。");
        if (string.IsNullOrWhiteSpace(StartTitle) || StartTitle.Length > 100 || StartMessage is null || StartMessage.Length > 1000 ||
            string.IsNullOrWhiteSpace(StartButton) || StartButton.Length > 100 || Pages is null || Pages.Count is < 2 or > 50 ||
            Pages.Any(p => p is null || !Id(p.Id)) || Pages.Select(p => p.Id).Distinct().Count() != Pages.Count || !Pages.Any(p => p.Id == FirstPageId))
            throw new ArgumentException("開始文面とページIDを確認してください。ページIDは半角英字で始まる英数字・_、重複なし、最大50ページです。");
        foreach (var p in Pages)
        {
            if (!Enum.IsDefined(p.Kind) || !Enum.IsDefined(p.NextStep) || string.IsNullOrWhiteSpace(p.Title) || p.Title.Length > 100 || p.Message is null || p.Message.Length > 1000 ||
                string.IsNullOrWhiteSpace(p.ButtonLabel) || p.ButtonLabel.Length > 100 || p.Choices is null || p.Routes is null || p.Routes.Count > 50)
                throw new ArgumentException($"ページ {p.Id}: 文面・ページ種類を確認してください。");
            if (p.Kind == KioskPageKind.Choice && (!Id(p.Variable) || IsSystemVariable(p.Variable) || p.Variable is "month" or "day" ||
                p.Choices.Count is < 2 or > 10 || p.Choices.Any(c => c is null || !Id(c.Id) || string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 100 || string.IsNullOrWhiteSpace(c.Value) || c.Value.Length > 100) ||
                p.Choices.Select(c => c.Id).Distinct().Count() != p.Choices.Count ||
                p.Variable is "hasFever" or "saysReserved" && p.Choices.Any(c => c.Value is not ("true" or "false"))))
                throw new ArgumentException($"ページ {p.Id}: 選択肢・設定変数を確認してください。発熱と予約の値はtrue/falseです。");
            if (p.NextPageId is null || p.Kind != KioskPageKind.Choice && p.Choices.Count > 0 || p.Kind == KioskPageKind.Guidance && (p.Routes.Count > 0 || p.NextPageId.Length > 0))
                throw new ArgumentException($"ページ {p.Id}: 選択肢は質問ページ、終了案内は遷移なしで設定してください。");
            var variables = Pages.Where(x => x.Kind == KioskPageKind.Choice).Select(x => x.Variable).Concat(["month", "day", "category", "hasChart", "actualReservation"]).ToHashSet();
            if (p.Routes.Any(r => r is null || !variables.Contains(r.Variable) || string.IsNullOrWhiteSpace(r.Value) || r.Value.Length > 100) ||
                p.Routes.Select(r => (r.Variable, r.Value)).Distinct().Count() != p.Routes.Count)
                throw new ArgumentException($"ページ {p.Id}: 条件の変数・値・重複を確認してください。");
            if (p.Kind != KioskPageKind.Guidance && (!Pages.Any(x => x.Id == p.NextPageId) || p.Routes.Any(r => !Pages.Any(x => x.Id == r.NextPageId))))
                throw new ArgumentException($"ページ {p.Id}: 次ページIDが存在しません。");
        }
        int visits = 0;
        void Walk(string id, HashSet<string> path, HashSet<string> assigned, bool lookup)
        {
            if (++visits > 5000) throw new ArgumentException("分岐が多すぎます。フローを簡潔にしてください。");
            if (!path.Add(id)) throw new ArgumentException("ページ遷移が循環しています。");
            var p = Page(id);
            if (lookup && p.Kind != KioskPageKind.Guidance) throw new ArgumentException("照会後は案内ページへ進めてください。");
            if (p.Kind == KioskPageKind.Birthday) { assigned.Add("month"); assigned.Add("day"); }
            if (p.Kind == KioskPageKind.Card) assigned.Add("cardDone");
            if (p.Kind == KioskPageKind.Choice) assigned.Add(p.Variable);
            if (p.Kind == KioskPageKind.Lookup && !new[] { "cardDone", "month", "day", "hasFever", "saysReserved" }.All(assigned.Contains))
                throw new ArgumentException("照会前に資格確認・誕生月日・発熱・予約有無の回答が必要です。");
            if (p.Kind == KioskPageKind.Lookup) { assigned.Add("category"); assigned.Add("hasChart"); assigned.Add("actualReservation"); }
            if (p.Routes.Any(r => !assigned.Contains(r.Variable))) throw new ArgumentException($"ページ {p.Id}: 分岐に使う変数がまだ設定されていません。");
            if (p.Kind == KioskPageKind.Guidance)
            {
                if (!lookup && p.NextStep == KioskNextStep.Finish) throw new ArgumentException("照会前の案内は職員案内にしてください。");
                return;
            }
            foreach (var next in p.Routes.Select(r => r.NextPageId).Append(p.NextPageId).Distinct())
                Walk(next, new(path), new(assigned), lookup || p.Kind == KioskPageKind.Lookup);
        }
        Walk(FirstPageId, [], [], false);
    }
}

