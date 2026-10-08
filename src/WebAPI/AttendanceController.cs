using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AttendanceSystem.Core.Application;
using AttendanceSystem.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceSystem.WebAPI;

public sealed record AttendanceEntryRequest(int StudentId, string Code, int? MinutesLate, string? Notes);
public sealed record SaveAttendanceRequest(DateOnly AttendanceDate, IReadOnlyList<AttendanceEntryRequest> Entries);

[ApiController]
[Route("api")]
public sealed class AttendanceController(IMediator mediator, ApplicationDbContext db, IClock clock) : ControllerBase
{
    [HttpPost("schools/{schoolId:int}/attendance")]
    [Authorize]
    [ProducesResponseType(typeof(SaveAttendanceResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(int schoolId, [FromBody] SaveAttendanceRequest request, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        EnsureSchoolScope(schoolId);
        var caller = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "unknown";
        var endpoint = $"POST:/api/schools/{schoolId}/attendance";
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) throw new RequestValidationException(new Dictionary<string, string[]> { ["Idempotency-Key"] = ["A key of 1-200 characters is required."] });
        if (request.Entries is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["entries"] = ["At least one attendance entry is required."] });
        var command = new SaveDailyAttendanceCommand(schoolId, request.AttendanceDate, request.Entries.Select(x => new AttendanceEntryInput(x.StudentId, x.Code, x.MinutesLate, x.Notes)).ToArray(), caller);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        var now = clock.UtcNow;
        var existing = await db.IdempotencyEntries.SingleOrDefaultAsync(x => x.Caller == caller && x.Endpoint == endpoint && x.SchoolId == schoolId && x.Key == key, ct);
        if (existing is { ExpiresAt: var expiry } && expiry <= now)
        { db.IdempotencyEntries.Remove(existing); await db.SaveChangesAsync(ct); existing = null; }
        if (existing is not null)
        {
            if (existing.RequestHash != hash) throw new ConflictException("Idempotency-Key was already used with a different request.");
            for (var i = 0; existing.ResponseJson is null && i < 100; i++) { await Task.Delay(100, ct); await db.Entry(existing).ReloadAsync(ct); }
            if (existing.ResponseJson is null) throw new ConflictException("The request with this Idempotency-Key is still processing; retry shortly.");
            var replay = JsonSerializer.Deserialize<SaveAttendanceResult>(existing.ResponseJson)!;
            Response.Headers["Idempotency-Replayed"] = "true";
            return StatusCode(existing.StatusCode, replay);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entry = new IdempotencyEntry { Caller = caller, Endpoint = endpoint, SchoolId = schoolId, Key = key, RequestHash = hash, StatusCode = 200, CreatedAt = now, ExpiresAt = now.AddHours(24) };
        db.IdempotencyEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
            var result = await mediator.Send(command, ct);
            entry.ResponseJson = JsonSerializer.Serialize(result);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Ok(result);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            existing = await db.IdempotencyEntries.SingleOrDefaultAsync(x => x.Caller == caller && x.Endpoint == endpoint && x.SchoolId == schoolId && x.Key == key, ct);
            if (existing is null) throw new ConflictException("The idempotency reservation conflicted; retry the same request.");
            if (existing.RequestHash != hash) throw new ConflictException("Idempotency-Key was already used with a different request.");
            if (existing.ResponseJson is null) throw new ConflictException("The request with this Idempotency-Key is still processing; retry shortly.");
            Response.Headers["Idempotency-Replayed"] = "true";
            return StatusCode(existing.StatusCode, JsonSerializer.Deserialize<SaveAttendanceResult>(existing.ResponseJson));
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw;
        }
    }
    [HttpGet("students/{studentId:int}/attendance")]
    [Authorize]
    public async Task<AttendanceHistoryResult> History(int studentId, [FromQuery] string? schoolYear, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        await EnsureStudentScope(studentId, ct);
        return await mediator.Send(new GetAttendanceHistoryQuery(studentId, schoolYear, page, pageSize), ct);
    }

    [HttpGet("students/{studentId:int}/attendance/chronic-absence")]
    [Authorize]
    public async Task<ChronicAbsenceResult> ChronicAbsence(int studentId, [FromQuery] string? schoolYear, CancellationToken ct)
    {
        await EnsureStudentScope(studentId, ct);
        return await mediator.Send(new GetChronicAbsenceQuery(studentId, schoolYear), ct);
    }

    private void EnsureSchoolScope(int schoolId)
    {
        if (!User.FindAll("school_id").Any(c => int.TryParse(c.Value, out var id) && id == schoolId)) throw new UnauthorizedAccessException("Caller is not authorized for this school.");
    }
    private async Task EnsureStudentScope(int studentId, CancellationToken ct)
    {
        var student = await db.Students.AsNoTracking().Where(x => x.Id == studentId).Select(x => new { x.Id, x.SchoolId }).SingleOrDefaultAsync(ct);
        if (student is null) throw new NotFoundException("Student was not found.");
        var explicitStudent = User.FindAll("student_id").Any(c => c.Value == student.Id.ToString());
        var schoolScope = User.FindAll("school_id").Any(c => c.Value == student.SchoolId.ToString());
        if (!explicitStudent && !schoolScope) throw new UnauthorizedAccessException("Caller is not authorized for this student.");
    }
}

[ApiController]
[Route("api/auth")]
public sealed class DemoAuthController(IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost("demo-token")]
    [AllowAnonymous]
    public IActionResult DemoToken([FromQuery] int schoolId)
    {
        if (!environment.IsDevelopment()) return NotFound();
        if (schoolId <= 0) return BadRequest();
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"]!));
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(configuration["Jwt:Issuer"], configuration["Jwt:Audience"], [new System.Security.Claims.Claim("sub", "demo-user"), new System.Security.Claims.Claim("school_id", schoolId.ToString())], expires: DateTime.UtcNow.AddHours(1), signingCredentials: credentials);
        return Ok(new { accessToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token), tokenType = "Bearer", expiresIn = 3600 });
    }
}

