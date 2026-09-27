-- Synthetic demo data. Every person, address and number here is fictional.
-- Walks one patient through the full flow: booking -> reception -> collection -> results -> report
-- -> "results ready" email queued. The AI interpretation is left for the app to generate.

SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

INSERT core.Location (Name, AddressLine1, City, PostalCode, Phone, Email, IsCollectionCentre, IsLab)
VALUES (N'LabFlow Demo - Mississauga', N'100 Example Street, Unit 1', N'Mississauga', 'L5B 0A1', '905-555-0100', N'demo@labflow.test', 1, 1);
DECLARE @LocationId smallint = SCOPE_IDENTITY();

INSERT sched.LocationHours (LocationId, DayOfWeek, OpenTime, CloseTime, SlotMinutes, CapacityPerSlot)
SELECT @LocationId, d, '07:00', CASE WHEN d = 6 THEN '12:00' ELSE '16:00' END, 10, 2
FROM (VALUES (1),(2),(3),(4),(5),(6)) v(d);

INSERT sched.LocationClosure (LocationId, ClosedOn, Reason) VALUES
    (@LocationId, '2026-10-12', N'Thanksgiving'),
    (@LocationId, '2026-12-25', N'Christmas Day'),
    (@LocationId, '2026-12-26', N'Boxing Day');

INSERT core.Practitioner (LicenceNumber, OhipBillingNumber, FirstName, LastName, Specialty, ClinicName, City, Province, PostalCode, Phone, Fax, Email)
VALUES ('00001', '000001', N'Maya', N'Demo', N'Family Medicine', N'Demo Family Health Team', N'Mississauga', 'ON', 'L5B 0A2', '905-555-0110', '905-555-0111', N'dr.demo@clinic.test');
DECLARE @PractitionerId int = SCOPE_IDENTITY();

-- Health card numbers carry a valid Luhn check digit but are made up.
INSERT core.Patient (Mrn, HealthCardNumber, HealthCardVersion, HealthCardExpiry, HealthCardProvince, FirstName, LastName,
                     DateOfBirth, Sex, Email, MobilePhone, PreferredLanguage, City, Province, PostalCode,
                     ConsentEmailNotification, ConsentAiSummary, ConsentRecordedAt)
VALUES
    ('LF0000001', '9876543217', 'AB', '2029-05-31', 'ON', N'Daniel', N'Sample', '1978-03-14', 'M', N'daniel.sample@example.test', '416-555-0101', 'en', N'Toronto',     'ON', 'M5V 0A1', 1, 1, SYSUTCDATETIME()),
    ('LF0000002', '1234567897', 'CD', '2028-11-30', 'ON', N'Émilie', N'Exemple', '1990-07-02', 'F', N'emilie.exemple@example.test', '613-555-0102', 'fr', N'Ottawa',   'ON', 'K1P 0A1', 1, 0, SYSUTCDATETIME()),
    ('LF0000003', '1111111116', 'EF', '2027-01-31', 'ON', N'Priya',  N'Placeholder', '1965-11-20', 'F', N'priya.p@example.test', '905-555-0103', 'en', N'Brampton', 'ON', 'L6T 0A1', 0, 0, NULL);
DECLARE @PatientId int = (SELECT PatientId FROM core.Patient WHERE Mrn = 'LF0000001');

INSERT sec.AppUser (ExternalId, Email, DisplayName, Role, PatientId, PractitionerId) VALUES
    ('demo-admin',     N'admin@labflow.test',     N'Admin User',        'Admin',        NULL,       NULL),
    ('demo-reception', N'reception@labflow.test', N'Reception Desk',    'Reception',    NULL,       NULL),
    ('demo-collector', N'collector@labflow.test', N'Specimen Collector','Collector',    NULL,       NULL),
    ('demo-tech',      N'tech@labflow.test',      N'Lab Technologist',  'Technologist', NULL,       NULL),
    ('demo-path',      N'path@labflow.test',      N'Dr. Lab Director',  'Pathologist',  NULL,       NULL),
    ('demo-dr',        N'dr.demo@clinic.test',    N'Dr. Maya Demo',     'Practitioner', NULL,       @PractitionerId),
    ('demo-patient',   N'daniel.sample@example.test', N'Daniel Sample', 'Patient',      @PatientId, NULL);

DECLARE @Reception int = (SELECT UserId FROM sec.AppUser WHERE ExternalId = 'demo-reception'),
        @Collector int = (SELECT UserId FROM sec.AppUser WHERE ExternalId = 'demo-collector'),
        @Tech      int = (SELECT UserId FROM sec.AppUser WHERE ExternalId = 'demo-tech'),
        @Path      int = (SELECT UserId FROM sec.AppUser WHERE ExternalId = 'demo-path');

-- 1. Booking (08:10 Toronto time = 12:10 UTC in September)
INSERT sched.Appointment (PatientId, LocationId, StartUtc, EndUtc, Status, Channel)
VALUES (@PatientId, @LocationId, '2026-09-21 12:10', '2026-09-21 12:20', 'Completed', 'Web');
DECLARE @AppointmentId int = SCOPE_IDENTITY();

INSERT notify.Notification (PatientId, AppointmentId, Channel, TemplateCode, Recipient, Status, AttemptCount, QueuedAt, SentAt)
VALUES (@PatientId, @AppointmentId, 'Email', 'AppointmentConfirmed', N'daniel.sample@example.test', 'Sent', 1, '2026-09-18 15:00', '2026-09-18 15:00');

