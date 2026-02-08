# NoBSSftp TODO / Implementation Backlog

Last updated: 2026-02-08

This file tracks known feature gaps and implementation work identified from the current codebase.

## Priority Legend

- `P0`: Security/correctness risks
- `P1`: Core product capability gaps
- `P2`: Quality-of-life and competitive features
- `P3`: Engineering quality and maintainability

## P1 - Core Capability Gaps

- [x] Transfer queue, retry, and resume
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

## P2 - File Manager and Workflow Features

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
