using System.Xml.Linq;
using ReceptionAgent.Dynamics;
using ReceptionAgent.Oqs.ReferenceNumber;

internal static class DynamicsTests
{
    public static async Task Run(Action<bool, string> check)
    {
        DynamicsInsuranceCandidate Candidate(string raw, bool main, string insurer = "12345678") => new(
            new(new(raw, DynamicsChartNumber.ToPatientId(raw)), new(insurer, "TEST", raw, "02", false)), main);
        void Rejected(string code, params DynamicsInsuranceCandidate[] rows)
        {
            try { DynamicsInsuranceSelection.Select("11", rows); check(false, code); }
            catch (DynamicsPatientDataException ex) { check(ex.Code == code, code); }
        }
        check(DynamicsInsuranceSelection.ReadMainFlag(true) && DynamicsInsuranceSelection.ReadMainFlag(-1) && DynamicsInsuranceSelection.ReadMainFlag(1), "Access checkbox true and DAO numeric flags supported");
        check(!DynamicsInsuranceSelection.ReadMainFlag(DBNull.Value) && !DynamicsInsuranceSelection.ReadMainFlag(false) && !DynamicsInsuranceSelection.ReadMainFlag(0), "unchecked and null main flags do not select insurance");
        try { DynamicsInsuranceSelection.ReadMainFlag(2); check(false, "unknown flag"); }
        catch (DynamicsPatientDataException) { check(true, "unknown main flag rejected"); }
        check(DynamicsInsuranceSelection.Select("11", [Candidate("110", false), Candidate("117", true)])!.Identity.RawChartNo == "117", "checked main chart selected instead of first branch");
        check(DynamicsInsuranceSelection.Select("11", [Candidate("119", true), Candidate("110", false)])!.Identity.RawChartNo == "119", "main selection independent of row order");
        check(DynamicsInsuranceSelection.Select("11", [Candidate("110", false)])!.Identity.PatientId == "11", "single chart accepted without main check");
        check(DynamicsInsuranceSelection.Select("11", []) is null, "missing patient distinct from missing insurance");
        Rejected("MAIN_INSURANCE_NOT_SET", Candidate("110", false), Candidate("111", false));
        Rejected("MULTIPLE_MAIN_INSURANCE", Candidate("110", true), Candidate("111", true));
        Rejected("NO_HEALTH_INSURANCE", Candidate("110", true, ""), Candidate("111", false));
        Rejected("AMBIGUOUS_INSURANCE", Candidate("110", true), Candidate("110", true, "87654321"));
        Rejected("PATIENT_ID_MISMATCH", Candidate("120", true));
        check(DynamicsInsuranceSelection.Select("11", [Candidate("117", true), Candidate("117", true), Candidate("110", false)])!.Identity.RawChartNo == "117", "identical joined rows do not create a false ambiguity");

        string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-draft-tests-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(root, "Work", "draft");
        var export = new ReferenceDraftExporter(new SelectionProvider(), root);
        await export.ExportAsync("0110012345", 11, 13, output, null, CancellationToken.None);
        var files = Directory.GetFiles(output, "*.xml");
        check(files.Length == 1, "draft range skips missing and ambiguous patients, writes only selected main insurance");
        using var stream = File.OpenRead(files.Single());
        var xml = ReferenceNumberResultParser.Load(stream);
        check(xml.Descendants("ReferenceNumber").Single().Value == "11" && xml.Descendants("InsuredIdentificationNumber").Single().Value == "MAIN_CARD_NUMBER" && xml.Descendants("InsuredBranchNumber").Single().Value == "02", "COM candidate selection flows through to correct registration XML");
        string manifest = File.ReadAllText(Path.Combine(output, "manifest.jsonl"));
        check(File.ReadAllLines(Path.Combine(output, "manifest.jsonl")).Length == 3 && manifest.Contains("MULTIPLE_MAIN_INSURANCE") && manifest.Contains("NOT_FOUND"), "draft results persist for every attempted patient");
        check(!manifest.Contains("MAIN_CARD_NUMBER") && !Directory.Exists(Path.Combine(root, "req")) && !Directory.Exists(Path.Combine(root, "ReferenceRegistration")), "draft does not send or mark registered and manifest omits insurance details");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        string canceledOutput = Path.Combine(root, "cancelled");
        try { await export.ExportAsync("0110012345", 11, 13, canceledOutput, null, cancelled.Token); check(false, "draft cancel"); }
        catch (OperationCanceledException) { check(Directory.GetFiles(canceledOutput, "*.xml").Length == 0, "cancelled draft writes no patient XML"); }
    }
    private sealed class SelectionProvider : IDynamicsProvider
    {
        public Task<DynamicsPatient?> GetPatientAsync(string patientId, CancellationToken cancellationToken = default)
        {
            if (patientId == "12") throw new DynamicsPatientDataException("MULTIPLE_MAIN_INSURANCE");
            if (patientId == "13") return Task.FromResult<DynamicsPatient?>(null);
            var other = new DynamicsInsuranceCandidate(new(new("110", "11"), new("12345678", "TEST", "OLD_CARD_NUMBER", "00", false)), false);
            var main = new DynamicsInsuranceCandidate(new(new("117", "11"), new("12345678", "TEST", "MAIN_CARD_NUMBER", "02", false)), true);
            return Task.FromResult(DynamicsInsuranceSelection.Select(patientId, [other, main]));
        }
    }
}
