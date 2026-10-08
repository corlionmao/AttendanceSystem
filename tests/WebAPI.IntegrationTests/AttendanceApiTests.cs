using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Infrastructure;
using AttendanceSystem.Core.Application;

namespace AttendanceSystem.WebAPI.IntegrationTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = Path.Combine(AppContext.BaseDirectory, $"attendance-{Guid.NewGuid():N}.db");
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Attendance"] = $"Data Source={DatabasePath};Default Timeout=15",
            ["SeedDemoData"] = "true",
            ["Jwt:SigningKey"] = "DEV-ONLY-change-this-signing-key-before-use-2026!"
        }));
    }
    protected override void Dispose(bool disposing)
    { base.Dispose(disposing); if (disposing) { foreach (var suffix in new[] { "", "-shm", "-wal" }) try { File.Delete(DatabasePath + suffix); } catch (IOException) { } } }
}

public class AttendanceApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Endpoints_require_authentication_and_replay_or_conflict_on_idempotency_key()
    {
        using var client = factory.CreateClient();
        var unauthorized = await client.GetAsync("/api/students/1001/attendance");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains("application/problem+json", unauthorized.Content.Headers.ContentType!.MediaType);
        var token = await client.PostAsync("/api/auth/demo-token?schoolId=1", null);
        token.EnsureSuccessStatusCode();
        var tokenBody = await token.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenBody!.AccessToken);
        var json = "{\"attendanceDate\":\"2026-10-07\",\"entries\":[{\"studentId\":1001,\"code\":\"A\",\"minutesLate\":0,\"notes\":\"\"}]}";
        using var first = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        first.Headers.Add("Idempotency-Key", "test-save-key");
        var initial = await client.PostAsync("/api/schools/1/attendance", first);
        Assert.True(initial.StatusCode == HttpStatusCode.OK, $"{initial.StatusCode} {initial.Headers.WwwAuthenticate} {await initial.Content.ReadAsStringAsync()}");
        using var replayContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json"); replayContent.Headers.Add("Idempotency-Key", "test-save-key");
        var replay = await client.PostAsync("/api/schools/1/attendance", replayContent);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotency-Replayed"));
        using var conflictContent = new StringContent(json.Replace("\"A\"", "\"P\""), System.Text.Encoding.UTF8, "application/json"); conflictContent.Headers.Add("Idempotency-Key", "test-save-key");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/schools/1/attendance", conflictContent)).StatusCode);
        using var parallelOne = new StringContent(json, System.Text.Encoding.UTF8, "application/json"); parallelOne.Headers.Add("Idempotency-Key", "parallel-save-key");
        using var parallelTwo = new StringContent(json, System.Text.Encoding.UTF8, "application/json"); parallelTwo.Headers.Add("Idempotency-Key", "parallel-save-key");
        var parallelResults = await Task.WhenAll(client.PostAsync("/api/schools/1/attendance", parallelOne), client.PostAsync("/api/schools/1/attendance", parallelTwo));
        Assert.All(parallelResults, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Contains(parallelResults, response => response.Headers.Contains("Idempotency-Replayed"));
        var otherSchoolToken = await client.PostAsync("/api/auth/demo-token?schoolId=42", null);
        var otherToken = await otherSchoolToken.Content.ReadFromJsonAsync<TokenResponse>();
        using var otherClient = factory.CreateClient(); otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken!.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherClient.GetAsync("/api/students/1001/attendance")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stored = await db.IdempotencyEntries.SingleAsync(x => x.Key == "test-save-key");
            stored.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        using var expiredKeyBody = new StringContent(json.Replace("\"A\"", "\"P\""), System.Text.Encoding.UTF8, "application/json"); expiredKeyBody.Headers.Add("Idempotency-Key", "test-save-key");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/schools/1/attendance", expiredKeyBody)).StatusCode);
        using var invalidBody = new StringContent("{\"attendanceDate\":\"2026-10-07\",\"entries\":[]}", System.Text.Encoding.UTF8, "application/json"); invalidBody.Headers.Add("Idempotency-Key", "invalid-batch-key");
        var invalidResponse = await client.PostAsync("/api/schools/1/attendance", invalidBody);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Contains("application/problem+json", invalidResponse.Content.Headers.ContentType!.MediaType);
        using var missingDateBody = new StringContent("{\"entries\":[{\"studentId\":1001,\"code\":\"P\"}]}", System.Text.Encoding.UTF8, "application/json"); missingDateBody.Headers.Add("Idempotency-Key", "missing-date-key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/schools/1/attendance", missingDateBody)).StatusCode);
        var history = await client.GetAsync("/api/students/1001/attendance?schoolYear=2026-2027");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyBody = await history.Content.ReadFromJsonAsync<AttendanceHistoryResult>();
        Assert.Single(historyBody!.Records);
        Assert.Equal("P", historyBody.Records[0].Code);
        var chronic = await client.GetFromJsonAsync<ChronicAbsenceResult>("/api/students/1001/attendance/chronic-absence?schoolYear=2026-2027");
        Assert.Equal(0, chronic!.TotalAbsences);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    private sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);
}



