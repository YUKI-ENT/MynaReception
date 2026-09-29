namespace ReceptionAgent;

// The caller supplies real workflow state; the timer only changes its appearance.
public sealed class StationDisplay : UserControl
{
    private readonly Label[] stations;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private int current = -1;
    private bool blink, running, failed;
    public StationDisplay(params string[] titles)
    {
        Height = 88; Dock = DockStyle.Top;
        BackColor = Color.FromArgb(20, 29, 37);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = titles.Length, RowCount = 1, Padding = new Padding(6) };
        stations = new Label[titles.Length];
        for (int i = 0; i < titles.Length; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / titles.Length));
            stations[i] = new Label { Text = "●\r\n" + titles[i], Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.SlateGray,
                Font = new Font("Yu Gothic UI", 10, FontStyle.Bold), AccessibleName = titles[i] };
            layout.Controls.Add(stations[i], i, 0);
        }
        Controls.Add(layout);
        timer.Tick += (_, _) => { blink = !blink; PaintState(); };
    }
    public void SetState(int index, bool isRunning, bool hasError = false)
    {
        current = index; running = isRunning; failed = hasError; blink = true;
        timer.Enabled = isRunning; PaintState();
    }
    private void PaintState()
    {
        for (int i = 0; i < stations.Length; i++)
            stations[i].ForeColor = i < current ? Color.LightGreen : i != current ? Color.SlateGray
                : failed ? Color.Salmon : running ? (blink ? Color.Gold : Color.FromArgb(108, 91, 39)) : current == stations.Length - 1 ? Color.LightGreen : Color.Gold;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); foreach (var label in stations) label.Font.Dispose(); }
        base.Dispose(disposing);
    }
}
