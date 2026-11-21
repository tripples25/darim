# Ohmywishes automatic following implementation plan

> Execute inline with superpowers:executing-plans and test-driven-development.

Goal: automatically follow imported-list owners using the existing reservation account before reading reservations, including existing connections.

Approved spec: the plan approved in this conversation on 2026-11-21.

Architecture: OhmywishesReservations owns authenticated profile and follow requests. OhmywishesWeb.Snapshot calls it once after public list access validation. Keep anonymous import and privacy checks intact; subscription errors log only a safe status and do not prevent wish imports. No schema, configuration or frontend contract changes.

Evidence: overskye/ohmywishes src/Service/UserService.php uses POST /api/v3/users/{userId}/following without a body and accepts an empty response. PublicUserProfileDto has isFollowedByMe and isFollowingRequestSent. Darim commit 808d908 introduced reservation account reads separately from anonymous visibility checks.

Review focus: existing and pending subscriptions; disabled token; failure/retry on a subsequent sync; private/revoked lists; cancellation and invalid provider payloads.

## Task 1: subscription before snapshot

- [x] Add HTTP-boundary tests proving subscription precedes reservation reads, repeats do not POST again, pending requests are not repeated, subscription errors preserve imports and can recover, disabled credentials and inaccessible lists never follow.
- [x] Run tests and confirm missing-subscription failures.
- [x] Add EnsureFollowing(string account, CancellationToken ct) to OhmywishesReservations using existing authenticated headers and safe RuleException mapping. Validate returned profile identity and boolean fields. Accept empty successful POST responses.
- [x] Call once per accessible snapshot before wishes are read; log a safe warning and preserve imports on RuleException.
- [x] Update existing authenticated web test fixture to represent profile-follow state.
- [x] Run focused and all available non-database tests. Attempt full suite and record environment limitations.

## Task 2: documentation and review

- [x] Update reservation setup documentation with automatic following and approval-required behavior.
- [x] Review the diff for privacy, credentials and regression risks; run git diff --check.

Execution notes: native worktree tool stalled; created Git worktree F:/Playground/darim-ohmywishes-follow on codex/ohmywishes-auto-follow from origin/main 1db8d44. Docker engine is unavailable locally, which may block PostgreSQL integration tests. Existing main and frontend checkouts remain untouched.

Validation: baseline 12/12 existing focused tests passed. RED: new following suite 14 failed and 2 passed before implementation. GREEN: 28/28 focused tests passed. Full suite: 122 passed, 54 blocked by unavailable Docker/PostgresFixture. All 122 tests independent of Docker passed in a separate run. Results: F:/Playground/darim-ci-local-results-follow/full.trx and available.trx; failure names in failed-tests.txt. Independent code review found no critical or important issues. No production calls or deployments performed.
