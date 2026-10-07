using System.Drawing.Printing;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal static class KioskReceiptPrinter
{
    public static void Print(KioskReceipt receipt, string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName)) throw new InvalidOperationException("自動発券プリンタが未設定です。");
        using var document = new PrintDocument { DocumentName = "マイナ受付済み証（患者用・医院控え）", PrintController = new StandardPrintController() };
        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid) throw new InvalidOperationException("自動発券プリンタが見つかりません: " + printerName);
        // Virtual print-to-file printers require interactive destinations and cannot run unattended.
        if (document.PrinterSettings.PrintToFile || printerName.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
            printerName.Contains("XPS", StringComparison.OrdinalIgnoreCase) || printerName.Equals("Fax", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("自動発券には用紙へ印刷するプリンタを指定してください。");
        document.PrinterSettings.Copies = 1;
        document.DefaultPageSettings.PaperSize = new PaperSize("受付済み証 58×54mm", 228, 213);
        document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        document.DefaultPageSettings.Landscape = false;
        int page = 0;
        document.BeginPrint += (_, _) => page = 0;
        document.PrintPage += (_, e) =>
        {
            float scale = Math.Min(Math.Min(228, e.PageSettings.PrintableArea.Width) / 580, Math.Min(213, e.PageSettings.PrintableArea.Height) / 540);
            KioskReceiptForm.Draw(receipt, false, e.Graphics!, new RectangleF(0, 0, 580 * scale, 540 * scale), page == 0 ? "患者用" : "医院控え");
            page++; e.HasMorePages = page < 2;
        };
        document.Print();
    }
}
