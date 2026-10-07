using System.Drawing.Printing;
using System.Text.Json;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal sealed class KioskReceiptForm : Form
{
    private readonly KioskReceipt receipt;
    private readonly bool sample;
    private readonly Bitmap preview;
    public KioskReceiptForm(KioskReceipt receipt, bool sample = false)
    {
        this.receipt = receipt; this.sample = sample;
        Text = sample ? "受付済み証 — テスト見本（58mm）" : "受付済み証 — 患者用・医院控え（58mm）";
        Size = new(660, 780); StartPosition = FormStartPosition.CenterParent;
        preview = new Bitmap(580, 1100);
        using (var g = Graphics.FromImage(preview))
        { g.Clear(Color.White); Draw(receipt, sample, g, new RectangleF(0, 0, 580, 540), "患者用"); Draw(receipt, sample, g, new RectangleF(0, 560, 580, 540), "医院控え"); }
        var image = new PictureBox { Image = preview, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.LightGray };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        var print = new Button { Text = "2枚を印刷…", AutoSize = true };
        print.Click += (_, _) => Print();
        var save = new Button { Text = "見本を画像保存…", AutoSize = true };
        save.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "PNG画像|*.png", FileName = "受付済み証.png" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                try { preview.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存できませんでした"); }
        };
        actions.Controls.AddRange([print, save, new Label { Text = "1回の印刷で患者用・医院控えを各1枚。再操作は再印刷です。", AutoSize = true }]);
        Controls.Add(image); Controls.Add(actions);
    }
    internal static void Draw(KioskReceipt receipt, bool sample, Graphics graphics, RectangleF area, string copy)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(area.X, area.Y);
        graphics.ScaleTransform(area.Width / 580, area.Height / 540);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var normal = new Font("Yu Gothic", 24, GraphicsUnit.Pixel);
        using var title = new Font("Yu Gothic", 32, FontStyle.Bold, GraphicsUnit.Pixel);
        using var number = new Font("Yu Gothic", 66, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Yu Gothic", 21, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center };
        void Text(string text, Font font, float y, float h)
        {
            float size = font.Size;
            while (size > 12)
            {
                using var fitted = new Font(font.FontFamily, size, font.Style, GraphicsUnit.Pixel);
                var measured = graphics.MeasureString(text, fitted, 504, format);
                if (measured.Height <= h && measured.Width <= 504)
                { graphics.DrawString(text, fitted, Brushes.Black, new RectangleF(38, y, 504, h), format); return; }
                size -= 1;
            }
            using var minimum = new Font(font.FontFamily, 12, font.Style, GraphicsUnit.Pixel);
            graphics.DrawString(text, minimum, Brushes.Black, new RectangleF(38, y, 504, h), format);
        }
        Text(sample ? "テスト見本・受付済み証" : "受付済み証", title, 20, 48);
        Text(copy, normal, 70, 38);
        graphics.DrawLine(Pens.Black, 38, 118, 542, 118);
        Text("予約番号", normal, 132, 36); Text(receipt.ReceptionNo, number, 170, 85);
        Text("カルテ番号  " + receipt.PatientId, title, 266, 48);
        Text(receipt.NameKana, normal, 326, 65);
        Text(receipt.Name + " 様", title, 392, 65);
        Text(receipt.ReceivedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm") + "  マイナ受付", small, 472, 38);
        graphics.Restore(state);
    }
    private void Print()
    {
        try
        {
            using var document = new PrintDocument { DocumentName = sample ? "受付済み証テスト見本" : "マイナ受付済み証（患者用・医院控え）" };
            using var dialog = new PrintDialog { Document = document, UseEXDialog = true, AllowSomePages = false, AllowSelection = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (MessageBox.Show(this, "選択したプリンタへ患者用・医院控えを各1枚送信します。\n" + document.PrinterSettings.PrinterName,
                "受付済み証の印刷", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            document.PrinterSettings.Copies = 1;
            document.DefaultPageSettings.PaperSize = new PaperSize("受付済み証 58×54mm", 228, 213);
            document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            document.DefaultPageSettings.Landscape = false;
            int page = 0;
            document.BeginPrint += (_, _) => page = 0;
            document.PrintPage += (_, e) =>
            {
                // Graphics origin is at the hardware printable area; retain a small inner margin.
                float scale = Math.Min(Math.Min(228, e.PageSettings.PrintableArea.Width) / 580, Math.Min(213, e.PageSettings.PrintableArea.Height) / 540);
                Draw(receipt, sample, e.Graphics!, new RectangleF(0, 0, 580 * scale, 540 * scale), page == 0 ? "患者用" : "医院控え");
                page++; e.HasMorePages = page < 2;
            };
            document.Print();
            Directory.CreateDirectory(Path.Combine(MainForm.DataDirectory, "Logs"));
            File.AppendAllText(Path.Combine(MainForm.DataDirectory, "Logs", $"receipt-{DateTime.Now:yyyyMMdd}.jsonl"),
                JsonSerializer.Serialize(new { at = DateTimeOffset.Now, eventName = "receipt_spooled", sessionId = receipt.SessionId,
                    printer = document.PrinterSettings.PrinterName, copies = 2, sample }) + Environment.NewLine);
            MessageBox.Show(this, "印刷キューへ送信しました。実際の印刷・用紙カットはプリンタで確認してください。");
        }
        catch (Exception ex) { MessageBox.Show(this, "印刷結果が不明な場合は、印刷キューと用紙を確認してから再操作してください。\n" + ex.Message, "印刷を確認してください"); }
    }
    protected override void Dispose(bool disposing) { if (disposing) preview.Dispose(); base.Dispose(disposing); }
}
