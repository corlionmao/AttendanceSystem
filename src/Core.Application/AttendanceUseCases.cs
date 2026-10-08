using AttendanceSystem.Core.Domain;
using MediatR;

namespace AttendanceSystem.Core.Application;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("One or more request values are invalid.")
{ public IReadOnlyDictionary<string, string[]> Errors { get; } = errors; }
public sealed class NotFoundException(string message) : Exception(message);
public sealed class ConflictException(string message) : Exception(message);

public sealed record AttendanceEntryInput(int StudentId, string Code, int? MinutesLate = null, string? Notes = null);
public sealed record StudentSaveResult(int StudentId, int TotalAbsences, int Threshold, bool IsChronic);
public sealed record SaveAttendanceResult(int CreatedCount, int UpdatedCount, IReadOnlyList<StudentSaveResult> Students, DateTimeOffset SubmittedAt);
public sealed record SaveDailyAttendanceCommand(int SchoolId, DateOnly AttendanceDate, IReadOnlyList<AttendanceEntryInput> Entries, string? Actor) : IRequest<SaveAttendanceResult>;

public sealed record AttendanceHistoryItem(DateOnly Date, string Code, string CodeDescription, bool IsAbsent, bool IsExcused, int? MinutesLate, string? Notes, string? TermName);
public sealed record AttendanceHistoryResult(int StudentId, string SchoolYear, int TotalAbsences, int Threshold, bool IsChronic, IReadOnlyList<AttendanceHistoryItem> Records, int Page, int PageSize);
public sealed record GetAttendanceHistoryQuery(int StudentId, string? SchoolYear = null, int Page = 1, int PageSize = 50) : IRequest<AttendanceHistoryResult>;
public sealed record ChronicAbsenceResult(int StudentId, string SchoolYear, int TotalAbsences, int Threshold, bool IsChronic);
public sealed record GetChronicAbsenceQuery(int StudentId, string? SchoolYear = null) : IRequest<ChronicAbsenceResult>;

