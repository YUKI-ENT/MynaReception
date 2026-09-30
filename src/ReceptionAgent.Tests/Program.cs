using System.Text;
using System.Xml.Linq;
using ReceptionAgent.Oqs;
using ReceptionAgent.Oqs.ReferenceNumber;
using ReceptionAgent.Dynamics;

int checks = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); checks++; Console.WriteLine("PASS: " + name); }
var target = new ReferenceRegistrationTarget("00011", "00123456", "記号<&>", "000123", "00");
var builder = new ReferenceNumberRequestBuilder();
byte[] bytes = builder.Build("0000000000", "test-afi", target);
using var stream = new MemoryStream(bytes);
var doc = XDocument.Load(stream);
var info = doc.Root!.Element("MessageBody")!.Element("ReferenceNumberRegistrationInfo")!;
Check(doc.Declaration?.Encoding is "shift_jis" or "Shift_JIS" && doc.Declaration.Standalone == "no", "Shift_JIS declaration and standalone=no");
Check(info.Element("ReferenceNumber")!.Value == "00011" && info.Element("InsuredIdentificationNumber")!.Value == "000123", "leading zeros retained");
Check(info.Element("InsuredCardSymbol")!.Value == "記号<&>", "Japanese encoding and XML escaping round trip");
Check(doc.Root.Element("MessageHeader")!.Element("ArbitraryFileIdentifier")!.Value == "test-afi", "request correlation identifier emitted");
Check(ReferenceNumberRequestBuilder.CreateIdentifier() != ReferenceNumberRequestBuilder.CreateIdentifier(), "request identifiers unique");
using (var padded = new MemoryStream(builder.Build("0000000000", "test", target with { InsurerNumber = "123456" })))
    Check(XDocument.Load(padded).Descendants("InsurerNumber").Single().Value == "  123456", "short insurer is left SPACE padded, not zero padded");
