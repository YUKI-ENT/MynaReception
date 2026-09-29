using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using iCallManager.Bridge;
using iCallManager.Core;
using iCallManager.ICall;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

UiNode Cell(string text, bool header = false) => new() { Role = header ? 25 : 29, Name = text };
UiNode Row(params UiNode[] nodes) { var r = new UiNode { Role = 28 }; r.Children.AddRange(nodes); return r; }
UiNode Table(params UiNode[] rows) { var t = new UiNode { Role = 24 }; t.Children.AddRange(rows); return t; }
var header = Row(Cell("受付番号", true), Cell("診察券番号", true), Cell("患者名", true));
var arrivalCell = Cell("");
arrivalCell.Children.Add(new() { ControlType = 50000, Name = "来院確認", HelpText = "テスト患者", Enabled = true });
var table = Table(header, Row(Cell("36"), Cell("00011"), Cell("テスト患者"), arrivalCell),
    Row(Cell("37"), Cell("00012"), Cell("確認済み患者")));
var rows = RowParser.Parse(table, new());
Check(rows.Count == 2 && !rows[1].Reservation.CanMarkArrived, "rows without buttons are retained");
Check(rows[0].Reservation.PatientId == "00011" && rows[0].Reservation.ReceptionNo == "36", "column identity preserves leading zeros");
Check(RowParser.Parse(Table(header), new()).Count == 0, "verified empty table");
var twoTables = new UiNode(); twoTables.Children.Add(table); twoTables.Children.Add(table);
try { RowParser.Parse(twoTables, new()); Check(false, "ambiguous table rejected"); }
catch (BridgeException e) { Check(e.Code == "table_not_unique", "ambiguous table rejected"); }
var wrongNameCell = Cell(""); wrongNameCell.Children.Add(new() { ControlType = 50000, Name = "来院確認", HelpText = "別の患者", Enabled = true });
try { RowParser.Parse(Table(header, Row(Cell("36"), Cell("00011"), Cell("テスト患者"), wrongNameCell)), new()); Check(false, "button identity rejected"); }
catch (BridgeException e) { Check(e.Code == "row_layout_unverified", "button identity rejected"); }

UiNode FlatFixture() => DiagnosticFixture.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ie-flat-anonymized.txt"));
UiNode FlatTable(UiNode root) => root.Descendants().Single(n => n.AutomationId == "tableList");
void RejectFlat(UiNode fixture, string testName)
{
    try { RowParser.Parse(fixture, new()); Check(false, testName); }
    catch (BridgeException e) { Check(e.Code is "row_layout_unverified" or "table_not_unique", testName); }
}
var flat = FlatFixture();
var flatRows = RowParser.Parse(flat, new());
Check(flatRows.Count == 2, "actual IE hierarchy: separate header and flat body parsed");
Check(flatRows[1].Reservation.PatientId == "00011" && flatRows[1].Reservation.ReceptionNo == "102" &&
    flatRows[1].Reservation.PatientName == "テスト　患者", "flat table uses card column, never numeric memo column");
Check(!flatRows[0].Reservation.HasPatientIdentity && !flatRows[0].Reservation.CanMarkArrived &&
    !flatRows[0].Reservation.CanLink, "unassigned reservation displayed but cannot be operated");
Check(!flatRows[0].Reservation.HasMarkArrivedButton && flatRows[0].Reservation.HasLinkButton,
    "button presence is independent of unassigned patient operation policy");
var noCard = FlatFixture();
FlatTable(noCard).Children[19] = Cell("-");
FlatTable(noCard).Children[27].Children.Clear();
var noCardRow = RowParser.Parse(noCard, new())[1].Reservation;
Check(noCardRow.HasMarkArrivedButton && !noCardRow.HasLinkButton &&
    !noCardRow.CanMarkArrived && noCardRow.OperationRestriction == "患者IDなし・操作不可",
    "named patient without card shows existing arrival button and explicit restriction");
var disabledArrival = Table(header, Row(Cell("38"), Cell("00013"), Cell("テスト患者"),
    new UiNode { Role = 29, Children = { new UiNode { ControlType = 50000, Name = "来院確認", Enabled = false } } }));
