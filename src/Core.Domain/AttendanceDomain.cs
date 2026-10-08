using System.Text.RegularExpressions;

namespace AttendanceSystem.Core.Domain;

public sealed class DomainException(string message) : Exception(message);

public readonly record struct SchoolYear
{
    public int StartYear { get; }
    public int EndYear => StartYear + 1;
    public DateOnly StartDate => new(StartYear, 9, 1);
    public DateOnly EndDateExclusive => new(EndYear, 9, 1);
    public string Value => $"{StartYear}-{EndYear}";
    private SchoolYear(int year) => StartYear = year;
    public static SchoolYear For(DateOnly date) => new(date.Month >= 9 ? date.Year : date.Year - 1);
    public static SchoolYear Parse(string value)
    {
        var match = Regex.Match(value ?? string.Empty, "^(\\d{4})-(\\d{4})$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var start) || !int.TryParse(match.Groups[2].Value, out var end) || start < 1 || end != start + 1)
            throw new DomainException("School year must use YYYY-YYYY with consecutive years.");
        return new SchoolYear(start);
    }
    public bool Contains(DateOnly date) => date >= StartDate && date < EndDateExclusive;
    public override string ToString() => Value;
}

public sealed class School
{
    private School() { }
    public School(int id, string name, int? absenceAlertThreshold = null)
    {
        if (id <= 0) throw new DomainException("School identifier must be positive.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("School name is required.");
        if (absenceAlertThreshold < 0) throw new DomainException("Absence threshold cannot be negative.");
        Id = id; Name = name.Trim(); AbsenceAlertThreshold = absenceAlertThreshold;
    }
    public int Id { get; private set; }
    public string Name { get; private set; } = "";
    public int? AbsenceAlertThreshold { get; private set; }
    public int EffectiveAbsenceThreshold => AbsenceAlertThreshold ?? 10;
}

public sealed class Student
{
    private Student() { }
    public Student(int id, int schoolId, string firstName, string lastName, string? grade = null, bool active = true)
    {
        if (id <= 0 || schoolId <= 0) throw new DomainException("Student and school identifiers must be positive.");
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName)) throw new DomainException("Student name is required.");
        Id = id; SchoolId = schoolId; FirstName = firstName.Trim(); LastName = lastName.Trim(); Grade = grade; Active = active;
    }
    public int Id { get; private set; }
    public int SchoolId { get; private set; }
    public string FirstName { get; private set; } = "";
    public string LastName { get; private set; } = "";
    public string? Grade { get; private set; }
    public bool Active { get; private set; }
}

public sealed class AttendanceCode
{
    private AttendanceCode() { }
    public AttendanceCode(string value, string description, bool isAbsent, bool isExcused, bool isActive = true)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 5) throw new DomainException("Attendance code must contain 1-5 characters.");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("Attendance code description is required.");
        Value = value.Trim().ToUpperInvariant(); Description = description.Trim(); IsAbsent = isAbsent; IsExcused = isExcused; IsActive = isActive;
    }
    public string Value { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool IsAbsent { get; private set; }
    public bool IsExcused { get; private set; }
    public bool IsActive { get; private set; }
}

public sealed class SchoolTerm
{
    private SchoolTerm() { }
    public SchoolTerm(int id, int schoolId, string name, DateOnly startDate, DateOnly endDate)
    {
        if (id <= 0 || schoolId <= 0 || string.IsNullOrWhiteSpace(name) || endDate < startDate) throw new DomainException("Invalid school term.");
        Id = id; SchoolId = schoolId; Name = name.Trim(); StartDate = startDate; EndDate = endDate;
    }
    public int Id { get; private set; }
    public int SchoolId { get; private set; }
    public string Name { get; private set; } = "";
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public bool Includes(DateOnly date) => date >= StartDate && date <= EndDate;
}

