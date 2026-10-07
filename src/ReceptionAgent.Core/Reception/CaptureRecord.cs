using ReceptionAgent.Face;
using ReceptionAgent.ICall;

namespace ReceptionAgent.Reception;

public enum CaptureStage { Captured, PatientIdentified, FindWaiting, ReservationFound, ArrivedWaiting, LinkReady, LinkWaiting, Completed, NeedsReview, CandidateFindWaiting }

public sealed class CaptureRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTimeOffset? FileCreatedAt { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FirstSeenAt { get; set; }
    public double? PatientLookupMilliseconds { get; set; }
    public DateTimeOffset? ICallStartedAt { get; set; }
    public DateTimeOffset? ICallReceivedAt { get; set; }
    public int ICallTimeoutCount { get; set; }
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
    public DateTimeOffset ReconciliationNextAt { get; set; }
    public DateTimeOffset ReconciliationFirstMatchAt { get; set; }
    public string ReconciliationCandidateId { get; set; } = "";
    public string VerifiedPatientId { get; set; } = "";
    public bool ReconciliationIdentityMismatch { get; set; }
    public string ReconciliationStatus { get; set; } = "未検証";
    public int ReconciliationChecks { get; set; }
    public bool QualificationReconciliationStarted { get; set; }
    public bool QualificationReconciliationCompleted { get; set; }
    public bool MarkArrived { get; set; }
    public bool LinkReservation { get; set; }
    public bool ManualOperationRequested { get; set; }
    public string KioskAutomaticSessionId { get; set; } = "";
    public ICallRequest? PendingRequest { get; set; }
    public string ReceptionNo { get; set; } = "";
    public string ReservationPatientName { get; set; } = "";
    public string ArrivalResult { get; set; } = "未要求";
    public string LinkResult { get; set; } = "未要求";
    public string DynamicsReceiptDirectory { get; set; } = "";
    public List<ICallResponse> Responses { get; set; } = [];
    public bool? ChartNumberFoundAtReception { get; set; }
    public bool? ReservationFoundAtReception { get; set; }
    public DateTimeOffset? ReceptionClassifiedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public ReceptionCategory ReceptionCategory => ReceptionClassification.Category(this);
    public bool Conflict { get; set; }
}
