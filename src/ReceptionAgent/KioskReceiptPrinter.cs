using System.Drawing.Printing;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal static class KioskReceiptPrinter
{
    public static void Print(KioskReceipt receipt, string printerName) => Print(receipt, printerName, AgentSettings.Load(MainForm.DataDirectory).PosPrint);

    public static void Print(KioskReceipt receipt, string printerName, PosPrintOptions options, bool sample = false)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(printerName)) throw new InvalidOperationException("発券プリンタが未設定です。");
        using var document = new PrintDocument { DocumentName = sample ? "受付票テスト見本" : "受付票（患者用・スタッフ用）", PrintController = new StandardPrintController() };
        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid) throw new InvalidOperationException("発券プリンタが見つかりません: " + printerName);
        if (document.PrinterSettings.PrintToFile || printerName.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
            printerName.Contains("XPS", StringComparison.OrdinalIgnoreCase) || printerName.Equals("Fax", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("発券には用紙へ印刷するプリンタを指定してください。");
        document.PrinterSettings.Copies = 1;
        int width = (int)Math.Round(options.PaperWidthMm / 25.4 * 100);
        // Measure at the same DPI/unit as the Windows printing surface, allowing wrapped Japanese names.
        using var bitmap = new Bitmap(1, 1); bitmap.SetResolution(100, 100);
        using var measure = Graphics.FromImage(bitmap);
        float printableWidth = width - 24; // 3mm margins on each edge
        int[] heights = [0, 0];
        for (int i = 0; i < 2; i++)
            heights[i] = (int)Math.Ceiling(PosReceiptLayout.Draw(measure, printableWidth, options, receipt, i == 1, sample, true)) + 48;
        if (heights.Any(h => h > 1200)) throw new InvalidOperationException("文面が長すぎます。各用紙が約300mm以内になるように編集してください。");
        document.DefaultPageSettings.Margins = new Margins(12, 12, 12, 12);
        document.DefaultPageSettings.Landscape = false;
        int page = 0;
        document.BeginPrint += (_, _) => page = 0;
        document.QueryPageSettings += (_, e) => e.PageSettings.PaperSize = new PaperSize($"受付票 {options.PaperWidthMm}mm", width, heights[page]);
        document.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            float drawWidth = Math.Min(printableWidth, e.PageSettings.PrintableArea.Width - Math.Max(0, 12 - e.PageSettings.HardMarginX) - 4);
            if (drawWidth <= 20) throw new InvalidOperationException("印刷可能な用紙幅が不足しています。プリンタの用紙設定を確認してください。");
            float height = PosReceiptLayout.Draw(g, drawWidth, options, receipt, page == 1, sample, true);
            if (height > Math.Min(e.MarginBounds.Height, e.PageSettings.PrintableArea.Height - 4))
                throw new InvalidOperationException("プリンタの印刷可能領域に文面が収まりません。用紙サイズ設定と文面を確認してください。");
            g.TranslateTransform(Math.Max(0, 12 - e.PageSettings.HardMarginX), Math.Max(0, 12 - e.PageSettings.HardMarginY));
            PosReceiptLayout.Draw(g, drawWidth, options, receipt, page == 1, sample);
            page++; e.HasMorePages = page < 2;
        };
        document.Print();
    }
}