public interface IAttendanceRepository
{
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
    Task<School?> GetSchoolAsync(int id, CancellationToken cancellationToken);
    Task<Student?> GetStudentAsync(int id, CancellationToken cancellationToken);
    Task<AttendanceCode?> GetCodeAsync(string value, CancellationToken cancellationToken);
    Task<SchoolTerm?> GetTermAsync(int schoolId, DateOnly date, CancellationToken cancellationToken);
    Task<AttendanceRecord?> GetRecordAsync(int studentId, DateOnly date, CancellationToken cancellationToken);
    Task<IReadOnlyList<AttendanceRecord>> GetRecordsAsync(int studentId, int schoolId, DateOnly start, DateOnly endExclusive, CancellationToken cancellationToken);
    Task<IReadOnlyList<AttendanceHistoryItem>> GetHistoryAsync(int studentId, DateOnly start, DateOnly endExclusive, int skip, int take, CancellationToken cancellationToken);
    Task<int> CountHistoryAsync(int studentId, DateOnly start, DateOnly endExclusive, CancellationToken cancellationToken);
    Task<AbsenceSummary?> GetSummaryAsync(int studentId, string schoolYear, CancellationToken cancellationToken);
    Task<bool> HasOpenAlertAsync(int studentId, int schoolId, string schoolYear, CancellationToken cancellationToken);
    void AddSummary(AbsenceSummary summary);
    void AddAlert(ChronicAbsenceAlert alert);
    void AddSubmission(AttendanceSubmission submission);
    void AddRecord(AttendanceRecord record);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class SaveDailyAttendanceHandler(IAttendanceRepository repository, IClock clock) : IRequestHandler<SaveDailyAttendanceCommand, SaveAttendanceResult>
{
    public Task<SaveAttendanceResult> Handle(SaveDailyAttendanceCommand request, CancellationToken cancellationToken)
    {
        Validate(request);
        return repository.InTransactionAsync(async ct =>
        {
            var school = await repository.GetSchoolAsync(request.SchoolId, ct) ?? throw new NotFoundException("School was not found.");
            var term = await repository.GetTermAsync(school.Id, request.AttendanceDate, ct);
            var year = SchoolYear.For(request.AttendanceDate);
            var created = 0; var updated = 0;
            var statuses = new List<StudentSaveResult>(request.Entries.Count);
            foreach (var entry in request.Entries)
            {
                var student = await repository.GetStudentAsync(entry.StudentId, ct);
                if (student is null) throw new NotFoundException($"Student {entry.StudentId} was not found.");
                if (!student.Active) throw new RequestValidationException(new Dictionary<string, string[]> { [$"entries[{entry.StudentId}].studentId"] = ["Student is not active."] });
                if (student.SchoolId != school.Id) throw new RequestValidationException(new Dictionary<string, string[]> { [$"entries[{entry.StudentId}].studentId"] = ["Student does not belong to the requested school."] });
                var code = await repository.GetCodeAsync(entry.Code, ct);
                if (code is null || !code.IsActive) throw new RequestValidationException(new Dictionary<string, string[]> { [$"entries[{entry.StudentId}].code"] = ["Attendance code is unknown or inactive."] });
                var now = clock.UtcNow;
                var record = await repository.GetRecordAsync(entry.StudentId, request.AttendanceDate, ct);
                if (record is null)
                {
                    repository.AddRecord(new AttendanceRecord(student.Id, school.Id, request.AttendanceDate, code, entry.MinutesLate, entry.Notes, term?.Id, request.Actor, now));
                    created++;
                }
                else
                {
                    if (record.SchoolId != school.Id) throw new ConflictException("Existing attendance record has inconsistent school ownership.");
                    record.Update(code, entry.MinutesLate, entry.Notes, term?.Id, request.Actor, now);
                    updated++;
                }
                var records = await repository.GetRecordsAsync(student.Id, school.Id, year.StartDate, year.EndDateExclusive, ct);
                // Include the pending insert/update because the transaction has not flushed this entry yet.
                var total = records.Count(x => x.IsAbsent);
                var candidate = await repository.GetRecordAsync(student.Id, request.AttendanceDate, ct);
                var pendingAbsent = code.IsAbsent;
                if (candidate is null) total += pendingAbsent ? 1 : 0;
                else total = total - (candidate.IsAbsent ? 1 : 0) + (pendingAbsent ? 1 : 0);
                var summary = await repository.GetSummaryAsync(student.Id, year.Value, ct);
                if (summary is null) repository.AddSummary(new AbsenceSummary(student.Id, school.Id, year, total, now));
                else summary.Recalculate(total, now);
                var threshold = school.EffectiveAbsenceThreshold;
                if (total >= threshold && !await repository.HasOpenAlertAsync(student.Id, school.Id, year.Value, ct))
                    repository.AddAlert(new ChronicAbsenceAlert(student.Id, school.Id, year, now, $"Student has reached {total} absences this year."));
                statuses.Add(new StudentSaveResult(student.Id, total, threshold, total >= threshold));
            }
            var submittedAt = clock.UtcNow;
            repository.AddSubmission(new AttendanceSubmission(school.Id, request.AttendanceDate, request.Entries.Count, submittedAt, request.Actor));
            await repository.SaveChangesAsync(ct);
            return new SaveAttendanceResult(created, updated, statuses, submittedAt);
        }, cancellationToken);
    }

    private static void Validate(SaveDailyAttendanceCommand request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SchoolId <= 0) errors["schoolId"] = ["School identifier must be positive."];
        if (request.AttendanceDate == default) errors["attendanceDate"] = ["Attendance date is required."];
        if (request.Entries is null || request.Entries.Count == 0) errors["entries"] = ["At least one attendance entry is required."];
        else
        {
            var duplicateIds = request.Entries.GroupBy(x => x.StudentId).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (duplicateIds.Length > 0) errors["entries"] = ["Each student may appear only once in a batch."];
            foreach (var entry in request.Entries)
            {
                if (entry.StudentId <= 0) errors[$"entries[{entry.StudentId}].studentId"] = ["Student identifier must be positive."];
                if (string.IsNullOrWhiteSpace(entry.Code) || entry.Code.Trim().Length > 5) errors[$"entries[{entry.StudentId}].code"] = ["Attendance code is required and limited to 5 characters."];
                if (entry.MinutesLate < 0) errors[$"entries[{entry.StudentId}].minutesLate"] = ["Minutes late cannot be negative."];
                if (entry.Notes?.Length > 500) errors[$"entries[{entry.StudentId}].notes"] = ["Notes cannot exceed 500 characters."];
            }
        }
        if (errors.Count > 0) throw new RequestValidationException(errors);
    }
}