try { builder.Build("123", "test", target); Check(false, "institution length"); }
catch (ArgumentException) { Check(true, "invalid institution length rejected"); }
try { builder.Build("0000000000", "test", target with { InsuredBranchNumber = "" }); Check(false, "unknown branch"); }
catch (ArgumentException) { Check(true, "missing insurance branch is not replaced with 00"); }
try { builder.Build("0000000000", "test", target with { PatientId = "-" }); Check(false, "invalid ID"); }
catch (ArgumentException) { Check(true, "invalid ID rejected"); }
try { builder.Build("0000000000", "test", target with { IsPublicAssistance = true }); Check(false, "public assistance"); }
catch (NotSupportedException) { Check(true, "unverified public assistance schema rejected"); }
try { builder.Build("0000000000", "test", target with { InsuredCardSymbol = "😀" }); Check(false, "lossy encoding"); }
catch (EncoderFallbackException) { Check(true, "unrepresentable characters rejected without lossy conversion"); }
string root = Path.Combine(Path.GetTempPath(), "ReceptionAgent-tests-" + Guid.NewGuid().ToString("N"));
var sequence = new OqsRequestSequence(root);
var day = new DateOnly(2026, 9, 29);
Check(sequence.Next(day) == "202609290001" && new OqsRequestSequence(root).Next(day) == "202609290002", "sequence persists across instances");
Check(sequence.Next(day.AddDays(1)) == "202609300001", "next day resets sequence");
try { sequence.Next(day); Check(false, "clock reversal"); } catch (InvalidOperationException) { Check(true, "clock reversal rejected"); }
File.WriteAllText(Path.Combine(root, "reference_request_sequence.txt"), "broken");
try { sequence.Next(day); Check(false, "corrupt sequence"); } catch (InvalidDataException) { Check(true, "corrupt sequence not reset silently"); }
Check(Enumerable.Range(0, 10).All(branch => DynamicsChartNumber.ToPatientId("24434" + branch) == "24434"), "all ten Dynamics chart branches map to one patient ID by integer division");
Check(DynamicsChartNumber.ToPatientId("000119") == "11", "chart number uses numeric division, not textual truncation");
Check(DynamicsChartNumber.ToPatientId("10") == "1", "minimum positive chart patient ID");
foreach (string invalid in new[] { "", "9", "-110", "110.5", "110x", "999999999999999999999" })
{
    try { DynamicsChartNumber.ToPatientId(invalid); Check(false, "invalid chart"); }
    catch (DynamicsPatientDataException) { Check(true, "invalid chart rejected: " + invalid); }
}
var mappedTarget = target with { PatientId = DynamicsChartNumber.ToPatientId("244347"), InsuredBranchNumber = "02" };
using (var mappedXml = new MemoryStream(builder.Build("0000000000", "mapped", mappedTarget)))
{
    var mappedDoc = XDocument.Load(mappedXml);
    Check(mappedDoc.Descendants("ReferenceNumber").Single().Value == "24434" && mappedDoc.Descendants("InsuredBranchNumber").Single().Value == "02", "Dynamics chart branch and insurance card branch remain independent in XML");
}
var mappingStore = new RegistrationJobStore(Path.Combine(root, "mapping-jobs"));
var draft = new ReferenceRegistrationJob { InstitutionCode = "0000000000", OqsRoot = root, From = 11, To = 11, InsuranceBranchField = "", RemoveChartBranchDigit = false };
using (mappingStore.AcquireLease()) mappingStore.Save(draft);
var loadedDraft = mappingStore.Load(draft.Id);
Check(loadedDraft.InsuranceBranchField == "枝番" && loadedDraft.RemoveChartBranchDigit, "unstarted legacy draft adopts confirmed mapping");
draft.Entries.Add(new RegistrationEntry { PatientId = "11" });
using (mappingStore.AcquireLease()) mappingStore.Save(draft);
Check(!mappingStore.Load(draft.Id).RemoveChartBranchDigit, "started legacy mapping is preserved for review");
Check(DynamicsInstitutionCode.Build("01", "1", "0012345") == "0110012345", "institution code concatenates prefecture, fee table and seven-digit code");
Check(DynamicsInstitutionCode.Build(1, 1, 12345) == "0110012345", "numeric institution components retain required leading zeros");
foreach (var parts in new object?[][] { [null, 1, 12345], [DBNull.Value, 1, 12345], [1, "", 12345], [100, 1, 12345], [1, 12, 12345], [1, 1, 12345678], [1, 1, "12x"], [1, 1, -1], [1, 1, 12.5m] })
{
    try { DynamicsInstitutionCode.Build(parts[0], parts[1], parts[2]); Check(false, "invalid institution component"); }
    catch (InvalidDataException) { Check(true, "invalid institution component rejected"); }
}
string settingsRoot = Path.Combine(root, "settings-test");
new ReceptionAgent.AgentSettings { OqsRoot = root + Path.DirectorySeparatorChar }.Save(settingsRoot);
Check(ReceptionAgent.AgentSettings.Load(settingsRoot).OqsRoot == Path.TrimEndingDirectorySeparator(root), "OQS settings persist normalized root");
try { new ReceptionAgent.AgentSettings { OqsRoot = "relative-folder" }.Save(settingsRoot); Check(false, "relative OQS path"); }
catch (ArgumentException) { Check(ReceptionAgent.AgentSettings.Load(settingsRoot).OqsRoot == root, "invalid settings do not replace previous OQS folder"); }
File.WriteAllText(Path.Combine(settingsRoot, "settings.json"), "broken");
try { ReceptionAgent.AgentSettings.Load(settingsRoot); Check(false, "corrupt settings"); }
catch (System.Text.Json.JsonException) { Check(true, "corrupt settings not silently reset"); }
await BulkTests.Run(Check);
await DynamicsTests.Run(Check);
await FaceTests.Run(Check);
FaceInsuranceTests.Run(Check);
await CaptureTests.Run(Check);
await SingleRegistrationTests.Run(Check);
await AutomaticRegistrationTests.Run(Check);
Console.WriteLine($"All {checks} checks passed (synthetic data only).");
