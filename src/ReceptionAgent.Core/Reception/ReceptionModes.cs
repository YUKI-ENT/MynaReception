namespace ReceptionAgent.Reception;

public static class ReceptionModes
{
    // Enum values are persisted; map explicitly rather than changing their numeric values.
    public static int Level(ReceptionOperationMode mode) => mode switch
    {
        ReceptionOperationMode.MonitorOnly => 1,
        ReceptionOperationMode.Reservation or ReceptionOperationMode.Staff => 2,
        ReceptionOperationMode.WalkIn => 3,
        ReceptionOperationMode.Kiosk => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static bool IncludesArrival(ReceptionOperationMode mode) => Level(mode) >= 2;
    public static bool IncludesAssignment(ReceptionOperationMode mode) => Level(mode) >= 3;
}
