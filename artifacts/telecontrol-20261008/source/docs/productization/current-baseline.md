# Agent productization baseline

This worktree develops the agent-facing product without changing the existing
operational checkout or its scheduled Deye workflow.

## Frozen reference

- Git baseline: `2b197db5` (`main`).
- Productization branch: `codex/agent-productization`.
- The operational checkout remains the reference implementation during shadow
  mode.
- Production Deye writes remain owned by the existing scheduled runner until a
  controlled pilot explicitly transfers writer ownership.
- New components default to read-only and must not require production write
  credentials.

## Compatibility invariants

The productized workflow must preserve these behaviours:

1. Missing, stale, duplicated or invalid critical input blocks executable plan
   creation.
2. A Deye payload contains exactly six strictly ordered start-time/settings
   pairs. Rotating `00:00` to the final wire position must not shift settings.
3. A live prewrite validates topology, freshness, BMS SOC, alarms and power
   limits immediately before a write.
4. Only the configured master may be written.
5. Once a write is accepted or its result is unknown, it is never blindly
   repeated.
6. Verification uses a fresh device read and semantic comparison with the
   approved payload.
7. Credentials, tokens, station identifiers and device serial numbers never
   enter agent context, fixtures or audit exports.
8. Exactly one writer is enabled for a site and time window.

## Migration stages

1. Characterization fixtures and deterministic contract tests.
2. Read-only API and orchestration modules in shadow mode.
3. Legacy-versus-shadow comparison with explicit mismatch reporting.
4. Bounded agent tools for observation, preview and explanation.
5. Proposal, approval and durable outbox while the legacy runner remains the
   sole writer.
6. Controlled single-site pilot with kill switch and rollback drill.
7. Writer ownership transfer only after verified equivalence.

No stage may implicitly enable the next stage.

## First verification slice

The first slice adds sanitized executable and blocked golden fixtures plus an
exact `ShadowPlanComparer`. Run only this slice with:

```powershell
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1'
& 'C:\Users\admin\temp\bess-ems\.dotnet-x64\dotnet.exe' test `
  tests\hexagon\BatteryEms.Application.Tests\BatteryEms.Application.Tests.csproj `
  --no-restore --nologo /m:1 /nodeReuse:false `
  /p:UseSharedCompilation=false /p:RunAnalyzers=false `
  --filter FullyQualifiedName~ShadowPlanComparisonTests
```

`RunAnalyzers=false` applies only to the legacy test assembly in this focused
command. The new Application code is compiled separately with repository
analyzers enabled before this command; unrelated baseline analyzer failures are
documented rather than edited as part of the shadow slice.

## Shadow orchestration and agent read API

`ShadowPlanComparisonModule` runs only for `day-ahead-shadow`. The delivery date
is explicit in `TriggerRef` as `yyyy-MM-dd`; it is never inferred from UTC and
therefore cannot drift across the Europe/Kyiv day boundary. Both legacy and
shadow observations must exist before comparison. Missing input blocks only the
shadow run, while a semantic mismatch produces a persisted `Partial` result for
investigation. Neither result grants activation permission.

The bounded agent endpoint is:

```text
GET /agent/sites/{siteId}/shadow-comparisons/latest?deliveryDate=yyyy-MM-dd
```

It requires the `agent-read` policy. Authenticated `viewer` and `operator`
roles are accepted; anonymous callers receive `401`. The response contains only
internal site/run references, readiness flags and field-level mismatches. It
does not expose full TOU payloads, vendor identifiers, credentials or command
operations.

Focused verification uses `FullyQualifiedName~ShadowPlan` for Application
comparison/orchestration tests and
`FullyQualifiedName~AgentShadowEndpointTests` for the authenticated API
contract tests.

## Persistent shadow evidence

Shadow snapshots and comparisons have an append-only PostgreSQL persistence
path. Snapshot identity is based on a canonical SHA-256 hash and a unique
`(site_id, delivery_date, side, snapshot_hash)` key, so replaying the same
observation is idempotent. Comparisons are tied to an orchestration run and are
upserted only for that run. The schema deliberately contains no device serial,
station identifier or credential fields.

Migration-resource tests and the persistence adapter build pass. Database
roundtrips now pass on an isolated portable PostgreSQL 16.15 cluster; see
"Live PostgreSQL verification" below. No operational database was used.

## Legacy import and host activation boundary

`LegacyDeyeScenarioFileSource` is a read-only adapter for the existing scenario
files. It accepts only the exact delivery-date filenames, enforces an 8 MiB
limit, validates the embedded date, and maps only bounded plan fields. Raw JSON,
vendor identifiers and secrets are not returned to the agent-facing layer.

Host integration is opt-in through `ShadowModeEnabled`; it is disabled by
default and fails closed when enabled without `LegacyScenarioDirectory`.
Registration keeps `IBatteryCommandSink` on the existing `NoOp` implementation.
Focused architecture verification passes all three host composition tests:
default isolation, missing-directory rejection and read-only opt-in wiring.

This import/comparison slice does not itself schedule `day-ahead-shadow`, grant
an agent device-write capability, or transfer writer ownership. Triggering is a
separate, explicitly gated surface described below.

## Bounded shadow trigger

The first state-changing agent tool is intentionally limited to internal shadow
computation:

```text
POST /agent/sites/{siteId}/shadow-runs/{deliveryDate}
```

The route exists only when shadow control is explicitly enabled (`Bess:`
`ShadowModeEnabled` in the production host, or `Agent:ShadowControlEnabled` in
the standalone API test host). It requires the `operator` role; anonymous and
viewer callers receive `401` and `403` respectively. The request accepts no
body, command settings, device identifiers or vendor payload.

