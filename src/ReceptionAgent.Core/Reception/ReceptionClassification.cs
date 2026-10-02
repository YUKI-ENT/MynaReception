namespace ReceptionAgent.Reception;

public enum ReceptionCategory { Pending, NeedsReview, NewWithReservation, NewWithoutReservation, ReturningWithReservation, ReturningWithoutReservation }

public static class ReceptionClassification
{
    public static ReceptionCategory Category(CaptureRecord record)
    {
        if (record.Conflict || record.ReconciliationIdentityMismatch || record.Lookup is { Selected: null, Candidates.Count: > 0 }) return ReceptionCategory.NeedsReview;
        bool? chart = record.ChartNumberFoundAtReception ?? (record.Lookup?.Selected is not null ? true : null);
        bool? reservation = record.ReservationFoundAtReception ?? (!string.IsNullOrWhiteSpace(record.ReceptionNo) ? true :
            record.Responses.Any(r => r.RequestId.EndsWith("-find", StringComparison.Ordinal) && !r.Success && r.Code == "not_found") ? false : null);
        if (chart is null || reservation is null) return record.Stage == CaptureStage.NeedsReview ? ReceptionCategory.NeedsReview : ReceptionCategory.Pending;
        return (chart.Value, reservation.Value) switch
        {
            (false, true) => ReceptionCategory.NewWithReservation,
            (false, false) => ReceptionCategory.NewWithoutReservation,
            (true, true) => ReceptionCategory.ReturningWithReservation,
            _ => ReceptionCategory.ReturningWithoutReservation
        };
    }
    public static string Label(ReceptionCategory category) => category switch
    {
        ReceptionCategory.NewWithReservation => "初診（カルテなし）・予約あり",
        ReceptionCategory.NewWithoutReservation => "初診（カルテなし）・予約なし",
        ReceptionCategory.ReturningWithReservation => "再診（カルテあり）・予約あり",
        ReceptionCategory.ReturningWithoutReservation => "再診（カルテあり）・予約なし",
        ReceptionCategory.NeedsReview => "要確認",
        _ => "照会中"
    };
    public static DateTimeOffset? ClassifiedAt(CaptureRecord record) => record.ReceptionClassifiedAt ??
        record.Responses.FirstOrDefault(r => r.RequestId.EndsWith("-find", StringComparison.Ordinal) &&
            (r.Success && r.Code == "found" || !r.Success && r.Code == "not_found"))?.CompletedAt;
    public static TimeSpan? AcquisitionTime(CaptureRecord record)
    {
        var end = ClassifiedAt(record);
        return end.HasValue ? end.Value - new DateTimeOffset(record.GeneratedAt) : null;
    }
    public static string FormatTime(CaptureRecord record)
    {
        var duration = AcquisitionTime(record);
        return duration is null ? "—" : duration.Value < TimeSpan.Zero ? "時計要確認" : duration.Value.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "秒";
    }
}
