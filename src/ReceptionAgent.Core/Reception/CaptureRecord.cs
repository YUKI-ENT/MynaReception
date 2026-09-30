using ReceptionAgent.Face;
using ReceptionAgent.ICall;

namespace ReceptionAgent.Reception;

public enum CaptureStage { Captured, PatientIdentified, FindWaiting, ReservationFound, ArrivedWaiting, LinkReady, LinkWaiting, Completed, NeedsReview }

public sealed class CaptureRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTimeOffset? FileCreatedAt { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset RetryAfter { get; set; }
    public FaceXmlEncoding Encoding { get; set; }
    public FaceIdentity? Face { get; set; }
    public FaceLookupResult? Lookup { get; set; }
    public string XmlPatientName { get; set; } = "";
    public CaptureStage Stage { get; set; }
    public string Status { get; set; } = "XML取得済み";
    public string RequestDirectory { get; set; } = "";
    public string ResponseDirectory { get; set; } = "";
    public bool AutoRegisterReferenceNumber { get; set; }
    public string RegistrationOqsRoot { get; set; } = "";
    public bool PatientIdentifiedByDynamics { get; set; }
    public bool AutomaticRegistrationAttempted { get; set; }
    public string AutomaticRegistrationError { get; set; } = "";
    public bool MarkArrived { get; set; }
    public bool LinkReservation { get; set; }
    public bool ManualOperationRequested { get; set; }
    public ICallRequest? PendingRequest { get; set; }
    public string ReceptionNo { get; set; } = "";
    public string ReservationPatientName { get; set; } = "";
    public string ArrivalResult { get; set; } = "未要求";
    public string LinkResult { get; set; } = "未要求";
    public List<ICallResponse> Responses { get; set; } = [];
    public bool Conflict { get; set; }
}
