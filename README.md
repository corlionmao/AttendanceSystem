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

This document summarizes the structural gaps discovered in the legacy system and the precise engineering decisions applied to deliver a production-ready, .NET 8 Web API.

---

### 1. Schema Gaps & Data Initialization
* **Identified Gaps:** `schema.sql` omits `Schools`, `SchoolTerms`, and `AttendanceSubmissionLog`. The roster query, grade filters, and `sp_GetStudentsForAttendance` are missing entirely.
* **Resolution:** Out-of-slice capabilities (roster loading, reporting, terms management) are excluded. In `Development`, the database uses EF Core `EnsureCreated()` to bootstrap schema rules and automatically seeds **Demo School 1**, synthetic students (**IDs 1001/1002**), status codes (**P/A/EA/T**), and one active term. Production environments must use versioned migrations.

### 2. Business Logic Refactoring & Correction
* **Identified Gaps:** Legacy code relies on an undefined `SchoolYear()` function and introduces an aggregation bug by evaluating the user-requested target date within its absence counters instead of checking individual record dates.
* **Resolution:** Replaced relational stored procedures with C# logic. The modern backend recalculates all statistics and metrics on save directly from attendance rows to eliminate stale state data, evaluating absence history inside a strict calendar window of `[Sep 1, next Sep 1)`.

### 3. Pipeline Safeguards & Domain Invariants
* **Identified Gaps:** Legacy processes handle input via unsafe cursor loops (last-write-wins) and unescaped raw XML concatenation without verifying profile or code validity.
* **Resolution:** Replaced the legacy pipeline with clear, structured JSON DTO payloads. The API strictly rejects payloads containing duplicate student IDs within the same batch, inactive profiles, or invalid codes. A system absenteeism threshold fallback of **10** is automatically applied if left unset.

### 4. Idempotency & Distributed Concurrency
* **Identified Gaps:** The legacy UI captures zero execution context or actor identity, risking duplicate database entries under intermittent network drops.
* **Resolution:** Designed a 3-axis stateful tracking key scoped by **Subject**, **Endpoint**, and **School ID**. Successful responses cache for **24 hours**, while failed attempts release immediately for retry. Concurrent duplicate requests block and wait for up to **10 seconds** before gracefully throwing a `409 Conflict` error.

### 5. Authentication & Context Boundaries
* **Identified Gaps:** The old layer logs events using a generic DB `system-user` session without true identity mapping.
* **Resolution:** Audit actors map to the authenticated JWT `subject` claim (falling back to `demo-user` in Dev). School claims grant explicit visibility into matching student cohorts. Production rules dictate issuing narrower `student_id` scopes to enforce tight policy isolation.


