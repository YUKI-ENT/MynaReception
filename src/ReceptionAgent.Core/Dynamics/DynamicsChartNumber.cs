using System.Globalization;

namespace ReceptionAgent.Dynamics;

public static class DynamicsChartNumber
{
    public static string ToPatientId(string rawChartNo)
    {
        if (string.IsNullOrEmpty(rawChartNo) || rawChartNo.Any(c => c is < '0' or > '9') ||
            !long.TryParse(rawChartNo, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number < 10)
            throw new DynamicsPatientDataException("INVALID_CHART_NUMBER");
        // The last digit is Dynamics' chart branch, not the insurance card branch.
        return (number / 10).ToString(CultureInfo.InvariantCulture);
    }
}
