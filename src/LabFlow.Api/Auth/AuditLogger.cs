using System.Diagnostics;
using System.Text.Json;
using LabFlow.Api.Data;

namespace LabFlow.Api.Auth;

/// <summary>
/// Adds PHIPA access-log rows to the current DbContext. Rows are saved with the caller's
/// SaveChanges, so the log entry and the change it describes commit together.
/// </summary>
public class AuditLogger(LabFlowDbContext db, IHttpContextAccessor http)
{
    public void Log(string action, string entityType, object? entityId, int? patientId, object? details = null)
    {
        var ctx = http.HttpContext;
        db.AccessLogs.Add(new AccessLog
        {
            OccurredAt = DateTime.UtcNow,
            UserId = ctx?.User.UserId(),
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            PatientId = patientId,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            IpAddress = ctx?.Connection.RemoteIpAddress?.ToString(),
            // W3C trace id is 32 hex chars, the same shape as a Guid in "N" format
            CorrelationId = Activity.Current is { } a ? Guid.ParseExact(a.TraceId.ToHexString(), "N") : null,
        });
    }
}
