using System.Drawing.Printing;
using System.Text.Json;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal sealed class KioskReceiptForm : ReceptionAgentForm
{
    private readonly KioskReceipt receipt;
    private readonly bool sample;
    private readonly Bitmap preview;
    private readonly PosPrintOptions options;
    public bool PrintSubmitted { get; private set; }
    public KioskReceiptForm(KioskReceipt receipt, bool sample = false) : this(receipt, sample, AgentSettings.Load(MainForm.DataDirectory).PosPrint) { }
    public KioskReceiptForm(KioskReceipt receipt, bool sample, PosPrintOptions options)
    {
        this.receipt = receipt; this.sample = sample; this.options = options;
        options.Validate();
        Text = $"受付票 — {(sample ? "テスト見本" : "患者用・スタッフ用")}（{options.PaperWidthMm}mm）";
        Size = new(660, 780); StartPosition = FormStartPosition.CenterParent;
        float width = options.PaperWidthMm / 25.4f * 100 - 24;
        using var bitmap = new Bitmap(1, 1); bitmap.SetResolution(100, 100);
        using var measure = Graphics.FromImage(bitmap);
        float first = PosReceiptLayout.Draw(measure, width, options, receipt, false, sample, true);
        float second = PosReceiptLayout.Draw(measure, width, options, receipt, true, sample, true);
        if (first > 1152 || second > 1152) throw new InvalidOperationException("文面が長すぎます。文字サイズ・余白・行数を調整して各用紙を約300mm以内にしてください。");
        float scale = options.PaperWidthMm * 10 / (width + 24);
        preview = new Bitmap(options.PaperWidthMm * 10, (int)Math.Ceiling((first + second + 68) * scale));
        preview.SetResolution(100, 100);
        using (var g = Graphics.FromImage(preview))
        {
            g.Clear(Color.White); g.ScaleTransform(scale, scale); g.TranslateTransform(12, 12);
            PosReceiptLayout.Draw(g, width, options, receipt, false, sample);
            g.TranslateTransform(0, first + 32);
            PosReceiptLayout.Draw(g, width, options, receipt, true, sample);
        }
        var image = new PictureBox { Image = preview, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.LightGray };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new(8) };
        var print = new Button { Text = "2枚を印刷…", AutoSize = true };
        print.Click += (_, _) => Print();
        var save = new Button { Text = "見本を画像保存…", AutoSize = true };
        save.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "PNG画像|*.png", FileName = "受付票.png" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                try { preview.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存できませんでした"); }
        };
        actions.Controls.AddRange([print, save, new Label { Text = "患者用・スタッフ用を各1枚。再操作は再印刷です。", AutoSize = true }]);
        Controls.Add(image); Controls.Add(actions);
    }
    private void Print()
    {
        try
        {
            using var document = new PrintDocument();
            document.PrinterSettings.PrinterName = options.PrinterName;
            using var dialog = new PrintDialog { Document = document, UseEXDialog = true, AllowSomePages = false, AllowSelection = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            KioskReceiptPrinter.Print(receipt, document.PrinterSettings, document.DefaultPageSettings, options, sample);
            PrintSubmitted = true;
            Directory.CreateDirectory(Path.Combine(MainForm.DataDirectory, "Logs"));
            File.AppendAllText(Path.Combine(MainForm.DataDirectory, "Logs", $"receipt-{DateTime.Now:yyyyMMdd}.jsonl"),
                JsonSerializer.Serialize(new { at = DateTimeOffset.Now, eventName = "receipt_spooled", sessionId = receipt.SessionId,
                    printer = document.PrinterSettings.PrinterName, paperWidthMm = options.PaperWidthMm, copies = 2, separateJobs = options.SeparateJobsPerReceipt, sample }) + Environment.NewLine);
            MessageBox.Show(this, "印刷キューへ送信しました。実際の印刷・各用紙のカットはプリンタで確認してください。");
        }
        catch (Exception ex) { MessageBox.Show(this, "印刷結果が不明な場合は、印刷キューと用紙を確認してから再操作してください。\n" + ex.Message, "印刷を確認してください"); }
    }
    protected override void Dispose(bool disposing) { if (disposing) preview.Dispose(); base.Dispose(disposing); }
}
