using System.Text.Json;
using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

public enum KioskNextStep { StaffHelp, Finish }
public sealed class KioskRule
{
    public ReceptionCategory Category { get; set; }
    public string Title { get; set; } = "受付のご案内";
    public string Message { get; set; } = "受付へお声掛けください。";
    public KioskNextStep NextStep { get; set; } = KioskNextStep.StaffHelp;
}
public sealed class KioskOptions
{
    public int Port { get; set; } = 5180;
    public int FaceTimeoutSeconds { get; set; } = 60;
    public int LookupTimeoutSeconds { get; set; } = 60;
    public List<KioskRule> Rules { get; set; } = Enum.GetValues<ReceptionCategory>()
        .Where(c => c is not (ReceptionCategory.Pending or ReceptionCategory.NeedsReview))
        .Select(c => new KioskRule { Category = c, Message = c switch
        {
            ReceptionCategory.ReturningWithReservation => "予約を確認しました。職員の案内をお待ちください。",
            ReceptionCategory.ReturningWithoutReservation => "この一覧では予約を確認できませんでした。受付へお声掛けください。",
            ReceptionCategory.NewWithReservation => "お名前と生年月日が一致する予約候補があります。受付で確認します。",
            _ => "カルテと予約を確認できませんでした。診察券をお持ちの方も受付へお声掛けください。"
        } }).ToList();
    public void Validate()
    {
        if (Port is < 1024 or > 65535 || FaceTimeoutSeconds is < 15 or > 300 || LookupTimeoutSeconds is < 15 or > 300)
            throw new ArgumentException("ポートは1024～65535、待機時間は15～300秒で指定してください。");
        var categories = Enum.GetValues<ReceptionCategory>().Where(c => c is not (ReceptionCategory.Pending or ReceptionCategory.NeedsReview)).ToArray();
        if (Rules.Count != 4 || categories.Any(c => Rules.Count(r => r.Category == c) != 1) ||
            Rules.Any(r => string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 100 || string.IsNullOrWhiteSpace(r.Message) || r.Message.Length > 1000 || !Enum.IsDefined(r.NextStep)))
            throw new ArgumentException("4分類の案内を各1件、タイトル100文字・本文1000文字以内で設定してください。");
        if (Rules.Any(r => r.Category != ReceptionCategory.ReturningWithReservation && r.NextStep == KioskNextStep.Finish))
            throw new ArgumentException("初期テストでは再診・予約あり以外は職員案内にしてください。");
    }
    public static KioskOptions Load(string directory) => File.Exists(Path.Combine(directory, "kiosk-settings.json"))
        ? JsonSerializer.Deserialize<KioskOptions>(File.ReadAllText(Path.Combine(directory, "kiosk-settings.json"))) ?? throw new InvalidDataException("Kiosk設定を読めません。") : new();
    public void Save(string directory)
    {
        Validate(); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "kiosk-settings.json"), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