`DefaultShadowRunTrigger` can create only `day-ahead-shadow` runs from manual,
scheduled or retry triggers. It derives the UTC horizon from the Europe/Kyiv
delivery day and uses the stable key
`day-ahead-shadow:{siteId}:{yyyy-MM-dd}`, so repeated calls return the same run.
The route remains absent when shadow mode is disabled, and host composition
still resolves the command sink to `NoOpBatteryCommandSink` in the tested
configuration.

## Product schedule projection

`ScheduleShadowPlanProjectionModule` now creates the independent
`ShadowPlanSide.Shadow` observation by reading the active product
`DayAhead` schedule. It does not call the schedule optimization use case (which
would replace a live schedule) and has no device-command dependency. The pure
projector maps the 24 hourly schedule into four Deye comparison windows with
exactly six start-time/settings rows each.

Projection fails closed when the schedule is absent, belongs to another asset,
does not cover the exact Europe/Kyiv delivery horizon, has a non-hourly grid,
exceeds per-inverter power, needs more than six settings transitions in a zone,
or falls on a 23/25-hour DST day that has not yet received an explicit Deye
mapping rule. A blocked projection is persisted as a blocked shadow observation
and never becomes a device payload.

## Automatic shadow scheduler

Automatic polling is a second opt-in gate on top of shadow mode. It is enabled
only when all of the following are explicitly configured:

```text
Bess:ShadowModeEnabled=true
Bess:ShadowSchedulerEnabled=true
Bess:ShadowSiteId=<site/asset id>
Bess:ShadowTriggerHourLocal=<0..23>
Bess:ShadowTriggerMinuteLocal=<0..59>
Bess:PersistenceConnectionString=<PostgreSQL connection>
```

An in-memory scheduler is rejected by default. The separate
`Bess:ShadowSchedulerAllowInMemory=true` escape hatch exists only for tests and
local evaluation and must not be used for a production shadow deployment.

The scheduler resolves the delivery date using Europe/Kyiv time: before the
configured local trigger it catches up the current delivery day; after the
trigger it targets the next day. Before consuming the orchestration idempotency
key it verifies both that the legacy scenario is available and that the active
product schedule is representable. Late inputs therefore remain retryable on
the next poll. A successful day is started at most once per process, while the
required Dapper persistence adapter lets the orchestration idempotency key also
protect restart/replica repeats.

The scheduler only invokes `IShadowRunTrigger` with trigger type `Scheduled`.
It does not activate a schedule or call any Deye transport. Waiting polls are
debug-level; only an actual run start is informational.

Verification at this checkpoint:

- shadow trigger Application tests: 2/2 passed;
- authenticated agent API tests: 8/8 passed;
- schedule shadow projection tests: 3/3 passed;
- automatic scheduler tests: 4/4 passed;
- opt-in host composition tests: 7/7 passed;
- complete Application regression suite: 467/467 passed;
- complete API regression suite: 82/82 passed;
- complete architecture regression suite: 41/41 passed;
- complete persistence unit/migration suite: 20/20 passed;
- Application and API analyzer builds: 0 warnings, 0 errors;
- complete Host build: 0 warnings, 0 errors after suppressing only known
  pre-existing analyzer findings in legacy OREE/Deye and executable-host code.

Automatic shadow scheduling is implemented but is not enabled in the operational
checkout or any production configuration. Production Deye writer ownership has
not moved.

## Activation proposal and held outbox gate

An operator can now create a short-lived activation proposal only from the
latest exact-date shadow comparison when both observations are ready, the plans
are semantically equivalent and there are no mismatches. The proposal stores a
canonical payload and SHA-256 hash internally. Agent API responses expose the
hash and workflow metadata, but never return the payload itself.

The opt-in routes are:

```text
POST /agent/sites/{siteId}/activation-proposals/{deliveryDate}
POST /agent/activation-proposals/{proposalId}/approve
GET  /agent/activation-proposals/{proposalId}
```

Proposal creation and approval require `operator`; the read route accepts the
bounded agent-read roles. Four-eyes enforcement prevents the proposer from
approving the same proposal. Approval is atomic and idempotent and creates
exactly one durable outbox row in `Held` state. Proposal approval itself cannot
release, claim or dispatch that row. Release is a later, independently gated
operation described below; the internal pre-write claim protocol is described
later, while automated execution and vendor dispatch remain absent.

PostgreSQL migration `0015_activation_proposals_outbox.sql` adds the proposal
and outbox tables with comparison/run foreign keys, unique proposal and
idempotency constraints, bounded status checks and no credential/device-secret
columns. The Dapper store uses a serializable transaction and row lock for the
approval-plus-outbox operation. Its analyzer build and migration tests pass.
The PostgreSQL integration scenario now passes at runtime and covers durable
reload, four-eyes rejection, idempotent proposal creation and idempotent approval.

The remaining stages are intentionally absent: automated outbox execution and
vendor dispatch, technical fencing shared with the legacy writer, an executed rollback drill,
an executed controlled pilot and real equivalence evidence across the agreed
observation period. The bounded pilot control plane described below exists,
and has now been exercised on isolated PostgreSQL, but not Deye.
Until all of those are implemented and verified, the existing runner remains
the sole Deye writer.

## Product-writer safety control plane

The product side now has a dedicated safety contract instead of reusing the
short-lived orchestration-run lock. A safety state is fail-closed when absent;
its initial persisted form has the kill switch engaged and assigns authority to
the legacy runner. State changes use compare-and-exchange revisions so stale
operators cannot overwrite a newer cutover or rollback decision.

