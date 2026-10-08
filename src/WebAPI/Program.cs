using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AttendanceSystem.Core.Application;
using AttendanceSystem.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => { o.SwaggerDoc("v1", new() { Title = "AttendanceSystem API", Version = "v1" }); o.AddSecurityDefinition("Bearer", new() { Name = "Authorization", Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = Microsoft.OpenApi.Models.ParameterLocation.Header }); o.AddSecurityRequirement(new() { [new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() }); });
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(SaveDailyAttendanceCommand).Assembly));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Attendance") ?? "Data Source=students.db", sqlite => sqlite.CommandTimeout(30)));
builder.Services.AddScoped<IAttendanceRepository, EfAttendanceRepository>();
builder.Services.AddHealthChecks().AddCheck<SqliteHealthCheck>("sqlite", tags: ["ready"]);
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("AttendanceSystem.WebAPI")).WithTracing(t =>
{
    t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddEntityFrameworkCoreInstrumentation();
    if (Uri.TryCreate(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"], UriKind.Absolute, out var otlpEndpoint)) t.AddOtlpExporter(o => o.Endpoint = otlpEndpoint);
});
var signingKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be configured with at least 32 bytes.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"], ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"], ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = ClaimTypes.NameIdentifier };
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages(async status =>
{
    var context = status.HttpContext;
    context.Response.ContentType = "application/problem+json";
    var problem = new ProblemDetails { Status = context.Response.StatusCode, Title = context.Response.StatusCode == 401 ? "Unauthorized" : "Request failed", Detail = "The request could not be completed." };
    problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
});
app.Use(async (context, next) => { var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier; context.Response.Headers["X-Correlation-ID"] = traceId; using (app.Logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = traceId })) await next(); });
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); app.MapHealthChecks("/health/live"); app.MapHealthChecks("/health/ready", new() { Predicate = _ => true });

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.EnsureCreatedAsync();
    if (app.Configuration.GetValue("SeedDemoData", app.Environment.IsDevelopment())) await DemoSeed.SeedAsync(db);
}
await app.RunAsync();

public partial class Program { }

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch { RequestValidationException => 400, NotFoundException => 404, UnauthorizedAccessException => 403, ConflictException => 409, _ => 500 };
        if (status >= 500) logger.LogError(exception, "Unhandled request failure. TraceId={TraceId}", Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);
        else logger.LogInformation("Request rejected with status {StatusCode}. TraceId={TraceId}", status, Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);
        context.Response.StatusCode = status;
        var detail = status >= 500 ? "An unexpected error occurred." : exception.Message;
        var problem = new ProblemDetails { Status = status, Title = status switch { 400 => "Validation failed", 404 => "Not found", 403 => "Forbidden", 409 => "Conflict", _ => "Server error" }, Detail = detail, Instance = context.Request.Path };
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        if (exception is RequestValidationException validation) problem.Extensions["errors"] = validation.Errors;
        context.Response.ContentType = "application/problem+json";
        return await problemDetails.TryWriteAsync(new() { HttpContext = context, ProblemDetails = problem });
    }
}

public sealed class SqliteHealthCheck(ApplicationDbContext db) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default)
    { try { return await db.Database.CanConnectAsync(cancellationToken) ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("SQLite is reachable") : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("SQLite is unavailable"); } catch { return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("SQLite is unavailable"); } }
}


