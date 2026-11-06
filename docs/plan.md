# Wishlist implementation plan — 2026-11-06

Approved specification: the proposed plan in the conversation, including one occasion per gift, private solo reservations and equal-share splits, Telegram login, Blazor WASM and ASP.NET Core 10, PostgreSQL 18, Docker and Caddy.

## Tasks
1. Create solution and shared API contracts. Write failing domain/privacy/authentication tests, then implement rules and projections. Validate with xUnit.
2. Implement PostgreSQL schema/migrations, serialised gift mutations, invitation membership, owner editing/archive and notifications. Integration tests use real PostgreSQL via Testcontainers, including races.
3. Implement Telegram OIDC browser login, signed/fresh Mini App initData exchange, protected session cookie/CSRF and in-memory Mini App tokens. Test tampering, expiry and access boundaries.
4. Build Russian responsive Blazor UI with MudBlazor, grouped/filterable wishlists, editing, donation/split workflows, notices and Telegram JS adapter. Verify components and browser flows.
5. Add Docker Compose, Caddy, backup/restore scripts, CI, deployment instructions and smoke tests. Build release, run all tests, review whole change and correct significant findings.

## Defaults and decisions
- New repository on feature/wishlist: there is no existing repository or branch to isolate.
- Users belong to one company in v1. Company creator manages invitation rotation; no admin endpoint exposes gift reservations.
- Money is shown in RUB; price optional and indicative. Split has 2..number of other company members shares.
- Purchased gifts cannot be unreserved or left. Received gifts are archived by the owner; history remains available to donors.
- No hosting purchase or external publication. Deliver a deployable package; provider prices must be checked before rental.
- Browser cookie sessions and Mini App bearer sessions are separate; raw login data and credentials never logged.

## Verification
Domain transitions, owner-safe projections, JWT/initData validation, membership checks, true PostgreSQL races, notification recipients, bUnit and Playwright flows, release publish, Compose validation and backup/restore rehearsal. Actual Telegram client login requires the operator's bot credentials and domain; document this boundary explicitly.