`ProductAgent` authority is invalid without both a legacy-writer stop timestamp
and explicit evidence reference. Even with that state, the writer gate remains
closed until a specific product instance owns a live lease. Leases are bounded
to two minutes, exclusive per site and carry a monotonically increasing fencing
token whenever ownership changes after expiry. Same-owner renewal keeps the
same token. A missing state, engaged kill switch, wrong authority, missing stop
evidence, missing/expired lease or different lease owner all fail closed with a
specific blocking code.

Migration `0016_activation_writer_safety.sql` persists the revisioned state,
per-site fence sequence and writer lease. The Dapper lease operation serializes
per site before checking or replacing ownership. Application and persistence
analyzer builds pass; migration tests verify the default-closed schema. The
PostgreSQL round-trip scenario covers stale-revision rejection, exclusive lease
ownership, expiry takeover, monotonic fencing and durable reload, and now passes
on isolated PostgreSQL. This does not prove shared legacy/device fencing.

The bounded observation endpoint is:

```text
GET /agent/sites/{siteId}/writer-safety
```

It requires `agent-read` authorization and returns only configured/kill-switch
state, authority, whether stop evidence exists, revision and sanitized lease
timing. It deliberately omits the evidence text, operator identity, lease owner
and fencing token. There is no endpoint for mutating the control plane.

This control plane is not yet a claim that the legacy process is technically
fenced: the legacy runner does not currently consume the shared fence. The host
still resolves `NoOpBatteryCommandSink`, and no runtime claim/dispatch endpoint
or worker exists.

## Safety-bound release and rollback

Migration `0017_activation_cutover_controls.sql` binds every dispatchable
outbox row to the exact writer-owner id, safety revision and fencing token that
authorized its release. `DapperActivationCutoverStore` locks the proposal,
outbox row, safety state and writer lease in one serializable transaction. A
`Held → Ready` transition requires all of the following at that instant:

1. The proposal is approved and has a held outbox item.
2. The releasing operator is not the proposal author.
3. The server-defined consecutive equivalence window passes for the proposal's
   delivery date.
4. The supplied safety revision equals the locked durable state.
5. The kill switch is disengaged and `ProductAgent` has authority backed by
   legacy-stop evidence.
6. The named product instance owns a non-expired lease with the exact supplied
   fencing token.
7. A matching, non-expired pilot session is armed for this exact proposal,
   outbox item, owner, revision and fencing token by an operator other than the
   proposer, reviewer and releaser.

Retries with the same proposal, revision, owner and fence are idempotent.
Conflicting retries fail without modifying the row. Release metadata is kept
inside persistence and the API response omits writer-owner and fencing token.

Rollback is also one serializable operation: it engages the kill switch,
changes writer authority to `None`, increments the safety revision, cancels all
`Ready` or `Claimed` items for the site and deletes the live lease. A caller-provided UUID
makes rollback replay-safe and returns the original cancelled-row count. It
does not claim to restart the legacy runner; that remains a controlled runbook
step after product authority has been revoked.

The operator routes are a third, separate opt-in gate:

```text
POST /agent/activation-proposals/{proposalId}/release
POST /agent/sites/{siteId}/writer-safety/rollback
```

`Bess:ActivationCutoverEnabled=true` is rejected unless shadow mode, the
separate `Bess:ActivationPilotEnabled=true` opt-in and a PostgreSQL connection
are all configured. Standalone/in-memory hosts use a
fail-closed store that always returns `activation-durable-persistence-required`,
even when the route is deliberately enabled for contract testing. Both routes
require the operator role and append audit records.

The PostgreSQL round-trip scenario passes at runtime and covers wrong-fence rejection,
successful and repeated release, release-metadata reload, rollback, repeated
rollback and lease removal. No production worker consumes a `Ready` item, and
no code in this slice invokes Deye.

## Consecutive equivalence and pilot readiness

A single successful comparison is insufficient for cutover. The default
qualification window is 14 consecutive delivery days ending on the proposed
delivery date. Operators may configure a longer or shorter agreed window with
`Bess:PilotReadinessRequiredDays`, but the host refuses values below seven days
and caps the query horizon at 90 days.

For every required date, the latest comparison must exist, both legacy and
shadow payloads must be ready, `IsEquivalent` must be true and the mismatch
list must be empty. A missing day, blocked payload or mismatch fails the entire
window. Multiple comparisons for one delivery date cannot hide a regression:
the most recently compared record is authoritative.

The bounded explanation endpoint is:

```text
GET /agent/sites/{siteId}/pilot-readiness?windowEnd=yyyy-MM-dd
```

It requires `agent-read` authorization and returns dates, pass/fail status and
sanitized blocking codes. It omits legacy/shadow values and mismatch details.
The same policy is evaluated again inside the serializable PostgreSQL release
transaction, so an earlier read result is never treated as release authority.

Tests cover a full passing window, missing evidence, a later mismatch replacing
an earlier passing comparison, API authorization/sanitization and rejection of
host windows below seven days. The PostgreSQL cutover integration scenario now
seeds all 14 required dates and passes at runtime. These are synthetic test
observations, not real 14-day equivalence evidence for a production site.

## Bounded pilot session gate

Migration `0018_activation_pilot_sessions.sql` introduces a durable pilot
session that is deliberately authorization only: it contains no vendor
transport, command sink, outbox claim or dispatcher. A session is bound to one
proposal and one held outbox item, one site, one product-writer owner, one
safety revision and one fencing token. It is valid for at most 15 minutes.
Partial unique indexes permit at most one armed session per site and per outbox
item.

Arming rechecks the approved proposal, held outbox, current writer-safety state
and live lease in one serializable transaction. The arming operator must be
different from both proposer and reviewer. Release then locks and validates
the same session and additionally requires a different releasing operator.
Expired or aborted sessions, scope mismatches and safety/fence changes all fail
closed. Release persists the pilot-session UUID alongside the existing safety
metadata, so replay can only succeed with the original authorization tuple.

