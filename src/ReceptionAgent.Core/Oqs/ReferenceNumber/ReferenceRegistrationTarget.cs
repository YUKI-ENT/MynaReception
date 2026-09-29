namespace ReceptionAgent.Oqs.ReferenceNumber;

public sealed record ReferenceRegistrationTarget(string PatientId, string InsurerNumber,
    string InsuredCardSymbol, string InsuredIdentificationNumber, string InsuredBranchNumber,
    bool IsPublicAssistance = false)
{
    public string ReferenceNumber => PatientId;
}
