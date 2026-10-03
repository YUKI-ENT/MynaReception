using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReceptionAgent;

internal static class ResponseFolderExplorer
{
    // Called on the WinForms STA. Explorer may reuse its existing process and ignore WindowStyle,
    // so also locate the requested folder's shell window and minimize that window only.
    public static async Task<bool> OpenMinimizedAsync(string folder, CancellationToken token)
    {
        string target = AgentSettings.NormalizeRoot(folder);
        using var process = Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = "/n,\"" + target + "\"", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized
        });
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                token.ThrowIfCancellationRequested();
                object? windows = null;
                try
                {
                    windows = ((dynamic)shell!).Windows();
                    for (int i = 0; i < (int)((dynamic)windows).Count; i++)
                    {
                        object? window = null;
                        try
                        {
                            window = ((dynamic)windows).Item(i);
                            string location = (string)((dynamic)window!).LocationURL;
                            if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.IsFile &&
                                string.Equals(Path.TrimEndingDirectorySeparator(uri.LocalPath), target, StringComparison.OrdinalIgnoreCase))
                            {
                                // SW_SHOWMINNOACTIVE: minimize without activating the Explorer window.
                                return ShowWindowAsync(new IntPtr((long)((dynamic)window).HWND), 7);
                            }
                        }
                        catch (COMException) { /* Window closed or navigation still in progress. */ }
                        finally { Release(window); }
                    }
                }
                finally { Release(windows); }
                await Task.Delay(150, token);
            }
            return false;
        }
        finally { Release(shell); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
}
