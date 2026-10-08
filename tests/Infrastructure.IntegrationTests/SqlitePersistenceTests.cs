using AttendanceSystem.Core.Domain;
using AttendanceSystem.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.Infrastructure.IntegrationTests;

public class SqlitePersistenceTests
{
    [Fact]
    public async Task Schema_seed_and_unique_student_date_constraint_work()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await DemoSeed.SeedAsync(db);
        await DemoSeed.SeedAsync(db);
        Assert.Equal(2, await db.Students.CountAsync());
        var record = new AttendanceRecord(1001, 1, new DateOnly(2026, 10, 1), await db.AttendanceCodes.SingleAsync(x => x.Value == "A"), 0, null, null, "tester", DateTimeOffset.UtcNow);
        db.AttendanceRecords.Add(record); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.AttendanceRecords.Add(new AttendanceRecord(1001, 1, new DateOnly(2026, 10, 1), await db.AttendanceCodes.SingleAsync(x => x.Value == "P"), null, null, null, "tester", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Summary_and_open_alert_indexes_are_persisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options); await db.Database.EnsureCreatedAsync(); await DemoSeed.SeedAsync(db);
        db.AbsenceSummaries.Add(new AbsenceSummary(1001, 1, SchoolYear.Parse("2026-2027"), 10, DateTimeOffset.UtcNow));
        db.ChronicAbsenceAlerts.Add(new ChronicAbsenceAlert(1001, 1, SchoolYear.Parse("2026-2027"), DateTimeOffset.UtcNow, "Threshold reached"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.ChronicAbsenceAlerts.Add(new ChronicAbsenceAlert(1001, 1, SchoolYear.Parse("2026-2027"), DateTimeOffset.UtcNow, "Duplicate"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
