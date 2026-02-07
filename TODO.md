# NoBSSftp TODO / Implementation Backlog

Last updated: 2026-02-07

This file tracks known feature gaps and implementation work identified from the current codebase.

## Priority Legend

- `P0`: Security/correctness risks
- `P1`: Core product capability gaps
- `P2`: Quality-of-life and competitive features
- `P3`: Engineering quality and maintainability

## P0 - Security and Correctness

- [ ] Secure credential storage (OS keychain-backed)
  - Problem: profile data is persisted in JSON; encryption service is placeholder/base64 and currently unused.
  - Implementation target:
    - Store secrets in platform keychain (macOS Keychain, Windows DPAPI/Credential Manager, Linux Secret Service).
    - Persist only non-secret profile metadata plus keychain reference IDs.
    - Remove any raw password persistence paths.
  - Acceptance criteria:
    - No plaintext credentials in profile storage.
    - Existing profiles migrate safely.
    - Login works for password and key-passphrase flows.
  - Status:
    - Implemented for macOS Keychain using native Security.framework APIs.
    - `servers.json` is now sanitized (password/passphrase removed on save).
    - Saved-secret reuse in connect flow is gated by macOS device-owner verification.
    - Keychain reads are on-demand in connect paths, avoiding startup keychain prompt cycles.
    - Remaining work: Windows and Linux secure backends.

- [x] SSH host key verification and trust management
  - Problem: no known-host trust prompt/fingerprint pinning flow.
  - Implementation target:
    - Show fingerprint prompt on first connection.
    - Save trusted host keys.
    - Reject mismatched keys unless user explicitly re-trusts.
  - Acceptance criteria:
    - First-connect prompt appears.
    - MITM-style host key mismatch is blocked by default.
    - Trust decisions persist across app restarts.
  - Status:
    - Implemented via persisted `known_hosts.json` trust store and first-seen/mismatch trust dialogs.

## P1 - Core Capability Gaps

- [ ] Recursive directory upload
  - Problem: drag/drop upload path currently assumes file uploads.
  - Implementation target:
    - Detect dropped directories.
    - Walk local tree and upload recursively.
    - Show per-item and aggregate progress.
  - Acceptance criteria:
    - Mixed file/folder drops complete successfully.
    - Structure is preserved on remote.

- [ ] Directory download
  - Problem: download command supports files only.
  - Implementation target:
    - Add recursive remote directory download to local destination.
    - Handle conflicts (overwrite/duplicate/cancel).
  - Acceptance criteria:
    - Remote directories download with complete tree.
    - Conflicts are resolved via explicit user choice.

- [ ] Transfer queue, retry, and resume
  - Problem: current transfer UX is single-operation state with cancel only.
  - Implementation target:
    - Queue model with pending/running/completed/failed states.
    - Retry failed jobs.
    - Resume partially completed large transfers where protocol/server support allows.
  - Acceptance criteria:
    - Multiple jobs can be queued without blocking UI.
    - Failed jobs are retryable.
    - Long transfers can recover from transient interruptions.

- [ ] Integrity verification (optional post-transfer)
  - Problem: no checksum/validation step after transfer.
  - Implementation target:
    - Optional hash compare (where feasible) or size/time validation fallback.
  - Acceptance criteria:
    - Verification result is visible per transfer.
    - Corruption is surfaced clearly.

## P1 - Product Direction

- [x] Terminal strategy: external terminal only
  - Decision:
    - The app uses OS terminal launch for SSH access from session UI.
  - Acceptance criteria:
    - Product behavior and README feature list are consistent.

- [x] Remove deprecated embedded-terminal code paths
  - Problem:
    - Legacy embedded terminal classes remain in the codebase and can cause confusion.
  - Implementation target:
    - Remove or clearly quarantine unused in-app terminal view/viewmodel/emulator code.
  - Acceptance criteria:
    - No ambiguity about supported terminal mode.
  - Status:
    - Removed embedded terminal view/viewmodel/emulator and template routing references.

## P2 - File Manager and Workflow Features

- [x] Connecting-state UX feedback
  - Problem: connect action had no clear in-context loading state.
  - Implementation target:
    - Add centered connecting indicator in explorer area.
    - Prevent duplicate connect clicks while connect is in progress.
  - Acceptance criteria:
    - Users see immediate connection feedback.
    - Connect flow is guarded from re-entrant clicks.
  - Status:
    - Implemented with `IsConnecting` state + centered loading overlay.

- [ ] Local+remote dual-pane workflow
  - Problem: current explorer is remote-only.
  - Implementation target:
    - Add local pane with copy/move between panes.
    - Optional directory compare/sync mode.
  - Acceptance criteria:
    - Users can transfer without OS drag/drop dependency.
    - Sync plan can be previewed before execution.

- [ ] Search/filter in remote tree
  - Problem: no filename/path search in current directory tree.
  - Implementation target:
    - Fast filter for current listing.
    - Optional recursive remote search.
  - Acceptance criteria:
    - Large directories remain responsive.
    - Results can be opened/transferred directly.

## P3 - Engineering Quality

- [ ] Automated tests
  - Problem: no test project currently present.
  - Implementation target:
    - Unit tests for path handling, conflict logic, and transfer orchestration.
    - Integration tests for SFTP service behavior (mocked or test container).
  - Acceptance criteria:
    - CI runs tests on every PR/commit.
    - Core file operations have regression coverage.

- [ ] CI quality gates
  - Implementation target:
    - Build + test + static analysis workflow.
  - Acceptance criteria:
    - Broken builds/tests block merges.
    - Release artifacts are reproducible.

## Tracking Notes

- Keep this list ordered by impact and user risk.
- Convert each completed item into:
  - changelog entry
  - README update (if user-visible behavior changed)
