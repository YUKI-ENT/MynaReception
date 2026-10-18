using ReceptionAgent;
using ReceptionAgent.Kiosk;

internal static class PosPrintTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "pos-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
            var old = AgentSettings.Load(root);
            check(old.PosPrint.SeparateJobsPerReceipt, "legacy POS settings send each receipt as a document for AfterDocument cutter drivers");
            check(!old.StartMonitoringOnLaunch && !old.PosPrint.Enabled && old.PosPrint.PaperWidthMm == 80 && old.PosPrint.PrinterName == "POS-80C", "legacy settings default to manual monitoring and disabled POS-80C / 80mm");
            var receipt = new KioskReceipt("test", "00123", "受付 太郎{番号}", "ウケツケ タロウ", "0037", DateTimeOffset.Now);
            string patient = old.PosPrint.Render(old.PosPrint.PatientTemplate, receipt);
            string staff = old.PosPrint.Render(old.PosPrint.StaffTemplate, receipt);
            var xmlReceipt = receipt.WithXmlIdentity(new ReceptionAgent.Reception.CaptureRecord
            { Face = new("マイナ タロウ", new(2000, 1, 1), "0110012345", PatientName: "マイナ 太郎") });
            check(old.PosPrint.Render("{名前}\n{フリガナ}\n{マイナ氏名}\n{マイナフリガナ}", xmlReceipt) == "マイナ 太郎\nマイナ タロウ\nマイナ 太郎\nマイナ タロウ", "manual receipt prefers XML kanji and kana and exposes explicit XML placeholders");
            check(receipt.WithXmlIdentity(new()).Name == receipt.Name && receipt.WithXmlIdentity(new()).NameKana == receipt.NameKana, "missing XML identity preserves existing fallback name and kana");
            try { old.PosPrint.Render("{マイナ氏名}", receipt); check(false, "missing XML name"); }
            catch (InvalidOperationException) { check(true, "explicit XML name cannot silently print chart or iCall data"); }
            check(old.PosPrint.Render(old.PosPrint.PatientTemplate, receipt).Contains(receipt.NameKana), "new default receipt includes kana alongside the name");
            check(patient.Contains("医院名") && patient.Contains("予約番号\n0037") && patient.Contains("診察券番号　00123") && patient.Contains("受付 太郎{番号}"), "patient template retains leading zeros and does not recursively substitute patient data");
            check(staff.StartsWith("スタッフにお渡し下さい") && staff.Contains("番号\n0037") && staff.Contains(receipt.ReceivedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm")), "staff template includes handoff, number and reception time");
            old.StartMonitoringOnLaunch = true; old.PosPrint.Enabled = true; old.PosPrint.PaperWidthMm = 58;
            old.PosPrint.ClinicName = "テスト医院"; old.PosPrint.StaffTemplate = "職員用\n{名前}\n{番号}";
            old.PosPrint.PatientStyles[0] = new() { FontName = "Meiryo", FontSizePt = 16, Bold = true, SpaceAfterMm = 3.5f, Alignment = PosTextAlignment.Left };
            old.PosPrint.StaffStyles[0] = new() { FontName = "Yu Gothic", FontSizePt = 9, SpaceAfterMm = 0 };
            old.Save(root);
            var saved = AgentSettings.Load(root);
            old.PosPrint.SeparateJobsPerReceipt = false; old.Save(root);
            check(!AgentSettings.Load(root).PosPrint.SeparateJobsPerReceipt, "two-page job mode survives POS settings round trip");
            old.PosPrint.SeparateJobsPerReceipt = true; old.Save(root);
            check(AgentSettings.Load(root).PosPrint.SeparateJobsPerReceipt, "per-receipt document mode survives POS settings round trip");
            check(saved.PosPrint.PatientStyles[0].Alignment == PosTextAlignment.Left && saved.PosPrint.StaffStyles[0].Alignment == PosTextAlignment.Center, "per-copy alignment persists and existing style defaults to center");
            try { new PosLineStyle { Alignment = (PosTextAlignment)999 }.Validate(); check(false, "unknown alignment"); }
            catch (ArgumentException) { check(true, "unknown alignment rejected"); }
            check(saved.StartMonitoringOnLaunch && saved.PosPrint.Enabled && saved.PosPrint.PaperWidthMm == 58 && saved.PosPrint.ClinicName == "テスト医院" && saved.PosPrint.StaffTemplate == old.PosPrint.StaffTemplate, "POS templates, width and startup option survive settings round trip");
            check(saved.PosPrint.PatientStyles[0].FontName == "Meiryo" && saved.PosPrint.PatientStyles[0].FontSizePt == 16 && saved.PosPrint.PatientStyles[0].Bold && saved.PosPrint.PatientStyles[0].SpaceAfterMm == 3.5f && saved.PosPrint.StaffStyles[0].SpaceAfterMm == 0, "per-copy font, size, weight and millimeter spacing survive settings round trip");
            check(new PosPrintOptions { PaperWidthMm = 58 }.StyleFor(false, 0, "{名前}").FontSizePt == 10 && new PosPrintOptions().StyleFor(false, 0, "{予約番号}").Bold, "legacy templates retain default normal and number styles");
            var shared = new PosPrintOptions();
            shared.PatientStyles[4] = new() { FontName = "Meiryo", FontSizePt = 15 };
            shared.StaffStyles[4] = new() { FontName = "Yu Gothic", FontSizePt = 9, Alignment = PosTextAlignment.Left, Boxed = false };
            var sharedName = shared.StyleFor(true, 4, "{名前} 様");
            check(sharedName.FontName == "Meiryo" && sharedName.FontSizePt == 15 && sharedName.Alignment == PosTextAlignment.Left && shared.StaffStyles[4].FontSizePt == 9, "staff name follows patient font and size without changing saved overrides or alignment");
            check(shared.StyleFor(true, 0, "スタッフにお渡し下さい").Boxed == true, "handoff message defaults to a border even for existing templates");
            shared.StaffTemplate = "受付スタッフへお渡しください";
            shared.StaffStyles[0] = new() { Boxed = true };
            sharedName = shared.StyleFor(true, 0, shared.StaffTemplate);
            check(sharedName.Boxed == true, "edited handoff wording keeps an explicitly enabled border");
            shared.MatchStaffFontsToPatient = false;
            check(shared.StyleFor(true, 4, "{名前} 様").FontSizePt == 9, "independent staff fonts remain available when matching is disabled");
            old.PosPrint.PaperWidthMm = 58; old.PosPrint.MatchStaffFontsToPatient = false; old.PosPrint.StaffStyles[0].Boxed = true; old.Save(root);
            var bordered = AgentSettings.Load(root).PosPrint;
            check(!bordered.MatchStaffFontsToPatient && bordered.StaffStyles[0].Boxed == true, "font matching option and editable border persist");
            foreach (float invalid in new[] { 0f, 49f, float.NaN })
            {
                try { new PosLineStyle { FontSizePt = invalid }.Validate(); check(false, "invalid font size"); }
                catch (ArgumentException) { check(true, "invalid font size rejected"); }
            }
            try { new PosLineStyle { SpaceAfterMm = -1 }.Validate(); check(false, "negative spacing"); }
            catch (ArgumentException) { check(true, "negative spacing rejected"); }
            old.PosPrint.StaffTemplate = "{不明な項目}";
            try { old.Save(root); check(false, "unknown token rejected"); }
            catch (ArgumentException) { check(AgentSettings.Load(root).PosPrint.StaffTemplate == saved.PosPrint.StaffTemplate, "invalid template cannot replace persisted settings"); }
            old.PosPrint.StaffTemplate = saved.PosPrint.StaffTemplate; old.PosPrint.PaperWidthMm = 60;
            try { old.Save(root); check(false, "unsupported paper width rejected"); }
            catch (ArgumentException) { check(true, "unsupported paper width rejected"); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

