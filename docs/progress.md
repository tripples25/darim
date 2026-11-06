# Implementation ledger — plan: docs/plan.md

Setup: empty /workspace, no existing repository or .NET SDK. Initialised feature/wishlist; installing .NET 10 SDK. Docker available.
Pre-flight: shared API DTOs consumed by server and Blazor; gift domain transitions consumed by PostgreSQL service. Owner-safe projection is the single read boundary for both interfaces.
Ruling: build in /workspace/wishlist on a new feature branch — there is no existing code or user branch to protect.
Ruling: one company per account for v1, RUB prices, no cancellation after purchase — minimum complete workflow matching the approved scope.
Task 1: RED — 22 domain tests failed against explicit unimplemented rules; implementing transitions and owner-safe projections. Nullable slot validation requires GetValueOrDefault because the compiler cannot infer the throwing guard's postcondition.
Tasks 1–3: domain/auth/DB/API implementation complete; 49/49 xUnit and bUnit checks passed, including PostgreSQL races and CSRF over HTTPS. Migrations generated and tested on PostgreSQL 18.
Ruling: use current patched test dependencies (bUnit 2.13.3, Testcontainers 4.16.0) and MudBlazor 9.12.0 — earlier selected test packages pulled vulnerable dependencies.
Task 4: responsive Blazor UI implemented; browser flows in progress. Additional owner-contact privacy regression failed as expected; public gift owner projections now strip contacts, while split participants retain them.
Task 5: Compose, explicit migrations, HTTPS proxy, non-root runtime, off-server backup/restore, CI and Russian operating guide added. External provider rental and actual Telegram-client verification require operator credentials/domain and remain deployment steps, not fabricated successes.
Final review (independent reviewer): three Important findings verified and fixed: Telegram OIDC profile id must match Mini App user.id (sub differs); polling must refresh membership before split eligibility; split participants need an explicit private coordinator ID and label. Regression checks observed RED then GREEN. Suite: 56/56 passed.
Final: backup retention listing failure regraded Important because the operating contract requires failure reporting for the 14-day policy; captured the listing in a separately checked command. Regression observed RED then GREEN; 3/3 backup-script checks passed.
Final: no deferred code-review findings.
Ruling: operator-only checks (real Telegram clients, DNS/TLS, S3 credentials and rental) remain documented deployment checks — no credentials/domain were supplied; claiming live verification would be false.
Ruling: cancelled reservations disappear from current donor activity, while archived reserved/purchased gifts retain history — cancellation audit and organizer succession/removal are outside v1; those would require a later feature.
Verification: all 6 Playwright browser flows passed before review fixes, including signed Mini App bearer login, dark theme and mobile overflow. Re-running against optimized published assets after fixes.
Verification: local pg_dump/pg_restore rehearsal recovered 4 users and 8 gifts into a separate database. Full multi-stage Docker build exhausted this environment's 8GB filesystem while extracting the SDK image; temporary build cache was pruned. Compose configuration validated. Host .NET release publish succeeds; container image build remains to be verified on a machine with adequate disk.

Final verification: 56/56 .NET tests, 3/3 backup-script tests, 6/6 Playwright flows against optimized Release-published assets passed. Release publish exit 0; Compose configuration and shell syntax checks passed; git diff --check clean.
Final: hosted asset publication regression observed in the published browser run; client now uses hosted/default static assets and stable loader URL, served through ASP.NET Core MapStaticAssets. Removed the legacy UseBlazorFrameworkFiles branch which bypassed the selected static endpoint. Full published browser suite is the regression check and now passes.
Final: cleaned only generated browser-test wishes/notices from the local demo database; original eight sample gifts retained. Keeping feature/wishlist locally; no remote configured, no merge/push/deploy performed.
