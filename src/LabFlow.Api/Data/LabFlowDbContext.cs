using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LabFlow.Api.Data;

public enum Sequence { Accession, Mrn }

public class LabFlowDbContext(DbContextOptions<LabFlowDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Test> Tests => Set<Test>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<LocationHours> LocationHours => Set<LocationHours>();
    public DbSet<LocationClosure> LocationClosures => Set<LocationClosure>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Practitioner> Practitioners => Set<Practitioner>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Requisition> Requisitions => Set<Requisition>();
    public DbSet<RequisitionItem> RequisitionItems => Set<RequisitionItem>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();

    public async Task<int> NextSequenceValueAsync(Sequence sequence, CancellationToken ct)
    {
        // Sequence names can't be parameters, so only these fixed statements are ever run.
        FormattableString sql = sequence switch
        {
            Sequence.Accession => $"SELECT NEXT VALUE FOR lab.AccessionSeq AS [Value]",
            Sequence.Mrn => $"SELECT NEXT VALUE FOR core.MrnSeq AS [Value]",
            _ => throw new ArgumentOutOfRangeException(nameof(sequence)),
        };
        // ToListAsync, not SingleAsync: a composed query would wrap this in a subquery,
        // where SQL Server doesn't allow NEXT VALUE FOR.
        return (await Database.SqlQuery<int>(sql).ToListAsync(ct)).Single();
    }

    // Every datetime2 column holds UTC. SQL Server doesn't store the kind, so stamp it on read;
    // otherwise JSON would serialize without the trailing Z and browsers would read local time.
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    private class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Department>().ToTable("Department", "ref");

        b.Entity<Test>(e =>
        {
            e.ToTable("Test", "ref");
            e.Property(x => x.UninsuredPrice).HasPrecision(10, 2);
            e.HasMany(x => x.Members).WithOne().HasForeignKey(x => x.PanelTestId);
        });

        b.Entity<PanelMember>(e =>
        {
            e.ToTable("PanelMember", "ref");
            e.HasKey(x => new { x.PanelTestId, x.MemberTestId });
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.MemberTestId);
        });

        b.Entity<Location>(e =>
        {
            e.ToTable("Location", "core");
            e.HasMany(x => x.Hours).WithOne().HasForeignKey(x => x.LocationId);
        });

        b.Entity<LocationHours>(e =>
        {
            e.ToTable("LocationHours", "sched");
            e.HasKey(x => new { x.LocationId, x.DayOfWeek, x.OpenTime });
        });

        b.Entity<LocationClosure>(e =>
        {
            e.ToTable("LocationClosure", "sched");
            e.HasKey(x => new { x.LocationId, x.ClosedOn });
        });

        b.Entity<Patient>(e =>
        {
            e.ToTable("Patient", "core", t => t.IsTemporal(h =>
            {
                h.UseHistoryTable("Patient", "history");
                h.HasPeriodStart("ValidFrom");
                h.HasPeriodEnd("ValidTo");
            }));
            e.Property(x => x.RowVer).IsRowVersion();
        });

        b.Entity<Practitioner>().ToTable("Practitioner", "core");

        b.Entity<AppUser>(e =>
        {
            e.ToTable("AppUser", "sec");
            e.HasKey(x => x.UserId);
        });

        b.Entity<Appointment>(e =>
        {
            e.ToTable("Appointment", "sched");
            e.Property(x => x.RowVer).IsRowVersion();
            e.HasOne(x => x.Requisition).WithOne().HasForeignKey<Requisition>(x => x.AppointmentId);
        });

        b.Entity<Requisition>(e =>
        {
            e.ToTable("Requisition", "lab");
            e.Property(x => x.RowVer).IsRowVersion();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.RequisitionId);
        });

        b.Entity<RequisitionItem>(e =>
        {
            e.ToTable("RequisitionItem", "lab");
            e.Property(x => x.Price).HasPrecision(10, 2);
        });

        b.Entity<Invoice>(e =>
        {
            e.ToTable("Invoice", "billing");
            e.Property(x => x.Subtotal).HasPrecision(10, 2);
            e.Property(x => x.Tax).HasPrecision(10, 2);
            e.Property(x => x.Total).HasPrecision(10, 2).HasComputedColumnSql("[Subtotal] + [Tax]", stored: true);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("Notification", "notify");
            e.HasKey(x => x.NotificationId);
        });

        // The append-only trigger means EF must not use OUTPUT without INTO on this table.
        b.Entity<AccessLog>(e =>
        {
            e.ToTable("AccessLog", "audit", t => t.HasTrigger("TR_AccessLog_AppendOnly"));
            e.HasKey(x => x.AuditId);
        });
    }
}
