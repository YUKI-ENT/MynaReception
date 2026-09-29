namespace ReceptionAgent.Dynamics;

public sealed record DynamicsPatientIdentity(string RawChartNo, string PatientId);
public sealed record DynamicsInsuranceInfo(string InsurerNumber, string InsuredCardSymbol,
    string InsuredIdentificationNumber, string InsuredBranchNumber, bool IsPublicAssistance);
public sealed record DynamicsPatient(DynamicsPatientIdentity Identity, DynamicsInsuranceInfo Insurance);

public sealed class DynamicsPatientDataException(string code) : Exception("患者の保険情報を確認してください: " + code)
{
    public string Code { get; } = code;
}

// Patient master data remains in Dynamics, never in a local replica.
public interface IDynamicsProvider
{
    Task<DynamicsPatient?> GetPatientAsync(string patientId, CancellationToken cancellationToken = default);
}
