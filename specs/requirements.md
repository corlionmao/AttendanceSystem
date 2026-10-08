# Requirements and acceptance criteria

## Confirmed legacy behavior
- The VB6 form selects school/grade/date, loads students, captures attendance code/minutes late/notes, submits one school/date batch, and prints a Crystal Report.
- SQL intends to upsert by student/date, derive absent/excused from code metadata, associate an applicable term, recalculate yearly totals, maintain summary/alert state, and log batch submissions.
- History is newest-first by school year and includes code description, absence flags, lateness, notes, term; chronic status compares absence count against school threshold.
- School years begin in September; schema limits notes to 500 characters and defaults alert threshold to 10.

## Required modern slice
1. Save nonempty attendance batches atomically, validating school, active student/code, uniqueness per student in batch, ownership, date, lateness, and note length.
2. Upsert by student/date; derive classification exclusively from code definitions; associate matching term; recalculate exact yearly count and maintain summary/one open chronic alert; record submission audit.
3. Return history and chronic status for the requested/default school year, with history paging.
4. Persist SQLite state, idempotency, and audit; secure every student operation with scoped JWT; expose safe validation/not-found/conflict/Problem Details and readiness checks.
5. Preserve legacy files byte-for-byte; all new artifacts live beside the legacy project.

## Gaps and deliberate assumptions/corrections
- `schema.sql` omits `Schools`, `SchoolTerms`, and `AttendanceSubmissionLog`, all referenced by procedures; `sp_GetStudentsForAttendance` and its grade/date semantics are missing. This slice does not implement a student-roster query or grade filter.
- VB6 config connection string points to `config.ini` as a SQL Server data source, XML is concatenated without escaping, the report implementation is external, and UI records no authenticated actor. None is a portable API contract.
- The save SQL uses an undefined `SchoolYear()` and compares the requested date inside its absence count rather than each record date. Implementation explicitly counts records in `[Sep 1, next Sep 1)`.
- Duplicate student identifiers within one batch are rejected (rather than cursor-order last-write-wins); inactive students/codes are rejected; the default threshold 10 is used only when unset.
- Summary is persisted and synchronized on save; status/history counts are recalculated from attendance rows to avoid stale summaries.
- Demo school 1 and synthetic students (fresh database IDs 1001/1002), codes P/A/EA/T, and one current-school-year term are seeded only when missing in Development. A school claim authorizes its school’s students; deployments should issue narrower `student_id` claims as required by policy.
- SQLite startup uses `EnsureCreated` as reliable schema initialization; no checked-in migration snapshot is supplied. Use additive versioned EF migrations for production schema evolution.
- Idempotency keys are scoped by subject, endpoint, and school; successful responses persist for 24 hours. Failed operations release reservation for retry; pending concurrent duplicate requests wait up to 10 seconds then receive 409.
- Audit actor is authenticated subject; demo actor is `demo-user`. The existing SQL's system-user identity has no direct JWT equivalent.
- Out-of-slice legacy reporting, roster loading, terms management, alert resolution, and historical data import are not implemented.
