using System.Text.RegularExpressions;

namespace ReceptionAgent.Face;

public static class FaceReferencePatient
{
    public static FaceLookupResult? Resolve(FaceIdentity identity)
    {
        string reference = identity.ReferenceNumber?.Trim() ?? "";
        if (reference.Length == 0) return null;
        if (!Regex.IsMatch(reference, @"\A[0-9]{1,50}\z"))
            throw new InvalidDataException("ReferenceNumberがカルテ番号として不正です。半角数字50桁以内を確認してください。");
        // ReferenceNumber is already the branch-free patient ID; do not divide or search Dynamics again.
        var patient = new FacePatientMatch(reference, identity.PatientName ?? "", identity.NameKana, identity.Birthdate, []);
        return new([patient], patient, false, "face XMLのReferenceNumberからカルテ番号取得");
    }
}