-- 2. Reception
INSERT lab.Requisition (AccessionNumber, PatientId, OrderingPractitionerId, LocationId, AppointmentId, Status, PayerType,
                        RequisitionDate, ClinicalNotes, FastingHours, ReceivedAt, ReceivedByUserId)
VALUES ('LF26-000001', @PatientId, @PractitionerId, @LocationId, @AppointmentId, 'Final', 'OHIP',
        '2026-09-15', N'Annual physical. Family history of type 2 diabetes.', 12, '2026-09-21 12:12', @Reception);
DECLARE @RequisitionId int = SCOPE_IDENTITY();

INSERT lab.RequisitionItem (RequisitionId, TestId, Status, IsInsured, Price)
SELECT @RequisitionId, t.TestId, 'Verified', t.IsOhipInsured, CASE WHEN t.IsOhipInsured = 0 THEN t.UninsuredPrice END
FROM ref.Test t
WHERE t.Code IN ('LIPID','GLUF','A1C','VITD','ALT','CBC');

-- 3. Collection
INSERT lab.Specimen (RequisitionId, Barcode, SpecimenTypeCode, CollectedAt, CollectedByUserId, ReceivedInLabAt, Status) VALUES
    (@RequisitionId, 'LF26000001-01', 'SER', '2026-09-21 12:15', @Collector, '2026-09-21 14:00', 'Received'),
    (@RequisitionId, 'LF26000001-02', 'BLD', '2026-09-21 12:16', @Collector, '2026-09-21 14:00', 'Received');

-- 4. Results: ranges and flags come from the same functions the app uses
DECLARE @Sex char(1), @AgeDays int;
SELECT @Sex = Sex, @AgeDays = DATEDIFF(DAY, DateOfBirth, '2026-09-21') FROM core.Patient WHERE PatientId = @PatientId;

WITH v (Code, Value) AS (
    SELECT * FROM (VALUES
        ('CHOL', 6.10), ('TRIG', 2.30), ('HDL', 0.90), ('LDLC', 4.15),
        ('GLUF', 6.80), ('A1C', 6.30), ('VITD', 48), ('ALT', 52),
        ('WBC', 6.4), ('HGB', 148), ('PLT', 245)) x(Code, Value)
),
items AS (
    -- ordered item for each result: the panel it belongs to, or the test itself
    SELECT i.RequisitionItemId, TestId = COALESCE(pm.MemberTestId, i.TestId)
    FROM lab.RequisitionItem i
    LEFT JOIN ref.PanelMember pm ON pm.PanelTestId = i.TestId
    WHERE i.RequisitionId = @RequisitionId
)
INSERT lab.Result (RequisitionItemId, TestId, SpecimenId, ValueNumeric, UnitCode, RefLow, RefHigh, RefText, AbnormalFlag,
                   Status, ResultedAt, ResultedByUserId, VerifiedAt, VerifiedByUserId)
SELECT it.RequisitionItemId, t.TestId, s.SpecimenId, v.Value, t.UnitCode, rr.Low, rr.High, rr.DisplayText, f.Flag,
       'Final', '2026-09-21 18:30', @Tech, '2026-09-21 19:05', @Path
FROM v
JOIN ref.Test t  ON t.Code = v.Code
JOIN items it    ON it.TestId = t.TestId
JOIN lab.Specimen s ON s.RequisitionId = @RequisitionId AND s.SpecimenTypeCode = t.SpecimenTypeCode
OUTER APPLY ref.fn_ReferenceRangeFor(t.TestId, @Sex, @AgeDays, DEFAULT, '2026-09-21') rr
OUTER APPLY lab.fn_AbnormalFlag(v.Value, rr.Low, rr.High, rr.CriticalLow, rr.CriticalHigh) f;

-- 5. Report + outbox + OHIP claim + invoice for the uninsured vitamin D
INSERT lab.Report (RequisitionId, Version, Status, IssuedAt, IssuedByUserId, IsPatientVisible)
VALUES (@RequisitionId, 1, 'Final', '2026-09-21 19:10', @Path, 1);

INSERT notify.Notification (PatientId, RequisitionId, Channel, TemplateCode, Recipient)
VALUES (@PatientId, @RequisitionId, 'Email', 'ResultsReady', N'daniel.sample@example.test');

INSERT billing.OhipClaim (RequisitionId, PatientId, HealthCardNumber, HealthCardVersion, ReferringOhipBillingNumber, ServiceDate)
SELECT @RequisitionId, p.PatientId, p.HealthCardNumber, p.HealthCardVersion, pr.OhipBillingNumber, '2026-09-21'
FROM core.Patient p CROSS JOIN core.Practitioner pr
WHERE p.PatientId = @PatientId AND pr.PractitionerId = @PractitionerId;

INSERT billing.Invoice (RequisitionId, PatientId, Subtotal, Status, PaymentProvider, PaymentReference, PaidAt)
SELECT @RequisitionId, @PatientId, SUM(Price), 'Paid', 'Stripe', 'pi_demo_0001', '2026-09-21 12:13'
FROM lab.RequisitionItem WHERE RequisitionId = @RequisitionId AND IsInsured = 0;

INSERT audit.AccessLog (UserId, Action, EntityType, EntityId, PatientId, Details) VALUES
    (@Reception, 'Create', 'Requisition', CAST(@RequisitionId AS varchar(20)), @PatientId, N'{"accession":"LF26-000001"}'),
    (@Path,      'Update', 'Report',      CAST(@RequisitionId AS varchar(20)), @PatientId, N'{"status":"Final"}');

COMMIT;
