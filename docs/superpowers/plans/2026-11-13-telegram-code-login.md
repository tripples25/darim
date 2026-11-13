# Telegram code login implementation plan

Goal: /login in a private bot chat issues a single-use code, valid for five minutes. The website signs into the same Telegram account, creating it on first login.

Architecture: a singleton bounded, hash-only code store; a hosted Telegram long-poll worker; a rate-limited same-origin cookie login endpoint; a reusable login form. No schema changes. Codes are intentionally invalidated on restart; deploy one polling instance per bot. Existing Telegram login remains available.

1. Add tests for expiry, replacement, replay, concurrency, private chat validation and failed delivery revocation; observe failure.
2. Implement code store and bot worker. Use 12 random hex characters (48 bits), five-minute expiry, 30-second issuance cooldown, bounded storage. Never log codes, Telegram responses, token-bearing URLs or exceptions from bot HTTP calls.
3. Add POST /auth/code with JSON body, Origin verification and existing auth rate limiter; create cookie session through AccountService.Login.
4. Add form on welcome and shared wishlist login; preserve invites and return context. Add browser coverage for form and error handling.
5. Run .NET suite, release build and browser tests. Document operation and single-instance constraint.

Review focus: expired/replaced/replayed code, non-private messages, forged origin, delivery failure and duplicate commands, first-time account creation, preservation of invitation and share context.
