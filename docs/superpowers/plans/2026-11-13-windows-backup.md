# Windows-encrypted Darim Backup Implementation Plan

> **For agentic workers:** Use the approved native implementation approach and complete the tasks in order. Each task is test-first.

**Goal:** Pull a Darim recovery bundle to the user's Windows computer as an authenticated, encrypted archive, with a verified isolated restore path.

**Implementation status (2026-11-14):** The authenticated archive, encrypted SSH
pull, local status/retention, recovery-key export, and unregistered schedule
installer are implemented and covered by synthetic tests. The isolated restore
drill remains unimplemented; this change does not claim a verified recovery
path.

**Architecture:** A PowerShell entry point streams an SSH-generated tar archive into bounded memory (maximum 32 MiB), encrypts it with .NET AES-CBC, and HMAC-authenticates the result. A per-archive AES/HMAC key bundle is wrapped by Windows DPAPI and stored in the authenticated header; an optional recovery export encrypts that key bundle under a hidden password prompt. Restore verifies HMAC before sending plaintext through SSH to an isolated temporary PostgreSQL database.

**Tech Stack:** Windows PowerShell 5.1, built-in .NET cryptography, OpenSSH, Docker Compose/PostgreSQL.

**Spec:** `docs/superpowers/specs/2026-11-13-windows-backup-design.md`

## Global Constraints

- Keep plaintext backup contents off Windows disk; write only ciphertext and non-secret status metadata locally.
- Use current-user DPAPI for the default key wrapper; keep backup files under `%USERPROFILE%\.codex\darim-backups` with a current-user-only ACL.
- On the VPS, stage only Darim DB dump, Data Protection keys, generated service Compose, and app environment in a mode-700 directory with mode-600 files; always clean up.
- Never print records, secret values, or decrypted key material.
- Do not mutate production service configuration or restore into production.
- Do not register a scheduled task or create a production backup during implementation.
- Windows availability is irregular; freshness is best-effort and must not claim a guaranteed 24-hour RPO.

## Review Focus

- **Encrypted file tampering/truncation:** `test_tampered_or_truncated_archive_is_rejected_before_output` asserts failure and empty output.
- **Large or chunked source streams:** `test_multichunk_roundtrip_preserves_bytes` covers inputs larger than the copy buffer.
- **Wrong Windows user or lost profile:** `test_wrong_recovery_password_is_rejected` covers the portable recovery path; docs explain DPAPI profile/device binding.
- **Failed/partial SSH source:** `test_failed_source_does_not_publish_archive_or_success_status` asserts the `.partial` file is removed and the prior status remains.
- **Deploy in progress:** `test_remote_export_aborts_when_deploy_lock_is_held` asserts no dump/export proceeds while the release lock is held.

---

### Task 1: Authenticated archive format

**Files:**
- Create: `ops/windows-backup/ArchiveCrypto.psm1`
- Test: `tests/windows/test-archive-crypto.ps1`

**Interfaces:**
- `Protect-DarimArchive(Stream $Input, string $Path)` buffers at most 32 MiB, encrypts and authenticates it, then atomically publishes a ciphertext `.partial` file.
- `Test-DarimArchive(string $Path, byte[] $KeyBundle)` returns true only if the format and HMAC are valid.
- `Unprotect-DarimArchive(Stream $Output, string $Path, byte[] $KeyBundle)` verifies the complete HMAC before writing plaintext.
- `Get-DarimArchiveKey(string $Path)` unwraps the per-archive AES/HMAC key bundle with current-user DPAPI.
- `Export-DarimRecoveryKey(string $ArchivePath, string $Destination, SecureString $Password)` writes a password-encrypted key file.
- `Import-DarimRecoveryKey(string $Path, SecureString $Password)` returns the key bundle only after format and MAC validation.

- [x] **Step 1: Write synthetic tests** for round trip, multi-chunk input, tampering, truncation, no output before validation, and encrypted recovery-key export/import with wrong-password rejection.
- [x] **Step 2: Run tests and confirm the expected missing-module failures.**
- [x] **Step 3: Implement the versioned AES-CBC + HMAC format, DPAPI key wrapping, a fail-closed 32 MiB cap, and password-encrypted recovery-key format.**
- [x] **Step 4: Run tests and confirm all synthetic cases pass without plaintext files.**

### Task 2: SSH backup, status, and isolated restore workflow

**Files:**
- Create: `ops/windows-backup/darim-backup.ps1`
- Modify: `docs/disaster-recovery.md`
- Test: `tests/windows/test-darim-backup.ps1`

**Interfaces:**
- `-Action Backup` streams the remote staged tar archive through `Protect-DarimArchive`, atomically publishes it, then updates the local status file.
- `-Action Verify -BackupPath ...` validates DPAPI or supplied recovery-key authentication without emitting plaintext.
- `-Action ExportRecoveryKey -BackupPath ... -Destination ...` prompts twice without echo and writes only an encrypted recovery-key file.
- `-Action RestoreDrill -BackupPath ...` verifies the archive, then streams it to a temporary remote PostgreSQL restore; never targets the production DB.
- `-Action Status` reports the last successful pull time and archive age only.
- `-Action InstallSchedule` prepares an interactive logon plus daily catch-up task with `StartWhenAvailable`; implementation must not invoke it.

- [x] **Step 1: Write synthetic integration tests** for failed SSH source, deploy-lock contention, successful status update, and safe schedule definition. Restore-drill isolation tests remain pending with that feature.
- [x] **Step 2: Run tests and confirm they fail for the expected missing action behavior.**
- [x] **Step 3: Implement remote staging/cleanup, stream handling, local ACL, status, and optional schedule creation.**
- [ ] **Step 4: Run the synthetic tests and the isolated synthetic PostgreSQL restore.** Restore drill is out of scope for this implementation.
- [x] **Step 5: Review logs and files to confirm no secret or plaintext archive content is emitted or written locally.**

### Task 3: Recovery instructions and review

**Files:**
- Create: `docs/windows-backups.md`
- Modify: `docs/disaster-recovery.md`

- [x] **Step 1: Document manual backup, verify, recovery-key export, Windows availability limits, and profile/device loss boundary.** Isolated restore commands remain pending.
- [x] **Step 2: Run `git diff --check` and all synthetic archive/workflow tests.**
- [ ] **Step 3: Commit the implementation and report test output.**
- [x] **Step 4: Stop before any production dump, real secret archive, or scheduled-task registration; report the verified implementation for parent review.**
