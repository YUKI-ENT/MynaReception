namespace ReceptionAgent;

/// <summary>Common application icon for the main window and all agent dialogs.</summary>
public class ReceptionAgentForm : Form
{
    private static readonly Icon ApplicationIcon = LoadIcon();

    public ReceptionAgentForm()
    {
        Icon = ApplicationIcon;
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(ReceptionAgentForm).Assembly.GetManifestResourceStream("ReceptionAgent.AppIcon.ico")
            ?? throw new InvalidOperationException("ReceptionAgentのアイコンが見つかりません。");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
