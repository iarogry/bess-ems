# Codex Cloud handoff: isolated productization branch

Continue `codex/agent-productization`, not `main`. The user selected a private
repository for this handoff. Do not publish this branch to the public origin,
change repository visibility or merge into main without explicit approval.

## Objective and operational boundary

Safely productize BESS EMS plus agent while preserving established operations.
Read [current-task.md](current-task.md), [current-baseline.md](current-baseline.md)
and [shared-writer-boundary.md](shared-writer-boundary.md) first. They contain
checkpoints, evidence, known gaps and the user's separate-application-server
decision. Transfer development code/context only, not operational control.

All broker/device enable switches default false. Keep them false. No live Deye
calls, production migrations, scheduler changes, operational secret requests,
pilot activation, deployment or direct-write credential changes are authorized
by the cloud handoff. Use only synthetic vendor handlers and an owned disposable
test database. Never run integration tests against an existing database: they
reset schemas. Never unlock Initiated/Unknown merely to retry a mutation.

## Authoritative state to inspect

The previous local checkpoint is `b1e93dcc`, recording a clean broker package
built from `e1933a2f` and two disabled process launches. These are historical
local checks; verify the current checkout before relying on them. Ignored local
ZIPs/TRX/portable runtimes and operational untracked files do not travel with Git.
Do not claim those artifacts exist in cloud or that checks were rerun there.

Known local checks: DeyeCloud 36 tests, Postgres/HTTP 108 tests, strict broker
build and disabled package restart. They do not prove complete release quality,
real shadow equivalence, TLS, sole physical writer fencing or hardware readiness.
Existing Optimization analyzer findings and unexecuted Docker CI remain open.

## Cloud setup and first action

Inspect repository instructions, .NET target/version and lock files. The project
targets .NET 10. Restore in locked mode; never silently regenerate package locks.
The local scripts require PowerShell 7 and some PostgreSQL runner paths are
Windows-specific. Inspect the actual cloud OS/runtime and prepare an equivalent
owned disposable PostgreSQL test harness rather than copying operational `.env`
or assuming local portable Windows binaries are available.

Start by verifying checkout branch/commit, default-off composition and relevant
unit tests. Report missing environment tools and current test evidence precisely.
Then progress toward bounded authenticated clients and enabled-process recovery
tests using synthetic devices and an owned test DB. Retain the full objective;
do not redefine completion as a successful build or a disabled smoke check.

Real shadow data, trusted legacy attestation, real station schema/readback,
server/OS/TLS/secret provisioning, direct-writer credential/egress fencing and
operator-approved pilot/rollback remain gates requiring evidence and authority.
