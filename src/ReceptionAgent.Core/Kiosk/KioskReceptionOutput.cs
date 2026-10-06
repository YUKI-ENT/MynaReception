using ReceptionAgent.Reception;

namespace ReceptionAgent.Kiosk;

/// <summary>Staff/backend handoff. Source XML remains unchanged and is linked by capture ID.</summary>
public sealed record KioskReceptionOutput(string SessionId, string? CaptureId, string? FaceXmlFileName,
    string? PatientId, string? ReceptionNo, int? BirthMonth, int? BirthDay,
    bool? SaysReserved, bool? HasFever, string? ClinicClass, ReceptionCategory Category,
    KioskSessionState State, IReadOnlyDictionary<string, string> Answers);