var disabledRow = RowParser.Parse(disabledArrival, new())[0].Reservation;
Check(disabledRow.HasMarkArrivedButton && !disabledRow.CanMarkArrived,
    "disabled arrival button is present but cannot be invoked");
Check(flatRows[1].Arrival?.Key == 244 && flatRows[1].Link?.Key == 240 &&
    flatRows[1].Reservation.InternalId == "90002", "flat patient maps to its own arrival and link buttons");
Check(flatRows[1].Container.Key == 180 && flatRows[1].Cells.Count == 16 &&
    !flatRows[1].Cells.Intersect(flatRows[0].Cells).Any(), "live identity validation uses only selected patient cells");
var missingCell = FlatFixture(); FlatTable(missingCell).Children.RemoveAt(0);
RejectFlat(missingCell, "incomplete flat row rejected");
var shiftedCell = FlatFixture(); var shifted = FlatTable(shiftedCell).Children;
(shifted[11], shifted[27]) = (shifted[27], shifted[11]);
RejectFlat(shiftedCell, "cross-patient link buttons rejected by internal IDs");
var shiftedColumn = FlatFixture(); var shiftedColumns = FlatTable(shiftedColumn).Children;
(shiftedColumns[27], shiftedColumns[29]) = (shiftedColumns[29], shiftedColumns[27]);
RejectFlat(shiftedColumn, "link and arrival in wrong columns rejected");
var changedHeader = FlatFixture();
var flatHeader = changedHeader.Descendants().Single(n => n.Key == 149);
flatHeader.Children[4] = Cell("異なる見出し");
RejectFlat(changedHeader, "unknown flat header layout rejected");
var emptyFlat = FlatFixture(); FlatTable(emptyFlat).Children.Clear();
Check(RowParser.Parse(emptyFlat, new()).Count == 0, "empty flat table with verified header accepted");
var noButtons = FlatFixture();
foreach (var c in FlatTable(noButtons).Children.Skip(16)) c.Children.RemoveAll(n => n.ControlType == 50000);
var noButtonsRows = RowParser.Parse(noButtons, new());
Check(noButtonsRows.Count == 2 && !noButtonsRows[1].Reservation.CanMarkArrived && !noButtonsRows[1].Reservation.CanLink,
    "flat patient retained after action buttons disappear");
var duplicateFlat = FlatFixture();
duplicateFlat.Children.Add(FlatTable(duplicateFlat));
RejectFlat(duplicateFlat, "multiple tableList tables rejected");
if (args.Length >= 1)
{
    var provided = RowParser.Parse(DiagnosticFixture.Load(args[0]), new());
    int expectedCount = args.Length > 1 ? int.Parse(args[1]) : 2;
    Check(provided.Count == expectedCount && provided.All(r =>
        r.Reservation.HasMarkArrivedButton == (r.Arrival != null) && r.Reservation.HasLinkButton == (r.Link != null)),
        "provided diagnostic button presence matches captured elements without exposing patient data");
    if (expectedCount == 9)
        Check(provided.Count(r => !r.Reservation.HasPatientIdentity && r.Reservation.HasMarkArrivedButton) == 3 &&
            provided.Where(r => !r.Reservation.HasPatientIdentity).All(r => !r.Reservation.CanMarkArrived && !r.Reservation.CanLink),
            "provided diagnostic: three no-ID patients have arrival buttons, with operations blocked");
}

