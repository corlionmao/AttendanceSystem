# AttendanceSystem constitution

- Keep the Domain independent of persistence, transport, and framework concerns. Application owns use cases and repository ports; Infrastructure adapts EF Core/SQLite; WebAPI maps HTTP/auth concerns only.
- Attendance submission is an atomic application operation. Student/date uniqueness is enforced in both application behavior and SQLite.
- Authorization is deny-by-default and scoped to school claims (or explicit student claims); JWT validity by itself grants no attendance access.
- Treat attendance notes and student identity as sensitive. Do not place them in logs, traces, or Problem Details.
- Use the September 1 through following September 1 (exclusive) school-year interval everywhere.
- Make assumptions and corrections explicit; do not reproduce legacy SQL or its bugs.
- Tests use deterministic domain clocks and isolated SQLite databases. Demo data is synthetic and opt-in outside Development.
