using AttendanceSystem.Core.Application;
using AttendanceSystem.Core.Domain;

namespace AttendanceSystem.Core.Application.Tests;

public class AttendanceUseCaseTests
{
    [Fact]
    public async Task Save_creates_then_updates_daily_record_and_recalculates_absences()
    {
        var repository = new FakeRepository();
        var handler = new SaveDailyAttendanceHandler(repository, new FixedClock());
        var date = new DateOnly(2026, 10, 1);
        var created = await handler.Handle(new SaveDailyAttendanceCommand(2, date, [new(10, "A")], "teacher"), default);
        Assert.Equal(1, created.CreatedCount);
        Assert.Equal(1, created.Students.Single().TotalAbsences);
        var updated = await handler.Handle(new SaveDailyAttendanceCommand(2, date, [new(10, "P")], "teacher"), default);
        Assert.Equal(1, updated.UpdatedCount);
        Assert.Equal(0, updated.Students.Single().TotalAbsences);
        Assert.Single(repository.Records);
        Assert.Equal("P", repository.Records.Single().AttendanceCode);
        Assert.Equal(99, repository.Records.Single().TermId);
    }

    [Fact]
    public async Task Recalculation_creates_only_one_open_alert_at_the_school_threshold()
    {
        var repo = new FakeRepository();
        var handler = new SaveDailyAttendanceHandler(repo, new FixedClock());
        for (var day = 1; day <= 11; day++)
            await handler.Handle(new SaveDailyAttendanceCommand(2, new DateOnly(2026, 10, day), [new(10, "A")], "teacher"), default);
        Assert.Equal(11, repo.Records.Count);
        Assert.Single(repo.Alerts);
        Assert.Contains("10 absences", repo.Alerts.Single().Message);
        Assert.Equal(11, repo.Summaries.Single().TotalAbsences);
    }

    [Fact]
    public async Task History_and_chronic_status_use_current_year_and_configured_threshold()
    {
        var repo = new FakeRepository { ConfiguredSchool = new School(2, "School", 1) };
        var absent = new AttendanceCode("A", "Absent", true, false);
        repo.Records.Add(new AttendanceRecord(10, 2, new DateOnly(2026, 10, 1), absent, null, "private note", null, null, DateTimeOffset.UnixEpoch));
        repo.Records.Add(new AttendanceRecord(10, 2, new DateOnly(2026, 8, 31), absent, null, null, null, null, DateTimeOffset.UnixEpoch));
        var history = await new GetAttendanceHistoryHandler(repo, new FixedClock()).Handle(new GetAttendanceHistoryQuery(10), default);
        Assert.Equal("2026-2027", history.SchoolYear);
        Assert.Equal(1, history.TotalAbsences);
        Assert.True(history.IsChronic);
        Assert.Single(history.Records);
        var status = await new GetChronicAbsenceHandler(repo, new FixedClock()).Handle(new GetChronicAbsenceQuery(10), default);
        Assert.Equal(1, status.TotalAbsences);
        Assert.True(status.IsChronic);
    }
    [Fact]
    public async Task Empty_batch_is_a_validation_error()
    {
        var handler = new SaveDailyAttendanceHandler(new FakeRepository(), new FixedClock());
        await Assert.ThrowsAsync<RequestValidationException>(() => handler.Handle(new SaveDailyAttendanceCommand(2, new DateOnly(2026, 10, 1), [], null), default));
    }

    [Fact]
    public async Task Cross_school_students_are_rejected()
    {
        var repo = new FakeRepository(); repo.Students[11] = new Student(11, 7, "Sam", "Lee");
        var handler = new SaveDailyAttendanceHandler(repo, new FixedClock());
        await Assert.ThrowsAsync<RequestValidationException>(() => handler.Handle(new SaveDailyAttendanceCommand(2, new DateOnly(2026, 10, 1), [new(11, "A")], null), default));
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-07T12:00:00Z"); }
    private sealed class FakeRepository : IAttendanceRepository
    {
        public readonly Dictionary<int, Student> Students = new() { [10] = new Student(10, 2, "Taylor", "Morgan") };
        public readonly List<AttendanceRecord> Records = [];
        private readonly List<AbsenceSummary> summaries = [];
        private readonly List<ChronicAbsenceAlert> alerts = [];
        public IReadOnlyList<ChronicAbsenceAlert> Alerts => alerts;
        public IReadOnlyList<AbsenceSummary> Summaries => summaries;
        public School ConfiguredSchool { get; set; } = new School(2, "School");
        public Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) => work(cancellationToken);
        public Task<School?> GetSchoolAsync(int id, CancellationToken ct) => Task.FromResult<School?>(id == 2 ? ConfiguredSchool : null);
        public Task<Student?> GetStudentAsync(int id, CancellationToken ct) => Task.FromResult(Students.GetValueOrDefault(id));
        public Task<AttendanceCode?> GetCodeAsync(string value, CancellationToken ct) => Task.FromResult<AttendanceCode?>(value == "A" ? new("A", "Absent", true, false) : value == "P" ? new("P", "Present", false, false) : null);
        public Task<SchoolTerm?> GetTermAsync(int schoolId, DateOnly date, CancellationToken ct) => Task.FromResult<SchoolTerm?>(schoolId == 2 && date >= new DateOnly(2026, 9, 1) && date <= new DateOnly(2026, 12, 31) ? new SchoolTerm(99, 2, "Fall", new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31)) : null);
        public Task<AttendanceRecord?> GetRecordAsync(int studentId, DateOnly date, CancellationToken ct) => Task.FromResult(Records.SingleOrDefault(x => x.StudentId == studentId && x.Date == date));
        public Task<IReadOnlyList<AttendanceRecord>> GetRecordsAsync(int studentId, int schoolId, DateOnly start, DateOnly end, CancellationToken ct) => Task.FromResult<IReadOnlyList<AttendanceRecord>>(Records.Where(x => x.StudentId == studentId && x.SchoolId == schoolId && x.Date >= start && x.Date < end).ToArray());
        public Task<IReadOnlyList<AttendanceHistoryItem>> GetHistoryAsync(int studentId, DateOnly start, DateOnly end, int skip, int take, CancellationToken ct) => Task.FromResult<IReadOnlyList<AttendanceHistoryItem>>(Records.Where(x => x.StudentId == studentId && x.Date >= start && x.Date < end).OrderByDescending(x => x.Date).Skip(skip).Take(take).Select(x => new AttendanceHistoryItem(x.Date, x.AttendanceCode, x.AttendanceCode, x.IsAbsent, x.IsExcused, x.MinutesLate, x.Notes, null)).ToArray());
        public Task<int> CountHistoryAsync(int studentId, DateOnly start, DateOnly end, CancellationToken ct) => Task.FromResult(Records.Count(x => x.StudentId == studentId && x.Date >= start && x.Date < end));
        public Task<AbsenceSummary?> GetSummaryAsync(int studentId, string year, CancellationToken ct) => Task.FromResult(summaries.SingleOrDefault(x => x.StudentId == studentId && x.SchoolYear == year));
        public Task<bool> HasOpenAlertAsync(int studentId, int schoolId, string year, CancellationToken ct) => Task.FromResult(alerts.Any(x => x.StudentId == studentId && x.SchoolId == schoolId && x.SchoolYear == year && !x.IsResolved));
        public void AddSummary(AbsenceSummary summary) => summaries.Add(summary);
        public void AddAlert(ChronicAbsenceAlert alert) => alerts.Add(alert);
        public void AddSubmission(AttendanceSubmission submission) { }
        public void AddRecord(AttendanceRecord record) => Records.Add(record);
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
