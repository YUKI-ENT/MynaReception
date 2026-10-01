namespace iCallManager.Core;

public sealed record Reservation(string ReceptionNo, string PatientId, string PatientName,
    string InternalId, bool CanMarkArrived, bool CanLink)
{
    public int? WaitingOrder { get; init; }
    public bool HasAssignmentButton { get; init; }
    public bool CanAssignDummy { get; init; }
    public bool IsUnassignedDummy => PatientId == "-" && (string.IsNullOrWhiteSpace(PatientName) || PatientName == "-") && HasAssignmentButton;
    public bool HasMarkArrivedButton { get; init; }
    public bool HasLinkButton { get; init; }
    public string? LinkButtonName { get; init; }
    public bool HasPatientIdentity => !string.IsNullOrWhiteSpace(PatientId) && PatientId != "-" &&
        !string.IsNullOrWhiteSpace(PatientName) && PatientName != "-";
    public string OperationRestriction => string.IsNullOrWhiteSpace(PatientId) || PatientId == "-"
        ? "患者IDなし・操作不可"
        : string.IsNullOrWhiteSpace(PatientName) || PatientName == "-" ? "患者名なし・操作不可" : "";
}

public sealed record BridgeRequest(string RequestId, string Action, string PatientId = "",
    string? ExpectedReceptionNo = null, string? ExpectedPatientName = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PatientName { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? NameKana { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GivenName { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Birthdate { get; init; }
    public string Fingerprint() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(this, AppSettings.Json))));
}

public sealed record OperationResult(bool Success, string Code, string Message,
    Reservation? Reservation = null)
{
    public IReadOnlyList<ReservationCandidate>? Candidates { get; init; }
}

public sealed record ReservationCandidate(string ReceptionNo, string? PatientId, string PatientName,
    string ParsedName, string Birthdate, string NameMatch, bool RequiresConfirmation = true);

public sealed record BridgeResponse(string RequestId, bool Success, string Code, string Message,
    string? PatientId, string? ReceptionNo, string? PatientName, DateTimeOffset CompletedAt,
    string RequestFingerprint = "")
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ReservationCandidate>? Candidates { get; init; }
}

public sealed record SyncSnapshot(DateTimeOffset? LastSuccess, bool IsCurrent,
    IReadOnlyList<Reservation> Rows, string Status);

public interface IReservationAdapter : IDisposable
{
    IReadOnlyList<Reservation> Read();
    // Must resolve the row again and verify the complete identity before invoking.
    void Invoke(Reservation expected, string action, Func<bool> mayInvoke);
    Reservation AssignDummy(Reservation expected, string patientId, Func<bool> mayOperate) =>
        throw new BridgeException("assignment_unavailable", "患者割当が未対応です。");
    string Diagnose();
}

public interface ITicketPrinter
{
    OperationResult Print(Reservation reservation);
}

public sealed class UnconfiguredTicketPrinter : ITicketPrinter
{
    public OperationResult Print(Reservation reservation) =>
        new(false, "printing_not_configured", "発券方式が未設定です。", reservation);
}

public sealed class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
