namespace ReceptionAgent.Reception;

public static class WalkInAssignment
{
    public static bool HasEligiblePatient(CaptureRecord record) =>
        !record.Conflict && !record.ReconciliationIdentityMismatch && record.GeneratedAt.Date == DateTime.Today &&
        record.Lookup is { Selected: not null, Candidates.Count: 1 } &&
        System.Text.RegularExpressions.Regex.IsMatch(record.Lookup.Selected.PatientId, @"\A[0-9]{1,50}\z") &&
        (record.VerifiedPatientId.Length == 0 || record.VerifiedPatientId == record.Lookup.Selected.PatientId) &&
        record.ReceptionCategory == ReceptionCategory.ReturningWithoutReservation &&
        record.ReceptionNo.Length == 0 && record.ReservationPatientName.Length == 0;

    public static bool CanQueue(CaptureRecord record) => HasEligiblePatient(record) &&
        record.Stage == CaptureStage.Completed && record.PendingRequest is null && !record.AssignmentRequested &&
        !record.Responses.Any(r => r.RequestId == "ra-" + record.Id + "-assign");
}
