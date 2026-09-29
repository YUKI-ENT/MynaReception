using System.Globalization;

namespace ReceptionAgent.Dynamics;

public sealed record DynamicsInsuranceCandidate(DynamicsPatient Patient, bool IsMain);

public static class DynamicsInsuranceSelection
{
    public static bool ReadMainFlag(object? value)
    {
        if (value is null or DBNull) return false;
        if (value is bool flag) return flag;
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() switch
        {
            "-1" or "1" or "True" => true,
            "0" or "False" or "" => false,
            _ => throw new DynamicsPatientDataException("INVALID_MAIN_INSURANCE_FLAG")
        };
    }

    public static DynamicsPatient? Select(string patientId, IEnumerable<DynamicsInsuranceCandidate> source)
    {
        var rows = source.Distinct().ToArray();
        if (rows.Length == 0) return null;
        if (rows.Any(r => DynamicsChartNumber.ToPatientId(r.Patient.Identity.RawChartNo) != patientId || r.Patient.Identity.PatientId != patientId))
            throw new DynamicsPatientDataException("PATIENT_ID_MISMATCH");
        bool multipleCharts = rows.Select(r => r.Patient.Identity.RawChartNo).Distinct().Count() > 1;
        var main = rows.Where(r => r.IsMain).ToArray();
        // A lone chart may have no check. With multiple branches, a main check is mandatory.
        var selected = multipleCharts || main.Length > 0 ? main : rows;
        if (selected.Length == 0) throw new DynamicsPatientDataException("MAIN_INSURANCE_NOT_SET");
        if (selected.Select(r => r.Patient.Identity.RawChartNo).Distinct().Count() != 1)
            throw new DynamicsPatientDataException("MULTIPLE_MAIN_INSURANCE");
        var patients = selected.Select(r => r.Patient).Distinct().ToArray();
        if (patients.Length != 1) throw new DynamicsPatientDataException("AMBIGUOUS_INSURANCE");
        if (string.IsNullOrWhiteSpace(patients[0].Insurance.InsurerNumber)) throw new DynamicsPatientDataException("NO_HEALTH_INSURANCE");
        return patients[0];
    }
}
