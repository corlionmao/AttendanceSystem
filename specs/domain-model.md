# Domain model

- `School` owns configuration (optional chronic absence threshold; effective default 10); `Student` is school-owned; `AttendanceCode` is shared metadata with explicit active/absent/excused flags; `SchoolTerm` belongs to a school.
- `AttendanceRecord` is the daily fact; one record per `(StudentId, Date)`, with school ownership, code, classification snapshot, optional lateness/notes/term, and creation/modification actor/time. Code metadata, never code spelling, defines absence/excused behavior.
- `AbsenceSummary` is a maintained `(StudentId, SchoolYear)` projection; `ChronicAbsenceAlert` records `(StudentId, SchoolId, SchoolYear)` chronic state; a filtered unique index limits one unresolved alert. `AttendanceSubmission` captures batch audit.
- Identifiers are integer legacy-compatible IDs, except code value (string) and school year (`YYYY-YYYY`). Private setters/factories/methods guard entity state.
- `SchoolYear.For(date)` maps Sep-Dec to that year and Jan-Aug to prior year. Date interval is inclusive Sep 1 and exclusive next Sep 1.
- Batch duplicate student IDs are rejected. The use case runs in a relational transaction; unique constraints remain the race-safe second line.
