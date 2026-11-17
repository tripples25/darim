# React transition implementation plan

Goal: replace the old UI with React, implement open-ended splits and coordinator succession.

Approved scope: open enrollment until coordinator closes collection; current participants share cost equally; coordinator exit promotes earliest remaining participant; last exit releases reservation. Do not fix guest privacy issue #2 or include reliability PR #13.

Architecture: keep ASP.NET API and React as separate deployables. Add backward-compatible split state to DTO and database. Keep existing fixed splits readable and open existing unpurchased splits during migration. Keep Blazor available for rollback until live validation succeeds.

- [x] Add domain regression tests for open enrollment, closure, coordinator succession, last exit, owner privacy, and purchased immutability; observe failures.
- [x] Implement domain, DTO and additive EF migration. Preserve external reservation until the last participant leaves; permit continuing actions after company departure. Database round trip remains unverified (see ledger).
- [x] Update React split actions, participant counts, estimated shares and confirmation text; test action selection and closure.
- [x] Add in-memory Telegram Mini App session, authenticated photo loading, notices, sharing and coordinator management using existing API contracts. Test transport and bearer/CSRF boundaries.
- [x] Audit browser and Mini App scenario coverage; add missing controls consistent with current UI. Live provider and deployment checks remain below.
- [x] Add explicit production API/auth routing and migration/rollback runbook. Keep old UI until production parity is verified.
- [ ] Publish cross-repository documentation and related branches/PRs when GitHub connectivity permits. Validate lint, tests, builds and changed backend code. Record unverified deployment/database steps.

Decisions: closing requires at least two participants. Leaving a closed, unpurchased split reopens collection, so no stale fixed share is presented. Legacy cancel on a split means coordinator exit, preventing older clients from deleting everyone's participation. Legacy participant order was not stored; migrate deterministically, record actual order for new joins.

Constraints: current local main refs may be stale; fetch attempted before publishing. Docker was unavailable at initial inspection. Do not claim live rollout or complete migration without successful verification.

## Execution ledger

2026-11-15: new domain regressions reproduced five failures, then passed after implementation. Backend database-independent suite: 88 passed. Frontend: 12 Node tests passed, lint clean, Vite production build passed. Mobile Playwright fixture flow passed notices, share creation, split closure confirmation, public requests, Mini App launch, memory-only bearer, bearer-protected image and safe area. Browser test caught two UI defects: invisible mobile close control and reused button DOM submitting a new confirmation form. Fixed and covered.

Fresh reviewer found a PostgreSQL expectation still using destructive split cancellation; updated it and added close/join and concurrent-exit races. Updated external reservation regression to assert retention until the last exit. These database tests compile but have NOT run: Docker Desktop started, its Linux engine returns HTTP 500; no PostgreSQL is listening locally. EF model has no pending changes and migration SQL generation passed. Seeded upgrade/rollback and nginx runtime validation remain required before release.

GitHub HTTPS fetch failed. Direct SSH reached GitHub using its independently verified published host key, but local keys are not authorized. GitHub connector can access backend only; it still cannot see private frontend. Backend changed source blobs match current GitHub main, so publishing a draft on top of current remote tree will not overwrite newer code. No production change or old-client deletion performed.
