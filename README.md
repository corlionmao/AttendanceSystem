# AttendanceSystem .NET 8 API

# Architectural Decision Records (ADR): AttendanceSystem Modernization

This document outlines the 5 key architectural decisions made during the design and migration of the .NET 8 `AttendanceSystem` Web API.

---

## 1. Domain-Driven Design (DDD) & Clean Architecture
* **Decision:** Implement a decoupled 4-layer architecture (`Core.Domain`, `Core.Application`, `Infrastructure`, `WebAPI`) keeping the domain model completely isolated.
* **Justification:** Centralizes complex rules (e.g., calculating *chronic absenteeism*) into a pure, framework-independent core. Aggregates and entities natively enforce domain invariants before state changes hit the persistence layer.

## 2. Legacy Migration: Database-to-Application Refactoring
* **Decision:** Completely eliminate legacy relational stored procedures, migrating their functional responsibility directly into pure C# logic inside the Domain and Application layers.
* **Justification:** Replaces hard-to-maintain database scripts with unit-testable, version-controlled code. Consolidates business definitions into a single source of truth, removing distributed logic vulnerabilities. Scaling stateless .NET 8 Web APIs in containers (Docker/Kubernetes) is lightweight and cost-effective than Scalling SQL DB Compute.

## 3. CQRS Implementation via MediatR Pipeline Handlers
* **Decision:** Separate read and write actions strictly through the Command Query Responsibility Segregation (CQRS) pattern using explicit MediatR handoff mechanics.
* **Justification:** Ensures Single Responsibility Principle (SRP) by isolating use-cases (e.g., `SaveDailyAttendanceCommand`). Enables global cross-cutting policies (Validation, Logging, Transactions) via pipeline behaviors without modifying handler core code.

## 4. Resilience Framework: Centralized Idempotency & Global Error Handling
* **Decision:** Enforce a strict idempotency validation layer for mutating commands coupled with an RFC 7807 compliant global exception filter.
* **Justification:** Prevents double-logging anomalies (e.g., duplicate student attendance entries) caused by intermittent network retries. Normalizes system faults into standard formats while preventing structural stack traces from leaking to API consumers.

## 5. Deployment & Operability Isolation
* **Decision:** Deploy the application inside an isolated directory, configuring EF Core with a standalone local SQLite backend (`students.db`) and open telemetry metrics.
* **Justification:** Guarantees zero runtime dependencies on legacy artifacts, supporting pure Spec-Driven Development (SDD). Integrated observability primitives provide Day-Two operational visibility from the initial deployment window.

---

# Analysis of Legacy Code Gaps & Ambiguity Resolution

This document outlines the structural gaps identified in the legacy assets and the engineering assumptions applied to move the modernization forward.

---

## 1. Identified Legacy Gaps & Immediate Resolution

### Missing Core Schema Tables (`Schools`, `SchoolTerms`, `AttendanceSubmissionLog`)
* **The Ambiguity:** Relational integrity constraints and foreign key targets were missing from the legacy snapshot, hiding critical business rules regarding school operational contexts and submission logging lifetimes.
* **How It Was Handled:** 
  * Modeled placeholder **Value Objects and Entities** inside `Core.Domain` to represent these concepts based on their usage in the stored procedures.
  * Assumed a flat structure for `Schools` and mapped `SchoolTerms` as an invariant boundary (Start/End dates) to ensure attendance calculations fall within valid academic windows.
  * Encapsulated `AttendanceSubmissionLog` directly into an **Infrastructure-level Auditing Concern**, separating it from core domain behavior.

### Incomplete Legacy Form File (frmDailyAttendance.frm)
* **The Ambiguity:** The form file looks incomplete.
* **How It Was Handled:** 
  * Decoupled the API contract completely from the legacy view state. 

---

## 2. Advanced Strategic Assumptions (Potential Blind Spots)

To ensure production readiness, the following architectural blind spots must be validated with stakeholders:

### Multi-Tenancy & Data Isolation (The `Schools` Ambiguity)
* **The Assumption Made:** The system assumes a shared database architecture where a school ID is passed explicitly in the context header or command payload.
* **What Could Be Missed:** If Stakehoders requires hard compliance-based data isolation per school district, this flat approach must be refactored into a **Multi-Tenant DB schema** or independent SQLite file routing per tenant.

### Complex Academic Calendars (`SchoolTerms` Overlaps)
* **The Assumption Made:** A student belongs to one school term at a time with clear, linear chronological windows.
* **What Could Be Missed:** Tracking structural edge cases like rolling enrollment, alternative tracks, mid-term transfers, or overlapping specialized summer terms that invalidate simple date-range lookups.

### Audit Compliance vs. Transactional State (`AttendanceSubmissionLog`)
* **The Assumption Made:** The submission log acts purely as an append-only ledger for telemetry and operational auditing.
* **What Could Be Missed:** The log might have historically driven downstream processes (e.g., payroll triggers, state-funding reporting, or integration messaging). If so, it must be promoted to a **Domain Event / Outbox Pattern** consumer rather than an application-layer side effect.

### Batch Payload Scale & Timeout Risk
* **The Assumption Made:** Batch attendance updates represent localized student cohorts (e.g., 30–50 students per payload) processed synchronously.
* **What Could Be Missed:** If school administrators submit district-wide or school-wide batch payloads (thousands of rows) via a single request, the synchronous approach risks thread starvation and API timeouts. This would necessitate shifting to an **Asynchronous Queue / Background Worker** model.



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
