namespace iCallManager;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\iCallManager.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("iCallManagerは既に起動しています。", "iCallManager");
            return;
        }
        try { Application.Run(new Form1(Core.AppSettings.Load())); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "iCallManager 起動エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
