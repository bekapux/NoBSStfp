# NoBSSftp TODO / Implementation Backlog

Last updated: 2026-02-09

This file tracks known feature gaps and implementation work identified from the current codebase.

## Priority Legend

- `P0`: Security/correctness risks
- `P1`: Core product capability gaps
- `P2`: Quality-of-life and competitive features
- `P3`: Engineering quality and maintainability

## P0 - Security and Integrity Gaps

## P1 - Core Capability Gaps

- [x] SSH agent and modern auth options
  - Problem:
    - Auth currently focuses on password/private key file only.
  - Implementation target:
    - Add support for ssh-agent identities and profile-level auth preference order.
  - Acceptance criteria:
    - Users can connect with agent-backed keys without duplicating secrets in app profiles.

## P2 - File Manager and Workflow Features

- [ ] Bandwidth and concurrency controls
  - Problem:
    - Transfer queue is functional but lacks speed limits and configurable parallelism.
  - Implementation target:
    - Add per-session global bandwidth cap and max parallel transfer count.
  - Acceptance criteria:
    - User can cap throughput and tune concurrency for network stability.

- [ ] Manual pause/resume controls in transfer queue
  - Problem:
    - Queue supports retry/resume internally, but users cannot pause/resume jobs on demand.
  - Implementation target:
    - Add Pause/Resume actions with clear job state transitions.
  - Acceptance criteria:
    - Long transfers can be paused and resumed without restarting the job.

- [ ] Remote path bookmarks (per server)
  - Problem:
    - Frequent working directories require repeated manual navigation.
  - Implementation target:
    - Add bookmark add/remove/rename and quick jump in connected file explorer.
  - Acceptance criteria:
    - Common remote paths are reachable in one click.

- [ ] Folder sync mode (one-way mirror/update with preview)
  - Problem:
    - Bulk folder operations require repeated manual transfer actions.
  - Implementation target:
    - Add sync planner (preview diff, exclude patterns, overwrite policy) and execute as queued jobs.
  - Acceptance criteria:
    - Users can run repeatable folder sync with explicit preflight preview.

## P3 - Engineering Quality

- [ ] Automated integration tests against a disposable SFTP server
  - Problem:
    - Core transfer/security behaviors depend heavily on runtime conditions and are easy to regress.
  - Implementation target:
    - Add CI integration suite for connect, host-key trust, transfer queue, conflict handling, and cancellation cleanup.
  - Acceptance criteria:
    - Critical transfer and security paths are covered by repeatable automated tests.

## Tracking Notes

- Keep this list ordered by impact and user risk.
- Convert each completed item into:
  - changelog entry
  - README update (if user-visible behavior changed)
