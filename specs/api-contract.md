# HTTP API contract

All attendance routes require `Authorization: Bearer <JWT>`. JWT must contain matching `school_id` or matching `student_id` scope. POST additionally requires `school_id` for its route school. Dev token endpoint is available only in Development.

## Save attendance
`POST /api/schools/{schoolId}/attendance`
Headers: `Idempotency-Key` (required, 1-200 chars; caller/endpoint/school scoped; same body replays original response; different body is 409; retained 24h).
Body: `{ "attendanceDate":"2026-10-07", "entries":[{"studentId":1001,"code":"A","minutesLate":0,"notes":""}] }`.
Success 200: `{ "createdCount":1,"updatedCount":0,"students":[{"studentId":1001,"totalAbsences":1,"threshold":10,"isChronic":false}],"submittedAt":"..." }`.

## History
`GET /api/students/{studentId}/attendance?schoolYear=2026-2027&page=1&pageSize=50`
School year is optional (current year default); page starts at 1; pageSize defaults 50, max 200; date descending, then ID descending. Returns `{studentId,schoolYear,totalAbsences,threshold,isChronic,records:[{date,code,codeDescription,isAbsent,isExcused,minutesLate,notes,termName}],page,pageSize}`.

## Chronic status
`GET /api/students/{studentId}/attendance/chronic-absence?schoolYear=2026-2027`
Returns `{studentId,schoolYear,totalAbsences,threshold,isChronic}` using source attendance rows, not cached summary.

400 validation (Problem Details with `errors`), 401 unauthenticated, 403 scope denied, 404 missing student/school, 409 idempotency/uniqueness conflict, 500 generic non-sensitive problem. Problem Details includes traceId; `X-Correlation-ID` echoes trace ID. Swagger is development-only.
