using AttendanceSystem.Core.Application;
using AttendanceSystem.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Infrastructure;

public sealed class IdempotencyEntry
{
    public long Id { get; set; }
    public string Caller { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public int SchoolId { get; set; }
    public string Key { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public int StatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<School> Schools => Set<School>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<AttendanceCode> AttendanceCodes => Set<AttendanceCode>();
    public DbSet<SchoolTerm> SchoolTerms => Set<SchoolTerm>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<AbsenceSummary> AbsenceSummaries => Set<AbsenceSummary>();
    public DbSet<ChronicAbsenceAlert> ChronicAbsenceAlerts => Set<ChronicAbsenceAlert>();
    public DbSet<AttendanceSubmission> AttendanceSubmissions => Set<AttendanceSubmission>();
    public DbSet<IdempotencyEntry> IdempotencyEntries => Set<IdempotencyEntry>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<School>().HasKey(x => x.Id);
        model.Entity<School>().Property(x => x.Name).HasMaxLength(150).IsRequired();
        model.Entity<Student>().HasKey(x => x.Id);
        model.Entity<Student>().HasIndex(x => new { x.SchoolId, x.Active, x.Grade });
        model.Entity<Student>().Property(x => x.FirstName).HasMaxLength(100);
        model.Entity<Student>().Property(x => x.LastName).HasMaxLength(100);
        model.Entity<AttendanceCode>().HasKey(x => x.Value);
        model.Entity<AttendanceCode>().Property(x => x.Value).HasMaxLength(5);
        model.Entity<AttendanceCode>().Property(x => x.Description).HasMaxLength(100);
        model.Entity<SchoolTerm>().HasKey(x => x.Id);
        model.Entity<SchoolTerm>().Property(x => x.Name).HasMaxLength(100);
        model.Entity<AttendanceRecord>().HasKey(x => x.Id);
        model.Entity<AttendanceRecord>().HasIndex(x => new { x.StudentId, x.Date }).IsUnique();
        model.Entity<AttendanceRecord>().HasIndex(x => new { x.StudentId, x.SchoolId, x.Date });
        model.Entity<AttendanceRecord>().Property(x => x.AttendanceCode).HasMaxLength(5);
        model.Entity<AttendanceRecord>().Property(x => x.Notes).HasMaxLength(500);
        model.Entity<AttendanceRecord>().Property(x => x.CreatedBy).HasMaxLength(100);
        model.Entity<AttendanceRecord>().Property(x => x.ModifiedBy).HasMaxLength(100);
        model.Entity<AbsenceSummary>().HasKey(x => x.Id);
        model.Entity<AbsenceSummary>().HasIndex(x => new { x.StudentId, x.SchoolYear }).IsUnique();
        model.Entity<ChronicAbsenceAlert>().HasKey(x => x.Id);
        model.Entity<ChronicAbsenceAlert>().HasIndex(x => new { x.StudentId, x.SchoolId, x.SchoolYear }).IsUnique().HasFilter("\"ResolvedAt\" IS NULL");
        model.Entity<ChronicAbsenceAlert>().Property(x => x.Message).HasMaxLength(500);
        model.Entity<AttendanceSubmission>().HasKey(x => x.Id);
        model.Entity<IdempotencyEntry>().HasKey(x => x.Id);
        model.Entity<IdempotencyEntry>().HasIndex(x => new { x.Caller, x.Endpoint, x.SchoolId, x.Key }).IsUnique();
        model.Entity<IdempotencyEntry>().Property(x => x.Key).HasMaxLength(200);
        model.Entity<IdempotencyEntry>().Property(x => x.RequestHash).HasMaxLength(64);

        model.Entity<AttendanceRecord>().HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AttendanceRecord>().HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AttendanceRecord>().HasOne<AttendanceCode>().WithMany().HasForeignKey(x => x.AttendanceCode).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AttendanceRecord>().HasOne<SchoolTerm>().WithMany().HasForeignKey(x => x.TermId).OnDelete(DeleteBehavior.SetNull);
        model.Entity<Student>().HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SchoolTerm>().HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AbsenceSummary>().HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AbsenceSummary>().HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ChronicAbsenceAlert>().HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ChronicAbsenceAlert>().HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AttendanceSubmission>().HasOne<School>().WithMany().HasForeignKey(x => x.SchoolId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EfAttendanceRepository(ApplicationDbContext db) : IAttendanceRepository
{
    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await work(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try { var result = await work(ct); await transaction.CommitAsync(ct); return result; }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
        { await transaction.RollbackAsync(ct); throw new ConflictException("A conflicting attendance or summary record already exists."); }
        catch { await transaction.RollbackAsync(ct); throw; }
    }
    public Task<School?> GetSchoolAsync(int id, CancellationToken ct) => db.Schools.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<Student?> GetStudentAsync(int id, CancellationToken ct) => db.Students.SingleOrDefaultAsync(x => x.Id == id, ct);
    public Task<AttendanceCode?> GetCodeAsync(string value, CancellationToken ct) => db.AttendanceCodes.SingleOrDefaultAsync(x => x.Value == value.Trim().ToUpper(), ct);
    public Task<SchoolTerm?> GetTermAsync(int schoolId, DateOnly date, CancellationToken ct) => db.SchoolTerms.SingleOrDefaultAsync(x => x.SchoolId == schoolId && x.StartDate <= date && x.EndDate >= date, ct);
    public Task<AttendanceRecord?> GetRecordAsync(int studentId, DateOnly date, CancellationToken ct) => db.AttendanceRecords.SingleOrDefaultAsync(x => x.StudentId == studentId && x.Date == date, ct);
    public async Task<IReadOnlyList<AttendanceRecord>> GetRecordsAsync(int studentId, int schoolId, DateOnly start, DateOnly endExclusive, CancellationToken ct) => await db.AttendanceRecords.Where(x => x.StudentId == studentId && x.SchoolId == schoolId && x.Date >= start && x.Date < endExclusive).ToListAsync(ct);
    public async Task<IReadOnlyList<AttendanceHistoryItem>> GetHistoryAsync(int studentId, DateOnly start, DateOnly endExclusive, int skip, int take, CancellationToken ct) => await db.AttendanceRecords.AsNoTracking().Where(x => x.StudentId == studentId && x.Date >= start && x.Date < endExclusive).OrderByDescending(x => x.Date).ThenByDescending(x => x.Id).Skip(skip).Take(take).Join(db.AttendanceCodes.AsNoTracking(), a => a.AttendanceCode, c => c.Value, (a, c) => new { a, c }).Select(x => new AttendanceHistoryItem(x.a.Date, x.a.AttendanceCode, x.c.Description, x.a.IsAbsent, x.a.IsExcused, x.a.MinutesLate, x.a.Notes, db.SchoolTerms.Where(t => t.Id == x.a.TermId).Select(t => t.Name).FirstOrDefault())).ToListAsync(ct);
    public Task<int> CountHistoryAsync(int studentId, DateOnly start, DateOnly endExclusive, CancellationToken ct) => db.AttendanceRecords.CountAsync(x => x.StudentId == studentId && x.Date >= start && x.Date < endExclusive, ct);
    public Task<AbsenceSummary?> GetSummaryAsync(int studentId, string year, CancellationToken ct) => db.AbsenceSummaries.SingleOrDefaultAsync(x => x.StudentId == studentId && x.SchoolYear == year, ct);
    public Task<bool> HasOpenAlertAsync(int studentId, int schoolId, string year, CancellationToken ct) => db.ChronicAbsenceAlerts.AnyAsync(x => x.StudentId == studentId && x.SchoolId == schoolId && x.SchoolYear == year && x.ResolvedAt == null, ct);
    public void AddSummary(AbsenceSummary summary) => db.AbsenceSummaries.Add(summary);
    public void AddAlert(ChronicAbsenceAlert alert) => db.ChronicAbsenceAlerts.Add(alert);
    public void AddSubmission(AttendanceSubmission submission) => db.AttendanceSubmissions.Add(submission);
    public void AddRecord(AttendanceRecord record) => db.AttendanceRecords.Add(record);
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

public static class DemoSeed
{
    public static async Task SeedAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        if (!await db.Schools.AnyAsync(x => x.Id == 1, ct))
        {
            if (!await db.Schools.AnyAsync(ct)) { db.Schools.Add(new School(1, "SparRock Demo School", 10)); await db.SaveChangesAsync(ct); }
        }
        if (await db.Schools.AnyAsync(x => x.Id == 1, ct) && !await db.Students.AnyAsync(x => x.SchoolId == 1, ct))
        {
            var nextId = Math.Max(1001, (await db.Students.Select(x => (int?)x.Id).MaxAsync(ct) ?? 1000) + 1);
            db.Students.AddRange(new Student(nextId, 1, "Alex", "Rivera", "5"), new Student(nextId + 1, 1, "Jordan", "Chen", "6"));
            await db.SaveChangesAsync(ct);
        }
        var codes = new[] { new AttendanceCode("P", "Present", false, false), new AttendanceCode("A", "Absent", true, false), new AttendanceCode("EA", "Excused absence", true, true), new AttendanceCode("T", "Tardy", false, false) };
        foreach (var code in codes) if (!await db.AttendanceCodes.AnyAsync(x => x.Value == code.Value, ct)) db.AttendanceCodes.Add(code);
        var year = SchoolYear.For(DateOnly.FromDateTime(DateTime.UtcNow));
        if (await db.Schools.AnyAsync(x => x.Id == 1, ct) && !await db.SchoolTerms.AnyAsync(x => x.SchoolId == 1 && x.StartDate == year.StartDate, ct)) db.SchoolTerms.Add(new SchoolTerm((await db.SchoolTerms.Select(x => (int?)x.Id).MaxAsync(ct) ?? 0) + 1, 1, "Demo School Year", year.StartDate, year.EndDateExclusive.AddDays(-1)));
        await db.SaveChangesAsync(ct);
    }
}

