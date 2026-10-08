using AttendanceSystem.Core.Domain;

namespace AttendanceSystem.Core.Domain.Tests;

public class AttendanceRulesTests
{
    [Theory]
    [InlineData("2026-08-31", "2025-2026")]
    [InlineData("2026-09-01", "2026-2027")]
    [InlineData("2026-12-31", "2026-2027")]
    [InlineData("2027-01-01", "2026-2027")]
    public void School_year_uses_september_boundary(string input, string expected)
    {
        var year = SchoolYear.For(DateOnly.Parse(input));
        Assert.Equal(expected, year.Value);
        Assert.True(year.Contains(DateOnly.Parse(input)));
    }

    [Fact]
    public void School_year_range_is_inclusive_start_exclusive_end()
    {
        var year = SchoolYear.Parse("2025-2026");
        Assert.True(year.Contains(new DateOnly(2025, 9, 1)));
        Assert.True(year.Contains(new DateOnly(2026, 8, 31)));
        Assert.False(year.Contains(new DateOnly(2026, 9, 1)));
    }

    [Fact]
    public void Invalid_school_year_is_rejected()
    { Assert.Throws<DomainException>(() => SchoolYear.Parse("2025-2027")); }

    [Fact]
    public void Flags_come_from_code_definition_not_code_name()
    {
        var code = new AttendanceCode("XYZ", "Present despite name", false, true);
        var record = new AttendanceRecord(1, 2, new DateOnly(2026, 9, 1), code, 2, null, null, "teacher", DateTimeOffset.UnixEpoch);
        Assert.False(record.IsAbsent);
        Assert.True(record.IsExcused);
    }

    [Fact]
    public void Attendance_rejects_invalid_lateness_notes_and_inactive_code()
    {
        var active = new AttendanceCode("A", "Absent", true, false);
        var record = new AttendanceRecord(1, 2, new DateOnly(2026, 9, 1), active, 0, null, null, null, DateTimeOffset.UnixEpoch);
        Assert.Throws<DomainException>(() => record.Update(active, -1, null, null, null, DateTimeOffset.UnixEpoch));
        Assert.Throws<DomainException>(() => record.Update(active, 0, new string('x', 501), null, null, DateTimeOffset.UnixEpoch));
        Assert.Throws<DomainException>(() => record.Update(new AttendanceCode("I", "Inactive", true, false, false), null, null, null, null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Summary_threshold_and_recalculation_are_explicit()
    {
        var summary = new AbsenceSummary(1, 2, SchoolYear.Parse("2025-2026"), 9, DateTimeOffset.UnixEpoch);
        Assert.False(summary.IsChronic(10));
        summary.Recalculate(10, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.True(summary.IsChronic(10));
    }
}
