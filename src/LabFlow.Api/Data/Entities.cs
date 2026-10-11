// Entities map 1:1 onto the tables created by database/010_schema.sql.
// The SQL scripts are the source of truth; EF Core is only the access layer (no migrations).

namespace LabFlow.Api.Data;

public class Department
{
    public short DepartmentId { get; set; }
    public string Code { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string? NameFr { get; set; }
    public short SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public class Test
{
    public int TestId { get; set; }
    public string Code { get; set; } = "";
    public string? LoincCode { get; set; }
    public string NameEn { get; set; } = "";
    public string? NameFr { get; set; }
    public string? ShortName { get; set; }
    public short DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string? SpecimenTypeCode { get; set; }
    public string ResultType { get; set; } = "";
    public string? UnitCode { get; set; }
    public string? ApplicableSex { get; set; }
    public bool IsOrderable { get; set; }
    public bool IsOhipInsured { get; set; }
    public decimal? UninsuredPrice { get; set; }
    public bool FastingRequired { get; set; }
    public string? PreparationEn { get; set; }
    public string? PreparationFr { get; set; }
    public short? TurnaroundHours { get; set; }
    public bool IsActive { get; set; }
    public List<PanelMember> Members { get; set; } = [];
}

public class PanelMember
{
    public int PanelTestId { get; set; }
    public int MemberTestId { get; set; }
    public Test Member { get; set; } = null!;
    public short SortOrder { get; set; }
}

public class Location
{
    public short LocationId { get; set; }
    public string Name { get; set; } = "";
    public string AddressLine1 { get; set; } = "";
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string? Phone { get; set; }
    public bool IsCollectionCentre { get; set; }
    public bool IsActive { get; set; }
    public List<LocationHours> Hours { get; set; } = [];
}

public class LocationHours
{
    public short LocationId { get; set; }
    public byte DayOfWeek { get; set; }
    public TimeOnly OpenTime { get; set; }
    public TimeOnly CloseTime { get; set; }
    public byte SlotMinutes { get; set; }
    public byte CapacityPerSlot { get; set; }
}

public class LocationClosure
{
    public short LocationId { get; set; }
    public DateOnly ClosedOn { get; set; }
    public string? Reason { get; set; }
}

public class Patient
{
    public int PatientId { get; set; }
    public string Mrn { get; set; } = "";
    public string? HealthCardNumber { get; set; }
    public string? HealthCardVersion { get; set; }
    public DateOnly? HealthCardExpiry { get; set; }
    public string? HealthCardProvince { get; set; }
    public string FirstName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    public string Sex { get; set; } = "U";
    public string? Email { get; set; }
    public string? MobilePhone { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? PostalCode { get; set; }
    public bool ConsentEmailNotification { get; set; }
    public bool ConsentAiSummary { get; set; }
    public DateTime? ConsentRecordedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public byte[] RowVer { get; set; } = [];
}

public class Practitioner
{
    public int PractitionerId { get; set; }
    public string LicenceNumber { get; set; } = "";
    public string LicenceBody { get; set; } = "CPSO";
    public string? OhipBillingNumber { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Specialty { get; set; }
    public string? ClinicName { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public bool IsActive { get; set; }
}

public class AppUser
{
    public int UserId { get; set; }
    public string ExternalId { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public int? PatientId { get; set; }
    public int? PractitionerId { get; set; }
    public bool IsActive { get; set; }
}

public class Appointment
{
    public int AppointmentId { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public short LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Status { get; set; } = AppointmentStatus.Booked;
    public string Channel { get; set; } = "Web";
    public string? Notes { get; set; }
    public string? CancelReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public byte[] RowVer { get; set; } = [];
    public Requisition? Requisition { get; set; }
}

public static class AppointmentStatus
{
    public const string Booked = "Booked";
    public const string CheckedIn = "CheckedIn";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string NoShow = "NoShow";
}

public class Requisition
{
    public int RequisitionId { get; set; }
    public string AccessionNumber { get; set; } = "";
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int OrderingPractitionerId { get; set; }
    public Practitioner OrderingPractitioner { get; set; } = null!;
    public short LocationId { get; set; }
    public int? AppointmentId { get; set; }
    public string Priority { get; set; } = "Routine";
    public string Status { get; set; } = "Received";
    public string PayerType { get; set; } = "OHIP";
    public DateOnly RequisitionDate { get; set; }
    public string? ClinicalNotes { get; set; }
    public bool IsPregnant { get; set; }
    public byte? PregnancyWeek { get; set; }
    public byte? FastingHours { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int? ReceivedByUserId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVer { get; set; } = [];
    public List<RequisitionItem> Items { get; set; } = [];
}

public class RequisitionItem
{
    public int RequisitionItemId { get; set; }
    public int RequisitionId { get; set; }
    public int TestId { get; set; }
    public Test Test { get; set; } = null!;
    public string Status { get; set; } = "Ordered";
    public bool IsInsured { get; set; }
    public decimal? Price { get; set; }
}

public class Invoice
{
    public int InvoiceId { get; set; }
    public int RequisitionId { get; set; }
    public int PatientId { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; private set; }   // computed column
    public string Status { get; set; } = "Open";
    public DateTime CreatedAt { get; set; }
}

public class Notification
{
    public long NotificationId { get; set; }
    public int? PatientId { get; set; }
    public int? RequisitionId { get; set; }
    public int? AppointmentId { get; set; }
    public string Channel { get; set; } = "Email";
    public string TemplateCode { get; set; } = "";
    public string Language { get; set; } = "en";
    public string Recipient { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public DateTime NextAttemptAt { get; set; }
    public DateTime QueuedAt { get; set; }
}

public class AccessLog
{
    public long AuditId { get; set; }
    public DateTime OccurredAt { get; set; }
    public int? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public int? PatientId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public Guid? CorrelationId { get; set; }
}

/// <summary>An order from a clinic system (ClinicHub), waiting for the patient to come to collection. See database/050.</summary>
public class ElectronicOrder
{
    public int ElectronicOrderId { get; set; }
    public string SourceClinic { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public int PractitionerId { get; set; }
    public Practitioner Practitioner { get; set; } = null!;
    public string Priority { get; set; } = "Routine";
    public string? ClinicalNotes { get; set; }
    public string Status { get; set; } = "Waiting";
    public DateTime ReceivedAt { get; set; }
    public int? RequisitionId { get; set; }
    public List<ElectronicOrderItem> Items { get; set; } = [];
}

public class ElectronicOrderItem
{
    public int ElectronicOrderId { get; set; }
    public int TestId { get; set; }
    public Test Test { get; set; } = null!;
    public string ExternalId { get; set; } = "";
}

/// <summary>One analyte's result (FHIR Observation), lab.Result. For a panel, each member gets its own row under the panel's item.</summary>
public class LabResult
{
    public long ResultId { get; set; }
    public int RequisitionItemId { get; set; }
    public int TestId { get; set; }
    public Test Test { get; set; } = null!;
    public decimal? ValueNumeric { get; set; }
    public string? ValueText { get; set; }
    public string? UnitCode { get; set; }

    /// <summary>The range in force when resulted, copied so a later change never rewrites a released report.</summary>
    public decimal? RefLow { get; set; }
    public decimal? RefHigh { get; set; }
    public string? RefText { get; set; }

    /// <summary>HL7 table 0078: N, L, H, LL, HH, A.</summary>
    public string? AbnormalFlag { get; set; }
    public string Status { get; set; } = "Preliminary";
    public string? Comment { get; set; }
    public DateTime ResultedAt { get; set; }
    public int? ResultedByUserId { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public int? VerifiedByUserId { get; set; }
    public byte[] RowVer { get; set; } = [];
}

/// <summary>A released report for a requisition (FHIR DiagnosticReport), lab.Report. A correction issues a new version.</summary>
public class LabReport
{
    public int ReportId { get; set; }
    public int RequisitionId { get; set; }
    public short Version { get; set; }
    public string Status { get; set; } = "Final";
    public DateTime IssuedAt { get; set; }
    public int? IssuedByUserId { get; set; }
}

public class ReferenceRange
{
    public int ReferenceRangeId { get; set; }
    public int TestId { get; set; }
    public string Sex { get; set; } = "U";
    public int AgeFromDays { get; set; }
    public int AgeToDays { get; set; }
    public decimal? Low { get; set; }
    public decimal? High { get; set; }
    public decimal? CriticalLow { get; set; }
    public decimal? CriticalHigh { get; set; }
    public string? DisplayText { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
}

/// <summary>A message to ClinicHub waiting to be sent (database/051).</summary>
public class OutboundMessage
{
    public int OutboundMessageId { get; set; }
    public string Destination { get; set; } = "ClinicHub";
    public string Kind { get; set; } = "";
    public int? RequisitionId { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}