var patient = new Reservation("36", "00011", "テスト患者", "57760", true, true);
var fake = new FakeAdapter { Rows = [patient] };
using (var service = new ReservationService(() => fake))
{
    BridgeRequest Request(string action, string? name = "テスト患者") => new(Guid.NewGuid().ToString("N"), action, "00011", "36", name);
    Check((await service.ExecuteAsync(Request("find"))).Reservation == patient, "find returns current reservation");
    Check((await service.ExecuteAsync(Request("arrived"))).Code == "operations_disabled" && fake.Invocations == 0, "read-only default");
    service.OperationsEnabled = true;
    Check((await service.ExecuteAsync(Request("arrived", "別の患者"))).Code == "identity_changed" && fake.Invocations == 0, "mismatch never invokes");
    fake.Rows = [patient, patient];
    Check((await service.ExecuteAsync(Request("arrived"))).Code == "ambiguous_patient", "duplicate patient never invokes");
    fake.Rows = [patient with { CanMarkArrived = false }];
    Check((await service.ExecuteAsync(Request("arrived"))).Code == "action_unavailable", "missing button is not reported as success");
    fake.Rows = [flatRows[0].Reservation];
    Check((await service.ExecuteAsync(new("unassigned", "link", "-", "101", "-"))).Code == "not_found" && fake.Invocations == 0,
        "service refuses placeholder patient identity");
    fake.Rows = [patient];
    Check((await service.ExecuteAsync(Request("link"))).Code == "invoked" && fake.Invocations == 1 && !service.Snapshot.IsCurrent, "dispatch distinct from completion");
    fake.ThrowOnInvoke = true;
    Check((await service.ExecuteAsync(Request("arrived"))).Code == "outcome_unknown", "invoke failure is uncertain");
    fake.ThrowOnInvoke = false;
    fake.ThrowOnRead = true;
    Check((await service.ExecuteAsync(Request("find"))).Code == "automation_unavailable" && !service.Snapshot.IsCurrent, "stale data never returned by find");
    fake.ThrowOnRead = false;
    Check((await service.ExecuteAsync(Request("guide"))).Code == "unsupported_action", "guide excluded");
    Check((await service.ExecuteAsync(Request("print"))).Code == "printing_not_configured", "printer remains replaceable and explicit");
    await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.ExecuteAsync(Request("find"))));
    Check(fake.Threads.Distinct().Count() == 1 && fake.Threads[0] != Environment.CurrentManagedThreadId, "all automation serialized on worker thread");
}

