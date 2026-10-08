## Prerequisites and run

Install a .NET 8 SDK. From this folder run:

```powershell
dotnet restore AttendanceSystem.sln
dotnet build AttendanceSystem.sln
dotnet test AttendanceSystem.sln
cd src\WebAPI
dotnet run
```

The app listens on the URL printed by `dotnet run`; Swagger UI is `/swagger` in Development. SQLite file is `src\WebAPI\students.db` when launched from that project directory. Startup creates its schema and seeds synthetic data only in Development. `/health/live` is liveness; `/health/ready` verifies SQLite connectivity.

## Authentication
Development only: `POST /api/auth/demo-token?schoolId=1` returns a one-hour JWT scoped to demo school 1. The checked-in development signing key is intentionally public/test-only and must never be used beyond local development. Production must set `Jwt__SigningKey` (at least 32 UTF-8 bytes), `Jwt__Issuer`, `Jwt__Audience`, a secure external issuer/secret store, and issue narrow `school_id`/`student_id` claims; disable `SeedDemoData`. Never deploy the demo-token endpoint/key.

## API quick example
Obtain a local token, then:

```powershell
$token = (Invoke-RestMethod -Method Post 'http://localhost:5000/api/auth/demo-token?schoolId=1').accessToken
$body = @{ attendanceDate='2026-10-07'; entries=@(@{ studentId=1001; code='A'; minutesLate=0; notes='' }) } | ConvertTo-Json -Depth 5
Invoke-RestMethod -Method Post -Uri 'http://localhost:5000/api/schools/1/attendance' -Headers @{ Authorization="Bearer $token"; 'Idempotency-Key'=[guid]::NewGuid().ToString() } -ContentType 'application/json' -Body $body
Invoke-RestMethod -Headers @{ Authorization="Bearer $token" } 'http://localhost:5000/api/students/1001/attendance'
Invoke-RestMethod -Headers @{ Authorization="Bearer $token" } 'http://localhost:5000/api/students/1001/attendance/chronic-absence'
```

Change the URL to the actual Kestrel URL. Logs are written to the console; attendance notes/student identity are excluded. OpenTelemetry instruments ASP.NET Core, HTTP, and EF Core activity. For local traces, start an OTLP-compatible collector/viewer such as Jaeger, set `$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"` before `dotnet run`, then inspect the `AttendanceSystem.WebAPI` service in that viewer. The exporter is optional; without the variable, the API runs normally without an external service. API behavior and gaps are fully described in `specs/`.
