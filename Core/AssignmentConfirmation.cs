using System.Runtime.InteropServices;

namespace iCallManager.Core;

/// <summary>Retries only reads whose COM elements may disappear as the assignment dialog closes.</summary>
public static class AssignmentConfirmation
{
    public static T Read<T>(Func<T> read, string phase, Action? retryDelay = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return read(); }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                OperationalLog.Write("assignment_confirmation_read_failed", new
                { phase, attempt, error = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}", retry = attempt < 5 });
                if (attempt >= 5) throw;
                if (retryDelay is null) Thread.Sleep(200); else retryDelay();
            }
        }
    }
}