string testRoot = Path.Combine(Path.GetTempPath(), "iCallManager-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
string settingsFile = Path.Combine(testRoot, "settings.json");
var savedSettings = new AppSettings { SyncIntervalSeconds = 17, PatientIdColumn = 3, WindowTitleContains = "Test window" };
savedSettings.Save(settingsFile);
var changedSettings = AppSettings.Load(settingsFile);
changedSettings.BridgeDirectory = AppSettings.PrepareBridgeDirectory(Path.Combine(testRoot, "custom bridge"));
changedSettings.Save(settingsFile);
var reloadedSettings = AppSettings.Load(settingsFile);
Check(reloadedSettings.BridgeDirectory == changedSettings.BridgeDirectory && reloadedSettings.SyncIntervalSeconds == 17 &&
    reloadedSettings.PatientIdColumn == 3 && reloadedSettings.WindowTitleContains == "Test window", "bridge folder persists without overwriting other settings");
Check(Directory.Exists(Path.Combine(changedSettings.BridgeDirectory, "request")) &&
    Directory.Exists(Path.Combine(changedSettings.BridgeDirectory, "response")) &&
    !Directory.EnumerateFiles(changedSettings.BridgeDirectory, ".icall-write-test-*", SearchOption.AllDirectories).Any(),
    "bridge folders created and write probes cleaned up");
changedSettings.BridgeDirectory = "relative-path";
try { changedSettings.Save(settingsFile); Check(false, "invalid path leaves saved settings intact"); }
catch (InvalidDataException) { Check(AppSettings.Load(settingsFile).BridgeDirectory == reloadedSettings.BridgeDirectory, "invalid path leaves saved settings intact"); }
foreach (var invalidPath in new[] { "", @"\\server\share", @"C:\folder:stream", "C:relative" })
{
    try { AppSettings.NormalizeBridgeDirectory(invalidPath); Check(false, "unsupported bridge path rejected"); }
    catch (InvalidDataException) { Check(true, "unsupported bridge path rejected"); }
}
string share = Path.Combine(testRoot, "share"), state = Path.Combine(testRoot, "state");
int executions = 0;
Task<OperationResult> Execute(BridgeRequest r, CancellationToken ct) { executions++; return Task.FromResult(new OperationResult(true, "invoked", "sent", patient)); }
void Submit(BridgeRequest r) => File.WriteAllText(Path.Combine(share, "request", r.RequestId + ".json"), JsonSerializer.Serialize(r, AppSettings.Json));
BridgeResponse Response(string id) => JsonSerializer.Deserialize<BridgeResponse>(File.ReadAllText(Path.Combine(share, "response", id + ".json")), AppSettings.Json)!;
var original = new BridgeRequest("test-1", "arrived", "00011", "36", "テスト患者");
using (var bridge = new FileBridge(share, state, Execute, _ => { }))
{
    Submit(original);
    await bridge.PollAsync();
    Check(executions == 1 && Response("test-1").Code == "invoked", "SMB request produces response");
    Submit(original);
    await bridge.PollAsync();
    Check(executions == 1, "same request does not invoke twice");
    Submit(original with { Action = "link" });
    await bridge.PollAsync();
    Check(executions == 1 && Directory.GetFiles(Path.Combine(state, "rejected")).Length == 1, "conflicting request ID rejected");
    File.WriteAllText(Path.Combine(share, "request", "broken.json"), "{not json");
    File.WriteAllText(Path.Combine(share, "request", "pending.tmp"), "{}");
    await bridge.PollAsync();
    Check(executions == 1 && File.Exists(Path.Combine(share, "request", "pending.tmp")), "malformed request rejected and temporary file ignored");
    try { using var duplicate = new FileBridge(share, state, Execute, _ => { }); Check(false, "second consumer rejected"); }
    catch (IOException) { Check(true, "second consumer rejected"); }
}
using (var bridge = new FileBridge(share, state, Execute, _ => { }))
{
    Submit(original);
    await bridge.PollAsync();
    Check(executions == 1, "completed request survives restart");
    var uncertain = original with { RequestId = "test-crash" };
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(uncertain, AppSettings.Json))));
    File.WriteAllText(Path.Combine(state, "journal", "test-crash.json"), JsonSerializer.Serialize(new { fingerprint, response = (BridgeResponse?)null }, AppSettings.Json));
    Submit(uncertain);
    await bridge.PollAsync();
    Check(executions == 1 && Response("test-crash").Code == "outcome_unknown", "incomplete claim is never retried after crash");
    var client = new FileICallBridgeClient(share);
    Check((await client.SendAsync(original, TimeSpan.FromSeconds(1))).Code == "invoked", "client reads identical completed response");
    try { await client.SendAsync(original with { Action = "link" }, TimeSpan.FromSeconds(1)); Check(false, "client detects conflicting cached response"); }
    catch (InvalidDataException) { Check(true, "client detects conflicting cached response"); }
    var clientRequest = original with { RequestId = "client-1", Action = "find" };
    var clientTask = client.SendAsync(clientRequest, TimeSpan.FromSeconds(5));
    for (int attempt = 0; attempt < 20 && !clientTask.IsCompleted; attempt++)
    { await Task.Delay(50); await bridge.PollAsync(); }
    Check((await clientTask).RequestId == "client-1" && executions == 2, "client and bridge exchange atomically");
    var timeoutRequest = original with { RequestId = "client-timeout" };
    try { await client.SendAsync(timeoutRequest, TimeSpan.FromMilliseconds(50)); Check(false, "client timeout"); }
    catch (TimeoutException) { Check(File.Exists(Path.Combine(share, "request", "client-timeout.json")), "timeout preserves submitted request"); }
}
Console.WriteLine($"All {checks} checks passed. Synthetic test files: {testRoot}");

sealed class FakeAdapter : IReservationAdapter
{
    public IReadOnlyList<Reservation> Rows { get; set; } = [];
    public int Invocations { get; private set; }
    public bool ThrowOnRead { get; set; }
    public bool ThrowOnInvoke { get; set; }
    public List<int> Threads { get; } = [];
    public IReadOnlyList<Reservation> Read()
    {
        Threads.Add(Environment.CurrentManagedThreadId);
        if (ThrowOnRead) throw new InvalidOperationException();
        return Rows;
    }
    public void Invoke(Reservation expected, string action, Func<bool> mayInvoke)
    {
        if (!mayInvoke()) throw new BridgeException("operations_disabled", "disabled");
        Invocations++;
        if (ThrowOnInvoke) throw new InvalidOperationException();
    }
    public string Diagnose() => "fake";
    public void Dispose() { }
}