public sealed class AttendanceRecord
{
    private AttendanceRecord() { }
    public AttendanceRecord(int studentId, int schoolId, DateOnly date, AttendanceCode code, int? minutesLate, string? notes, int? termId, string? actor, DateTimeOffset now)
    {
        if (studentId <= 0 || schoolId <= 0 || date == default) throw new DomainException("Attendance identifiers and date must be valid.");
        StudentId = studentId; SchoolId = schoolId; Date = date; CreatedAt = now; CreatedBy = actor;
        Update(code, minutesLate, notes, termId, actor, now);
        ModifiedAt = null; ModifiedBy = null;
    }
    public int Id { get; private set; }
    public int StudentId { get; private set; }
    public int SchoolId { get; private set; }
    public DateOnly Date { get; private set; }
    public string AttendanceCode { get; private set; } = "";
    public bool IsAbsent { get; private set; }
    public bool IsExcused { get; private set; }
    public int? MinutesLate { get; private set; }
    public string? Notes { get; private set; }
    public int? TermId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? CreatedBy { get; private set; }
    public DateTimeOffset? ModifiedAt { get; private set; }
    public string? ModifiedBy { get; private set; }
    public void Update(AttendanceCode code, int? minutesLate, string? notes, int? termId, string? actor, DateTimeOffset now)
    {
        if (!code.IsActive) throw new DomainException("Attendance code is inactive.");
        if (minutesLate < 0) throw new DomainException("Minutes late cannot be negative.");
        if (notes?.Length > 500) throw new DomainException("Notes cannot exceed 500 characters.");
        AttendanceCode = code.Value; IsAbsent = code.IsAbsent; IsExcused = code.IsExcused;
        MinutesLate = minutesLate; Notes = notes; TermId = termId; ModifiedAt = now; ModifiedBy = actor;
    }
}

public sealed class AbsenceSummary
{
    private AbsenceSummary() { }
    public AbsenceSummary(int studentId, int schoolId, SchoolYear schoolYear, int totalAbsences, DateTimeOffset updatedAt)
    { if (studentId <= 0 || schoolId <= 0 || totalAbsences < 0) throw new DomainException("Invalid absence summary."); StudentId = studentId; SchoolId = schoolId; SchoolYear = schoolYear.Value; TotalAbsences = totalAbsences; UpdatedAt = updatedAt; }
    public int Id { get; private set; }
    public int StudentId { get; private set; }
    public int SchoolId { get; private set; }
    public string SchoolYear { get; private set; } = "";
    public int TotalAbsences { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public bool IsChronic(int threshold) => TotalAbsences >= threshold;
    public void Recalculate(int count, DateTimeOffset at) { if (count < 0) throw new DomainException("Absence count cannot be negative."); TotalAbsences = count; UpdatedAt = at; }
}

public sealed class ChronicAbsenceAlert
{
    private ChronicAbsenceAlert() { }
    public ChronicAbsenceAlert(int studentId, int schoolId, SchoolYear year, DateTimeOffset date, string message)
    { StudentId = studentId; SchoolId = schoolId; SchoolYear = year.Value; AlertDate = date; Message = message; }
    public int Id { get; private set; }
    public int StudentId { get; private set; }
    public int SchoolId { get; private set; }
    public string SchoolYear { get; private set; } = "";
    public DateTimeOffset AlertDate { get; private set; }
    public string Message { get; private set; } = "";
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolvedBy { get; private set; }
    public bool IsResolved => ResolvedAt.HasValue;
    public void Resolve(DateTimeOffset at, string actor) { ResolvedAt = at; ResolvedBy = actor; }
}

public sealed class AttendanceSubmission
{
    private AttendanceSubmission() { }
    public AttendanceSubmission(int schoolId, DateOnly attendanceDate, int recordCount, DateTimeOffset submittedAt, string? actor)
    { SchoolId = schoolId; AttendanceDate = attendanceDate; RecordCount = recordCount; SubmittedAt = submittedAt; Actor = actor; }
    public int Id { get; private set; }
    public int SchoolId { get; private set; }
    public DateOnly AttendanceDate { get; private set; }
    public int RecordCount { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public string? Actor { get; private set; }
}
