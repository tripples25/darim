# Windows-encrypted Darim backup

## Intent

Keep a local, encrypted copy of Darim's database and recovery configuration on
the user's Windows computer, pulled over the existing SSH profile. This adds a
second machine outside the VPS without an external storage account.

## Goals and limits

- Stream a fresh PostgreSQL custom dump, the app Data Protection key volume, the
  Coolify-generated Darim Compose file, and the running app environment to an
  encrypted local archive.
- Never write plaintext backup contents to Windows disk or print secret values.
- Verify archive integrity before decrypting or restoring it.
- Restore a database dump into an isolated PostgreSQL container over SSH; never
  target the live production database.
- Test with synthetic data before any production backup.
- Keep freshness visible locally. Windows may be offline, so this is best-effort
  and does not guarantee a 24-hour RPO.

The archive uses a fresh AES-256 key and HMAC-SHA256 key per run. The encrypted
tar stream is buffered in memory up to a 32 MiB payload limit, then AES-CBC
encrypted; HMAC authenticates the full header and ciphertext before any restore
output is emitted. DPAPI protects the key bundle for the current Windows user.
An explicit command can export that archive's key bundle into a separate
password-encrypted recovery file; the password is entered in a hidden prompt
and never stored. The default archive remains bound to the Windows user profile
and computer. Archives exceeding the limit fail with a clear size message so a
future data growth requires a planned capacity change rather than disk spills.

## Approaches

1. **DPAPI per chunk:** simple platform API, but too much custom archive framing
   and per-chunk protection work.
2. **DPAPI-protected AES-CBC plus HMAC, bounded in memory (chosen):** uses
   built-in .NET crypto, authenticates before restore, writes no local
   plaintext, and keeps implementation small for the current archive size. The
   32 MiB cap bounds memory and fails closed if a later archive grows too large.
3. **Streaming hybrid encryption:** handles arbitrarily large archives, but
   needs more custom framing and failure handling than the current compressed
   dump size warrants.

## Constraints and safety

- Use only Windows PowerShell and built-in .NET crypto; no paid or external
  storage service.
- Put archives outside Git under the current user's `.codex` directory and set
  an ACL for that Windows SID.
- On the VPS, stage plaintext only in a mode-700 directory with mode-600 files;
  remove it on every exit path. Capture only Darim's app environment and
  Coolify Compose, not the entire Coolify database.
- Do not restart production services, change VPS configuration, or register a
  scheduled task during implementation.
- Verify the archive MAC before decrypting. For database restore, stream the
  verified dump to a disposable network-isolated PostgreSQL container.
- A portable recovery file must be explicitly requested and encrypted from a
  hidden password prompt. It is needed if the Windows profile/device is lost.

## Acceptance

Synthetic tests cover a multi-chunk round trip, truncation and tampering, wrong
recovery password, hidden-password export, and verification before output. A
synthetic PostgreSQL dump must restore into the isolated drill target and pass
schema/migration checks. After those checks, report status and remaining
limitations before creating any production backup or secret archive.
