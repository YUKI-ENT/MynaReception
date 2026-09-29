using System.Globalization;
using System.Text.Json;
using ReceptionAgent.Dynamics;

namespace ReceptionAgent.Oqs.ReferenceNumber;

public sealed record ReferenceDraftRow(string PatientId, string RawChartNo, string Status, string Code, string FileName);

public sealed class ReferenceDraftExporter(IDynamicsProvider provider, string dataDirectory)
{
    public async Task ExportAsync(string institution, long from, long to, string outputDirectory,
        IProgress<ReferenceDraftRow>? progress, CancellationToken token)
    {
        if (from < 1 || to < from || to > 999999999 || to - from >= 100000) throw new ArgumentException("患者番号の範囲を確認してください（1回最大100000番号）。");
        Directory.CreateDirectory(outputDirectory);
        using var manifest = new FileStream(Path.Combine(outputDirectory, "manifest.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var sequence = new OqsRequestSequence(dataDirectory);
        for (long n = from; n <= to; n++)
        {
            token.ThrowIfCancellationRequested();
            string id = n.ToString(CultureInfo.InvariantCulture), raw = "";
            ReferenceDraftRow result;
            try
            {
                var patient = await provider.GetPatientAsync(id, token).ConfigureAwait(false);
                if (patient is null) result = new(id, "", "患者なし", "NOT_FOUND", "");
                else
                {
                    raw = patient.Identity.RawChartNo;
                    if (patient.Identity.PatientId != id) throw new DynamicsPatientDataException("PATIENT_ID_MISMATCH");
                    var info = patient.Insurance;
                    var target = new ReferenceRegistrationTarget(id, info.InsurerNumber, info.InsuredCardSymbol, info.InsuredIdentificationNumber, info.InsuredBranchNumber, info.IsPublicAssistance);
                    byte[] xml = new ReferenceNumberRequestBuilder().Build(institution, ReferenceNumberRequestBuilder.CreateIdentifier(), target);
                    string name = "OQSmuimm01req_" + sequence.Next(DateOnly.FromDateTime(DateTime.Now)) + ".xml";
                    token.ThrowIfCancellationRequested();
                    // Local Work only; this class never writes to an OQS req folder or registration journal.
                    using (var file = new FileStream(Path.Combine(outputDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(xml); file.Flush(true); }
                    result = new(id, raw, "XML作成済み（未送信）", "", name);
                }
            }
            catch (DynamicsPatientDataException ex) { result = new(id, raw, "要確認（未作成）", ex.Code, ""); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            { result = new(id, raw, "要確認（未作成）", "INVALID_INSURANCE", ""); }
            JsonSerializer.Serialize(manifest, result); manifest.WriteByte((byte)'\n'); manifest.Flush(true);
            progress?.Report(result);
        }
    }
}
