# Data migration and persistence

- Store at `Data Source=students.db` relative to the process working directory. The startup initializer creates schema and seeds only missing demo reference entities in Development. Do not commit database files.
- EF Core SQLite mappings include school/student/code/term relationships, notes/code limits, attendance `(StudentId, Date)` unique index, history indexes, unique student/year summary, one unresolved chronic alert, audit submissions, and persisted idempotency.
- Legacy SQL Server tables/procedures are behavior references only; stored-procedure SQL is not copied. No automated import of the legacy SQL Server database is provided because no source connection/complete schema was supplied.
- Attendance date range is `[Sep 1, Sep 1 next year)`; yearly totals count each matching attendance row's own date and `IsAbsent` value.
- `EnsureCreated` is for this initial slice and fresh local installs. Production evolution must use versioned EF Core migrations and backup/restore controls.