Rollback now also aborts every armed pilot session for the site in the same
transaction that engages the kill switch, revokes authority, cancels `Ready`
items and removes the lease. The `0018` upgrade has an explicit guard: if an
older database still contains `Ready` or `Claimed` rows without pilot metadata,
migration stops and requires operator rollback instead of silently converting
or releasing them.

The separately opted-in routes are:

```text
POST /agent/activation-proposals/{proposalId}/pilot-sessions
GET  /agent/activation-pilot-sessions/{sessionId}
POST /agent/activation-pilot-sessions/{sessionId}/abort
```

Arm and abort require the operator role; read requires `agent-read`. In-memory
composition is fail-closed with `activation-durable-persistence-required`.
The read response omits the writer owner, fencing token, reasons and operator
identities; those values remain persistence-only authorization evidence.
Application, API and migration tests cover the 15-minute bound, expiry,
authorization, fail-closed fallback and route opt-ins. The PostgreSQL scenario
passes at runtime with arm, wrong-fence rejection, pilot-bound release, replay
and rollback-abort coverage. No Deye writer has been wired or enabled.

## Atomic pre-write claim protocol

Migration `0019_activation_prewrite_claims.sql` and
`DapperActivationPrewriteClaimStore` add the last durable boundary before a
future vendor write. It is a required boundary, not yet proof of device-level
fencing: a future writer must consume it immediately before the network call,
and the legacy/Deye fence remains a separate release gate. This is an internal persistence port only: there is no
agent route, hosted worker, timer or Deye dependency that can invoke it.

`Ready → Claimed` occurs in one serializable transaction and requires:

1. Exact release owner, safety revision, fence and pilot-session metadata.
2. A payload that deserializes as an executable `ShadowPlanSnapshot` and whose
   canonical SHA-256 still matches the approved proposal after JSONB storage.
3. An armed pilot session covering the entire claim window.
4. The same current `ProductAgent` authority, legacy-stop evidence and safety
   revision used at release.
5. The same live writer lease and fencing token, with enough remaining lease
   time for the whole claim window.

Claims last 15 seconds by default and can never exceed 30 seconds. A retry with
the same claim UUID and executor is idempotent. A different executor is
rejected. An expired claim is deliberately not reissued automatically because
the old process may still be alive; operator rollback is required before any
new attempt. Completion can record only `Succeeded` or `Failed` against the
exact claim UUID and executor. Rollback clears a `Claimed` row atomically before
late completion can succeed.

Unit tests cover the duration bound, JSONB-safe canonical hash verification and
the fail-closed in-memory binding. Migration tests pin the revision/fence/pilot
columns and completion evidence. The PostgreSQL scenario passes at runtime with claim,
idempotent replay, competing-executor rejection, rollback cancellation and
late-completion rejection. No production worker or Deye call consumes the claim.

## Fail-closed one-shot dispatch contract

The Application layer now contains a one-shot activation dispatch use case. It
can invoke only an `IActivationPlanDispatcher` after acquiring an atomic
pre-write claim, re-reading the canonical shadow plan, checking the payload hash,
matching the site identity, binding the in-memory plan object back to that same
hash and confirming that the claim has not expired. Every
adapter outcome is then completed against the exact claim. An adapter exception
is recorded as `activation-dispatch-outcome-unknown`; it is never converted into
an automatic retry. Persisted adapter outcome codes are limited to 96 lowercase
ASCII letters, digits and hyphens. This rejects raw response text and URLs;
future adapters must still use fixed allowlisted codes to prevent identifiers or
secrets that happen to fit that alphabet from entering the audit field.

The default dispatcher is `FailClosedActivationPlanDispatcher`. It has no
network or device dependency and always rejects with
`activation-plan-dispatcher-not-configured`. There is deliberately no endpoint,
hosted service, timer or Deye implementation for this contract. Consequently,
this slice makes the future execution semantics testable without creating a new
way to write to production hardware.

## Explicit single-window pilot scope

The operational four-window contract applies exactly one saved six-slot window
per trigger: Z1 at 23:55 on the previous local date, Z2 at 05:55, Z3 at 11:55,
and Z4 at 17:55 in Europe/Kyiv. A daily proposal contains all four windows;
one device update cannot prove completion of that whole daily plan.

Pilot arming now requires an explicit `window_id` of Z1, Z2, Z3 or Z4 and checks
that exactly one matching window belongs to the canonical approved payload.
Release copies the immutable pilot scope into `release_window_id`. The atomic
claim verifies that scope against the pilot and stores both `claim_window_id`
and a separate SHA-256 of the six-slot window. The dispatch envelope binds the
selected window back to the approved daily plan as well as to the claim hash.
A terminal outbox status therefore describes only this single authorized pilot
window. It must not be presented as proof that all four daily windows ran.

Migration `0020_activation_pilot_window_scope.sql` rejects missing scope with
explicit PostgreSQL `CHECK (... IS TRUE)` semantics. It refuses upgrade when
pilot history or released/claimed records already exist; such databases require
an operator-reviewed migration that preserves historical audit records. Do not
delete pilot history to make this migration pass. Fresh isolated databases can
apply the complete migration set normally.

Claim replay remains a read of the prior claim, but is now explicitly marked
`IsReplay`. The execution use case never dispatches or overwrites completion
on replay; it returns `activation-dispatch-replay-reconciliation-required`.
This also covers a process that lost the response after its claim committed.
The previous executor may still be active, so automated write retries remain
prohibited.

