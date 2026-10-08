# Implementation plan and completion notes

1. Inspect legacy schema, two stored procedures, and VB6 form; record gaps/corrections.
2. Define September school-year and constrained attendance domain; cover boundaries/invariants.
3. Add CQRS contracts/handlers and persistence port; keep EF/HTTP out of domain/use cases.
4. Implement EF SQLite mappings, transactional repository, startup initialization, and synthetic seed.
5. Wire scoped JWT, controllers, persisted 24h idempotency, Problem Details, readiness, Swagger, and OpenTelemetry.
6. Add domain/application/infrastructure/API tests; build and run tests; launch smoke test; verify legacy hashes.

Implementation note: initial SQLite setup uses `EnsureCreated` (not a versioned migration) for fresh installs. See requirements/data-migration for seed, auth scope, missing legacy dependencies, and deferred features.
