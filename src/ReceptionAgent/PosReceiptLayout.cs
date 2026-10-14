using ReceptionAgent.Kiosk;

namespace ReceptionAgent;

internal static class PosReceiptLayout
{
    // Coordinates are hundredths of an inch, as used by PrintDocument.
    public static float Draw(Graphics g, float width, PosPrintOptions options, KioskReceipt receipt, bool staff, bool sample = false, bool measureOnly = false)
    {
        string template = staff ? options.StaffTemplate : options.PatientTemplate;
        float y = 10;
        void Line(string line, PosLineStyle style)
        {
            using var format = new StringFormat { Alignment = style.Alignment switch
            { PosTextAlignment.Left => StringAlignment.Near, PosTextAlignment.Right => StringAlignment.Far, _ => StringAlignment.Center } };
            using var font = new Font(style.FontName, style.FontSizePt, style.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            string text = options.Render(line, receipt);
            float padding = style.Boxed == true ? 5 : 0;
            float inset = style.Boxed == true ? 2 : 0;
            float height = g.MeasureString(text.Length == 0 ? " " : text, font, (int)(width - 20 - inset * 2), format).Height;
            if (!measureOnly)
            {
                if (style.Boxed == true)
                {
                    using var border = new Pen(Color.Black, 1);
                    g.DrawRectangle(border, 10, y, width - 20, height + padding * 2);
                }
                g.DrawString(text, font, Brushes.Black, new RectangleF(10 + inset, y + padding, width - 20 - inset * 2, height), format);
            }
            y += height + padding * 2 + style.SpaceAfterMm / 25.4f * 100;
        }
        if (sample) Line("テスト見本", new PosLineStyle { FontSizePt = options.PaperWidthMm == 58 ? 10 : 11 });
        var lines = template.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) Line(lines[i], options.StyleFor(staff, i, lines[i]));
        return y + 15;
    }
}
