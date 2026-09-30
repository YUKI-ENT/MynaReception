using ReceptionAgent.Face;
using ReceptionAgent.Oqs.ReferenceNumber;

namespace ReceptionAgent.Reception;

public sealed class AutomaticReferenceRegistration(CaptureStore store, SingleReferenceRegistrationService registration)
{
    public static bool IsEligible(CaptureRecord record) =>
        record.AutoRegisterReferenceNumber && record.PatientIdentifiedByDynamics && !record.AutomaticRegistrationAttempted &&
        !record.Conflict && record.GeneratedAt.Date == DateTime.Today &&
        record.Lookup is { Selected: not null, Candidates.Count: 1 } &&
        record.Face is not null && string.IsNullOrWhiteSpace(record.Face.ReferenceNumber);

    public async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!await ProcessNextAsync(token)) await Task.Delay(1000, token);
        }
    }
    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        CaptureRecord? selected;
        await store.WorkflowGate.WaitAsync(token);
        try
        {
            selected = store.List(limit: int.MaxValue).Where(IsEligible).OrderBy(r => r.CapturedAt)
                .FirstOrDefault(r => registration.Load(r.Id) is null);
            if (selected is null) return false;
            // Claim before starting, so invalid insurance and uncertain submissions are never retried in a loop.
            selected.AutomaticRegistrationAttempted = true;
            store.Save(selected);
        }
        finally { store.WorkflowGate.Release(); }
        try
        {
            selected = store.Get(selected.Id);
            selected.Face = FaceXmlParser.Parse(store.ReadXml(selected.Id), selected.Encoding);
            await registration.RegisterAsync(selected, selected.RegistrationOqsRoot, token);
        }
        catch (OperationCanceledException)
        {
            await SaveErrorAsync(selected.Id, "自動登録の待機を中断しました。送信済み要求の結果は自動で再確認します。");
            throw;
        }
        catch (Exception ex) { await SaveErrorAsync(selected.Id, ex.Message); }
        return true;
    }
    private async Task SaveErrorAsync(string id, string message)
    {
        await store.WorkflowGate.WaitAsync();
        try
        {
            var current = store.Get(id);
            if (current.Conflict) return;
            current.AutomaticRegistrationError = message;
            store.Save(current); // Preserve the latest reservation workflow fields.
        }
        finally { store.WorkflowGate.Release(); }
    }
}
