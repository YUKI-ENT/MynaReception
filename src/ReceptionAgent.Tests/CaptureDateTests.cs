using ReceptionAgent.Reception;

internal static class CaptureDateTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-dates-" + Guid.NewGuid().ToString("N"));
        var store = new CaptureStore(root);
        var day = new DateOnly(2026, 10, 1);
        CaptureRecord Add(string name, DateOnly generated, DateTimeOffset updated, CaptureStage stage = CaptureStage.Captured)
        {
            var record = new CaptureRecord { FileName = name, ContentHash = name, GeneratedAt = generated.ToDateTime(TimeOnly.MinValue),
                UpdatedAt = updated, CapturedAt = updated, Stage = stage };
            store.Capture(record, [1]); return record;
        }
        var updated = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(9));
        var today = Add("today.xml", day, updated);
        var yesterday = Add("yesterday.xml", day.AddDays(-1), updated.AddHours(1));
        Add("yesterday2.xml", day.AddDays(-1), updated.AddHours(2), CaptureStage.Completed);
        check(store.List(limit: 1, date: day).Single().Id == today.Id,
            "date filter precedes result limit even when another day has newer updates");
        check(store.List(date: day.AddDays(-1)).Count == 2 && store.List(date: day.AddDays(1)).Count == 0,
            "date selection separates histories and handles empty day");
        check(store.ListDates().SequenceEqual(new[] { day, day.AddDays(-1) }),
            "selector dates are unique descending XML generated dates rather than capture/update dates");
        check(store.List(pendingOnly: true, date: day.AddDays(-1)).Single().Id == yesterday.Id,
            "date query retains pending workflow filtering");
        var reopened = new CaptureStore(root);
        check(reopened.List(date: day).Single().Id == today.Id && reopened.ListDates().Count == 2,
            "saved histories support day selection without recapture or schema migration");
        check(reopened.List().Count == 3, "unfiltered monitor/history callers retain original behavior");
    }
}