Unit tests cover mismatched window selection, altered window content, claim
replay, and API rejection of an unknown window. The PostgreSQL scenario includes
scope propagation and claim replay assertions and now passes on isolated
PostgreSQL. Full daily operation still requires separate
durable execution records and approvals for all four windows, trigger-time/date
coordination in the scheduler, a shared legacy writer fence and fresh device verification. No Deye
dispatcher or production scheduler is enabled by this change.

## Trigger-date and clock gates

`ActivationWindowTimingPolicy` derives the trigger from the approved delivery
date and explicit window in Europe/Kyiv. Z1 is due at 23:55 on the previous
local date; Z2/Z3/Z4 are due at 05:55/11:55/17:55 on the delivery date.
The new pilot permits startup for at most 15 minutes after that trigger, with
an exclusive deadline. This is a conservative product-pilot constraint, not a
change to the operational runner's delay policy. A delayed Z1 still refers to
the same delivery date even if execution begins after midnight. It never
advances to a different window. Unsupported 23/25-hour delivery dates remain
blocked consistently with the shadow projector.

Before `Ready -> Claimed`, persistence verifies the payload site and delivery
date against the outbox row, checks that the named trigger is due, and requires
the entire 15-second claim lifetime to fit before the start deadline. The
execution use case replaces request-supplied time with its server `IClock`.
At dispatch it checks time again, including clock rollback before acquisition,
claim expiry and the trigger deadline. A future concrete device adapter must
repeat these checks immediately at its network-write boundary, after live
preflight, together with the shared writer fence; application checks alone do
not fence an in-flight device request.

Application tests verify all four triggers in summer and winter, the year
boundary, midnight delay, deadline exclusion, stale selected windows, DST days
and untrusted request time. The PostgreSQL roundtrip fixture now contains a
real canonical approved payload/hash instead of placeholder JSON, truncates
pilot sessions along with their referenced test records, and asserts that early
and late claim attempts are rejected without consuming a valid Ready item.
These assertions now pass against isolated PostgreSQL.

## Live PostgreSQL verification

At the initial 2026-09-29 database checkpoint, all **61 persistence integration tests passed** against a fresh
PostgreSQL **16.15** Windows portable cluster, including the complete migration
set, idempotent/concurrent migration, shadow/proposal roundtrips, approval,
pilot release/claim/replay, rollback and late-completion rejection. Additional
runtime checks prove that `NULL` window/hash metadata and a mismatched claimed
window are rejected by PostgreSQL, and that the window-scope upgrade guard
preserves existing pilot history and Ready/Claimed outbox records.

Runtime testing found and fixed three defects in this isolated branch:

- DbUp interpreted tagged dollar-quote migration guards as substitution
  variables. Migrations 0018/0020 now use the existing untagged `DO $$` pattern;
  their protective guards remain intact.
- Dapper required an explicit `DateOnly` parameter/row handler. Configuration
  now initializes atomically via `Lazy`, with SQL date mapping that performs
  no local/UTC conversion.
- The existing activation dedupe compatibility ceiling still ended at 0017.
  It now recognizes this build's migrations through 0020; runtime tests still
  reject a synthetic future migration 9999.