public sealed class GetAttendanceHistoryHandler(IAttendanceRepository repository, IClock clock) : IRequestHandler<GetAttendanceHistoryQuery, AttendanceHistoryResult>
{
    public async Task<AttendanceHistoryResult> Handle(GetAttendanceHistoryQuery query, CancellationToken cancellationToken)
    {
        Validate(query.StudentId, query.SchoolYear, query.Page, query.PageSize);
        var student = await repository.GetStudentAsync(query.StudentId, cancellationToken) ?? throw new NotFoundException("Student was not found.");
        var school = await repository.GetSchoolAsync(student.SchoolId, cancellationToken) ?? throw new NotFoundException("School was not found.");
        var year = query.SchoolYear is null ? SchoolYear.For(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)) : SchoolYear.Parse(query.SchoolYear);
        var total = (await repository.GetRecordsAsync(student.Id, student.SchoolId, year.StartDate, year.EndDateExclusive, cancellationToken)).Count(x => x.IsAbsent);
        var items = await repository.GetHistoryAsync(student.Id, year.StartDate, year.EndDateExclusive, (query.Page - 1) * query.PageSize, query.PageSize, cancellationToken);
        return new(student.Id, year.Value, total, school.EffectiveAbsenceThreshold, total >= school.EffectiveAbsenceThreshold, items, query.Page, query.PageSize);
    }
    internal static void Validate(int id, string? year, int page, int size)
    {
        if (id <= 0 || page < 1 || page > 100000 || size < 1 || size > 200) throw new RequestValidationException(new Dictionary<string, string[]> { ["query"] = ["Student id must be positive, page >= 1 and pageSize between 1 and 200."] });
        if (year is not null)
        {
            try { _ = SchoolYear.Parse(year); }
            catch (DomainException) { throw new RequestValidationException(new Dictionary<string, string[]> { ["schoolYear"] = ["School year must use YYYY-YYYY with consecutive years."] }); }
        }
    }
}

public sealed class GetChronicAbsenceHandler(IAttendanceRepository repository, IClock clock) : IRequestHandler<GetChronicAbsenceQuery, ChronicAbsenceResult>
{
    public async Task<ChronicAbsenceResult> Handle(GetChronicAbsenceQuery query, CancellationToken cancellationToken)
    {
        GetAttendanceHistoryHandler.Validate(query.StudentId, query.SchoolYear, 1, 1);
        var student = await repository.GetStudentAsync(query.StudentId, cancellationToken) ?? throw new NotFoundException("Student was not found.");
        var school = await repository.GetSchoolAsync(student.SchoolId, cancellationToken) ?? throw new NotFoundException("School was not found.");
        var year = query.SchoolYear is null ? SchoolYear.For(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)) : SchoolYear.Parse(query.SchoolYear);
        var total = (await repository.GetRecordsAsync(student.Id, student.SchoolId, year.StartDate, year.EndDateExclusive, cancellationToken)).Count(x => x.IsAbsent);
        return new(student.Id, year.Value, total, school.EffectiveAbsenceThreshold, total >= school.EffectiveAbsenceThreshold);
    }
}