The archive came from the official EDB Windows binary link:
[PostgreSQL 16.15-4 Windows x64 binaries](https://get.enterprisedb.com/postgresql/postgresql-16.15-4-windows-x64-binaries.zip).
Downloaded archive SHA-256:
`F5F55B03BD54CE0DD1C51D524B54C7E015ABD4D620AF27D6971288A2DBE4A8F8`.
This recorded hash identifies the downloaded artifact; it is not an independent
vendor-signature/checksum verification. The runtime is not committed or
installed system-wide; only bin/lib/share and the server license were extracted
under ignored `artifacts/postgres-portable/runtime`.

Reproduce using a trusted PostgreSQL binary directory and the local SDK:

```powershell
& scripts\test-productization-postgres.ps1 `
  -PostgresBin artifacts\postgres-portable\runtime\pgsql\bin `
  -DotnetPath C:\Users\admin\temp\bess-ems\.dotnet-x64\dotnet.exe
```

The runner refuses an occupied port, creates a unique data directory for every
run, binds only `127.0.0.1:55439`, creates a dedicated test database and sets
test-process environment variables without reading operational `.env` files.
Trust authentication is for this loopback-only disposable cluster, not a
deployment configuration. It stops only its owned cluster in `finally`, restores
the process environment, and preserves data/logs/TRX results under ignored
artifacts. Existing integration tests reset the public schema; never point them
at an operational database or use the old schema-reset bootstrap for this run.

All 22 persistence unit/regression tests pass, including guards against
reintroducing DbUp-unsafe tagged dollar quotes. The production persistence build
passes with analyzers and warnings-as-errors (zero warnings/errors). The runner's
occupied-port refusal was also tested: it
failed before creating any cluster. All owned PostgreSQL processes were stopped
after the final integration run.

The database gate is now verified; production readiness is not. Real shadow
observations, shared legacy/device writer fencing, fresh device preflight and
readback, a controlled hardware pilot and an executed operational rollback
drill remain mandatory. No production Deye writes or scheduler settings changed.

## Dispatch restart, cancellation and completion evidence

The follow-up runtime suite now passes **70/70 PostgreSQL integration tests**.
It executes `DefaultActivationDispatchExecutionUseCase` against real Dapper
claim/completion stores with only the default fail-closed dispatcher or local
test doubles. No test adapter has a device/network dependency or host registration.
The shared released-pilot fixture retains proposal idempotency, four-eyes,
14-date synthetic readiness, pilot and release assertions for every scenario.

Observed runtime properties:

- Default dispatch rejection, simulated success and adapter exceptions produce
  durable exact completion evidence. Fresh use-case/store instances never
  redispatch a terminal row. Exact completion replay is idempotent; contradictory
  completion cannot overwrite the original evidence or completion timestamp.
- Cancellation after claim leaves `Claimed` with no invented completion. A fresh
  executor use-case returns reconciliation-required without calling the adapter;
  even expiry does not automatically reissue the claim.
- A second executor cannot dispatch while the first test adapter is in flight.
- Rollback during an in-flight test adapter cancels the row; a late success
  cannot resurrect it and returns an unknown/unrecorded outcome.

The completion request now enforces the same bounded technical-code grammar as
the dispatch result. Persistence rejects a new completion timestamp before
claim acquisition without consuming the claim. Migration
`0021_activation_completion_evidence.sql` also rejects unsafe outcome text and
backdated evidence at the database boundary with `CHECK (... IS TRUE)` semantics.
Runtime migration tests prove incompatible historical evidence is retained and
causes upgrade failure rather than silent rewriting; operator review is required.
The activation dedupe compatibility ceiling now tracks this build through 0021.

A late observation from an already initiated request may be recorded after claim
expiry; this is evidence recording, not permission for a late or repeated write.
Code grammar does not by itself exclude secrets shaped like technical codes:
future concrete adapters must return fixed allowlisted codes, never raw responses.
The tests do not establish hardware readback, prevent an already in-flight device
request, or prove shared fencing with the legacy runner. Those release gates
remain open, and the production dispatcher remains fail-closed without a caller.

Verification for this checkpoint: PostgreSQL integration **70/70**, Application
regression **473/473**, Persistence unit/resource **23/23**, and production
Application/Persistence build with analyzers and warnings-as-errors at zero
warnings/errors. The isolated PostgreSQL cluster was stopped after the run.

## Operational Deye transport contract brought into the isolated branch

The operational checkout's uncommitted adapter adds midnight-last rotation and
optional `enableSell`; neither behaviour existed in the frozen Git baseline.
Those two adapter behaviours and its 10 W power-match helper are now reproduced
in this worktree without modifying or committing the operational checkout.
Assertions are derived from the fully read operational four-window runbook,
not copied blindly from its dirty tests: time/settings pairs remain unchanged.
In particular, the Z4 idle settings stay at 18:00 and discharge stays at 19:00.

Local HTTP recorder tests verify the actual serialized mutation body, omission
of only the unsupported flag, unchanged source ordering, and exact/downward
power quantization. The isolated adapter also fails closed for non-finite guard
values, an invalid reserve below 30%, master payload power outside 0-80,000 W,
SOC outside 30-100%, negative voltage and null rows. These additional checks are
product-side hardening, not an edit to the current operational runner.

All **131 Optimization adapter tests pass** without Deye network access.
The default write opt-in remains false; no product dispatcher was connected to
this adapter. The remaining shared-writer design and its explicit unmet rollout
gates are recorded in [shared-writer-boundary.md](shared-writer-boundary.md).

Strict Optimization analyzer build is **not green**: six pre-existing findings
remain in unchanged `OreePriceFileSource` (CA2234, CA2007, CA1512, CA1062) and
`DeyeCloudAuthenticationHandler` (two CA1308 findings). The existing timeout
validation finding in the edited TOU adapter was repaired with the equivalent
standard throw helper. No analyzer finding is reported in the edited adapter.
The 131-test command uses `RunAnalyzers=false`; it does not prove that the
complete production Optimization project passes the strict release build.
All **41 architecture/composition tests pass**, including the default shadow
host boundaries. No live host, operational runner or Deye request was started.

Deployment decision confirmed by the operator: the shared write broker will run
on the separate application server, as a distinct credential/egress boundary.
The Windows runner and agent become bounded authenticated clients. This choice
does not authorize production cutover; server configuration and a controlled
pilot remain later operator-approved steps.

## Durable shared broker admission

Migration `0022_device_write_broker_attempts.sql` and a new internal broker store
add the common persistent per-site unresolved-attempt boundary. Prepared,
Initiated and Unknown attempts never expire automatically; lease takeover or
outbox rollback cannot grant a second attempt. Initiation repeats live control
plane authorization and, for the product path, locks the exact live claim.
Enum-derived observation codes and SQL checks prevent arbitrary outcome text;
NotSent cannot clear an Initiated/Unknown attempt. Verified windows and consumed
product claims cannot be executed again, and prewrite NotSent attempts are
bounded to three. The dedupe migration compatibility ceiling now includes 0022.

PostgreSQL integration **77/77** passes, including 12 simultaneous admission
clients, exact replay, lost DB client/recreated pool, expired lease takeover,
kill switch before initiation, wrong product claim, product rollback, observation
transitions, missing legacy-stop evidence before network initiation and direct
SQL uniqueness rejection. Application regression **477/477** and Persistence
unit/resource **24/24** pass. The production
Application/Persistence analyzer build passes with zero warnings/errors.

This is not a complete broker service: no HTTP endpoint, host registration,
authenticated legacy client, payload resolver, device driver or recovery endpoint
was enabled. The conservative legacy pilot timing rule is not yet proof of
operational delay equivalence. These remaining gates are explicit in
[shared-writer-boundary.md](shared-writer-boundary.md); production stays unchanged.

## Broker executor and counted fake-vendor mutations

The internal `DeviceWriteBrokerExecutionUseCase` now consumes the shared durable
admission protocol for both legacy and product authorities. It uses server time,
binds a trusted resolved plan to the exact daily hash/site/date/window, copies
collections before validation, performs read-only preflight, commits Initiated
before driver invocation, and records only enum-derived observations. The default
driver is fail-closed and has no network capability. No live host uses this code.

Initiation commit ambiguity never calls the driver or clears the latch. A lost
update response becomes Unknown; failed observation persistence or cancellation
after initiation leaves Initiated. Neither state can be retried by a fresh
executor. Timing is rechecked after preflight and after the initiation commit;
a post-commit deadline failure sends no update and retains an Unknown latch.

PostgreSQL integration **92/92** and Application regression **487/487** pass.
The production Application/Persistence build with analyzers and warnings-as-errors
passes with zero warnings/errors. The final owned PostgreSQL cluster was stopped;
its preserved TRX is
`artifacts/postgres-portable/cluster-b02683556bf74acaaa5faf9ebf78b1c1/test-results/postgres-integration.trx`.

Tests count HttpClient calls reaching an in-memory vendor handler, not merely
mocked gate results. Twelve concurrent identical execution requests produce
exactly one update and fresh readback. Both approved product and legacy paths
are covered. Product rollback during preflight sends zero updates; rollback after
an update retains Unknown and blocks a proposed legacy fallback. Injected lost
update responses, initiation acknowledgements and observation persistence, client
recreation, cancellation, deadline delays and resolver-list mutation are covered.

These are synthetic readbacks and injected faults, not real Deye verification,
Postgres server crash testing, credential isolation or HTTP service restart proof.
A separate authenticated broker service, concrete trusted saved-plan resolver,
real freshness/readback driver and bounded legacy client remain to be implemented.
The conservative legacy timing compatibility and original hardware/shadow rollout
gates remain open. Operational runner, credentials, scheduler and Deye were not
modified or invoked.

## Separate application-server broker shell

The isolated branch now contains `BatteryEms.BrokerHost`, a separate process
in the solution, and `DapperDeviceWriteBrokerPlanResolver`. Product payloads
resolve only from the exact live Claimed outbox row and are rechecked against
the daily/window hashes. Legacy shadow snapshots remain read-only evidence,
not write authorization; legacy plan resolution currently refuses requests.

The host is disabled by default. An explicitly enabled instance requires an
HTTPS listen URL and distinct role/owner/site-scoped client credentials. It
uses server time and a bounded JSON contract without raw vendor payloads or
URLs. Its default driver has no device capability: both readiness and a write
request return 503 **before** any broker attempt is created. The existing
operational host and runner do not reference this new process. It has not been
deployed, started against production, or connected to Deye.

PostgreSQL/HTTP integration **99/99** passes in a fresh owned cluster, including
real claimed-outbox resolution, corrupt/stale claim rejection, HTTPS-scheme and
token/site guards, strict JSON, owner/role binding, disabled-route behavior and
no-attempt default-driver refusal. The strict BrokerHost/Application/Persistence
build passes with zero warnings/errors. The cluster was stopped after testing;
its TRX remains at
`artifacts/postgres-portable/cluster-848eea44a40b4f2894f791da94e7939f/test-results/postgres-integration.trx`.
TestServer's HTTPS scheme is not proof of a real server certificate.
The existing host's architecture/composition regression also passes **41/41**.

Before any deployment, real TLS/private-network setup, legacy scenario
attestation, device preflight/readback, old-writer credential fencing, shadow
equivalence, controlled hardware pilot and rollback/reconciliation drill remain
open. See [shared-writer-boundary.md](shared-writer-boundary.md).

## Concrete Deye driver and final network authorization

The isolated branch now contains `DeyeCloudDeviceWriteBrokerDriver`: fresh
topology/telemetry/capability reads, one master TOU update and independent fresh
live-read verification. It preserves interval pairs/midnight rotation, optional
sell capability and established downward 10 W readback quantization. A dedicated
authenticated client factory disables redirects and mutation auth-refresh retries.

A new `IDeviceWriteBrokerMutationGate` is implemented by the persistent attempt
store. The driver calls it after its second live preflight and immediately before
the update, so a kill switch engaged during device reads prevents mutation.
The durable Initiated/Unknown latch remains independent of rollback/lease expiry.

Verification: DeyeCloud protocol/regression **18/18**, Postgres/HTTP integration
**102/102**, strict DeyeCloud/test and BrokerHost/Application/Persistence builds
with zero warnings/errors. The owned integration cluster was stopped; the TRX
is `artifacts/postgres-portable/cluster-f5374ce1c8a2423091cb8eeaf7fce6dd/test-results/postgres-integration.trx`.
Integration handlers recorded one update for twelve concurrent requests; a lost
response blocked every replay after DB client recreation; a kill switch during
write-boundary preflight recorded zero updates. Final protocol tests also prove
multiple fresh read orders, exact supported fields and independent readback after
failed order status. All device responses are synthetic.

The driver's test project is included in the solution and Docker test stage.
The complete Docker CI/release build has not been run; previously documented
Optimization analyzer findings remain an open release gate. This driver is not
registered by either host, and the broker still defaults to 503 without a device
driver. Actual station metric compatibility, authenticated client/token
composition, legacy equivalence, staging deployment and hardware pilot remain
unfinished. Operational runner, scheduler, credentials and Deye were unchanged.

## Auth-only token acquisition checkpoint

`DeyeBrokerTokenProvider` now obtains vendor tokens separately from the mutation
transport. Acquisition is bounded to 15 seconds and serialized; concurrent
callers reuse an immutable cached token only with more than 180 seconds remaining.
Expiry is measured from request start, not response arrival. Missing/invalid
expiry, short lifetime, malformed token, vendor failure and backwards clock fail
closed; there is no inferred 3600-second fallback and no automatic HTTP retry.
The dedicated production auth client disables redirects/cookies and caps response
buffering. Password handling preserves the existing lowercase SHA-256 protocol,
including already-hashed inputs, without depending on the old replaying handler.
Diagnostics omit vendor response bodies, URLs and inner exceptions; secret-bearing
options/tokens do not have generated record diagnostics.

Strict DeyeCloud adapter/test compilation and local protocol regression pass
**35/35** with zero warnings/errors. Fixtures cover concurrent acquisition,
explicit refresh, immutable old tokens, rejected responses, no 401 retry,
clock reversal, response latency, password hashing and caller cancellation.
All network handlers are synthetic. No database, operational process or Deye
request was changed or invoked for this checkpoint.

This provider is not yet registered by a host. Per-attempt token/client lifetime
and authenticated host composition remain the next implementation step;
the broker's existing fail-closed behavior remains intact. The user's separate
application-server decision and current scope are recorded in
[current-task.md](current-task.md), including the distinction between disabled
startup and enabled startup that runs database migrations. No deployment occurred.

## Broker-only session composition checkpoint

The distinct BrokerHost now references DeyeCloud and explicitly composes
`DeyeBrokerDriverSessionFactory` only when both `Broker:Deye:Enabled` and
`Broker:Deye:WriteEnabled` are true. Both default false; global `Broker:Enabled`
also still defaults false. Driver options, device identities and credentials are
server-bound, and both client allowlists must match the single configured site.
The operational host has not been wired to this factory.

TLS/token/site and structural request guards run first. Session acquisition then
gets a vendor token before `BeginAsync`; auth failure returns a fixed 503 without
creating an attempt. Each session owns a dedicated mutation HTTP client and is
disposed on completion/error/cancellation. Tokens cannot be refreshed underneath
an executing driver. The attempt store and final mutation gate resolve to the
same persistent instance. Token lifetime is checked again before preflight
(more than 150 seconds) and execution (more than 120 seconds), so database delays
cannot silently consume the flow's budget. Insufficient lifetime after initiation
remains Unknown, not a new permission to write.

Verification: strict BrokerHost/Application/Persistence/DeyeCloud build has zero
warnings/errors; DeyeCloud protocol tests **36/36** include one 401 update without
reauthentication/retry. Fresh owned Postgres/HTTP suite **108/108** covers auth
failure/no-attempt, malformed/unauthenticated callers/no-auth-call, distinct
sessions/cached token, disabled sessions, production DI switches/shared gate and
insufficient token lifetime. The cluster was stopped; result evidence remains at
`artifacts/postgres-portable/cluster-fdd9906bce4c457fa40e04c2f8bebc25/test-results/postgres-integration.trx`.
Integration test compilation uses the runner's existing analyzer-disabled setting;
the strict host and DeyeCloud builds are separate checks.

The secret-free [staging config example](broker-staging.example.json) is not
automatically loaded by ASP.NET and is intentionally disabled. Empty credentials
and `vendor.invalid` are placeholders, not a usable deployment configuration.
Readiness still reports 503; there is no claim of verified hardware readiness.
Host/OS/TLS and external secret provisioning are not yet selected. Trusted legacy
resolution, physical writer fencing, real shadow/device evidence and approved
pilot/rollback remain unfinished. No staging deployment, production migration,
runner/scheduler change or real Deye request occurred.

## Release package and disabled restart harness

`scripts/package-productization-broker.ps1` performs locked restore and strict
framework-dependent Release publish for the separate BrokerHost. Each invocation
creates a new GUID-owned directory/ZIP under ignored `artifacts/productization`.
The manifest records source revision, dirty state, SDK, file sizes and SHA256;
the archive hash is printed. No operational config or secret is copied. Hashes
are integrity checks against a trusted manifest, not publisher signatures.

`scripts/test-productization-broker-package.ps1` validates exact payload inventory
and hashes before starting its own loopback-only process. The child environment
is cleared; all broker/device switches are explicitly false. Implicit runtime
appsettings are refused. It requires health 503 and mutation-route 404, stops
only its owned child, then repeats once. Logs/results remain outside the payload.
This verifies disabled process restart, not live TLS, enabled migration recovery
or Initiated/Unknown reconciliation.

Initial development package verification passed twice and a deliberate added
payload marker was rejected before process startup; that owned marker was
removed afterward. Development evidence is preserved at
`artifacts/productization/broker-a1ae5839bfba467997e7d6911328b071/smoke-58bf26c1e90740499e9f6388f869c306/result.json`.
Its sourceDirty=true marker means it must not be promoted. The scripts and
[staging runbook](broker-staging-runbook.md) are prepared for a clean-commit build.
No service/auto-start, remote staging or operational database was touched.

### Clean package verified

Source commit `e1933a2f3278e1a4e1c00d0fc207f695c3edf869` was packaged with
`sourceDirty=false`, SDK 10.0.300, locked restore and strict Release publish.
The archive is
`artifacts/productization/broker-d8c30aa991df41e78e72a0b12f1a8bdb.zip`, SHA256
`1D2870B6F6C88315D982826B55F2BF152378A16002913D34847526F4FF838D9C`.
Its manifest is inside the ZIP and beside the payload directory. The clean
package's two disabled launches/restart checks passed: health 503, mutation
route 404, both owned processes stopped. Evidence:
`artifacts/productization/broker-d8c30aa991df41e78e72a0b12f1a8bdb/smoke-2a50e0b3c6e24f1bbb34fee0e1a9fc81/result.json`.

The archive is a local reviewable staging artifact, not a deployed server or a
complete release. Remote host/OS, private TLS, secret storage, dedicated staging
DB and enabled-process recovery drills remain open. The existing runner and its
three tracked operational edits were preserved; no production migration or
vendor request was made.
