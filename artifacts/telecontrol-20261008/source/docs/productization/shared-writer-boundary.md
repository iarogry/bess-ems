# Shared writer boundary: implementation and cutover requirements

Status: durable admission implemented/tested in the isolated branch;
server transport, driver and operational boundary **not deployed or satisfied**.
The existing operational runner remains the sole authorized Deye writer.

## Evidence and failure model

The operational runner directly calls `DeyeTouCommandAdapter.UpdateAsync` after
live preflight. It does not acquire the product's PostgreSQL writer lease or
consume its prewrite claim. The isolated host still registers a fail-closed
activation dispatcher, so there is currently no second product Deye writer.

An expiring lease is not a device fence. Consider an old process whose HTTP
request has started: its DB connection/lease can disappear before Deye completes
the request. A replacement process with a newer fencing token can then issue a
second request, because the vendor transport shown in the current source does
not transmit or enforce our fencing token. A transaction, named mutex or
advisory lock alone does not close this gap. Rollback can cancel a database row,
but cannot recall an already initiated device request.

The product must not describe the current leases, successful synthetic tests or
legacy-stop evidence text as proof of physical single-writer enforcement.

## Target boundary

Both execution paths must use one write broker; only that broker may retain
Deye write credentials and permitted write egress. The old scheduler retains
its established dates/windows and saved payloads; the agent retains bounded
proposal/approval tools. Neither becomes an unrestricted device client.
Operator decision on 2026-09-29: deploy the broker on the separate application
server. The existing Windows runner becomes an authenticated broker client;
the agent has only bounded application tools, not a generic broker/device API.
No deployment, scheduler or credential change is authorized by this location
choice alone.

The broker must be a distinct server-side process/security boundary, even when
co-located with the application. Restrict write credentials and Deye mutation
egress to that process; do not mount them into the agent or Windows runner.
Use an authenticated private transport for the runner/application clients, not
an unauthenticated public mutation endpoint. Agent API tokens must not double
as broker writer credentials. Production server address, trust material and
secret references remain deployment inputs, never checked-in values.

Start with one active broker per site and no automatic active-active failover.
An unresolved device request must survive server/process restart in PostgreSQL
and block replacement execution until the old process is drained and the device
outcome reconciled. Loss of connectivity from Windows blocks that attempt; it
must never fall back to a direct Deye mutation or a locally reconstructed payload.

Broker requirements, to implement before an operational pilot:

1. Accept only an explicit site, delivery date, Z1-Z4, canonical payload hash
   and bounded authorization reference. Never accept caller-supplied URLs,
   credentials, serial numbers, HTTP methods or raw vendor request bodies.
2. Admit `LegacyRunner` only while that authority is active; admit `ProductAgent`
   only against its exact approved pilot/release/claim and current revision and
   fence. Both paths must share the same durable per-site admission boundary.
3. Persist a unique unresolved write attempt **before** network mutation. Do not
   release or reissue it on lease expiry, process restart, DB disconnection,
   timeout, unknown order status or failed readback. Another site may proceed;
   another write to this site may not.
4. Repeat live topology, freshness, BMS/SOC, alarm, master/aggregate power and
   claim-time checks at the actual transport boundary. Permit only
   `order/sys/tou/update`, never TOU switching or arbitrary device commands.
5. Send at most once. After initiation, verification retries may create only
   fresh live-read orders. Status 666 is not proof of applied settings. Record
   exact readback or a fixed allowlisted unknown/mismatch code without exposing
   raw responses or device identifiers to the agent.
6. Recovery of an unresolved attempt requires proof that the old executor can
   no longer send, plus fresh device/order reconciliation. A DB flag or elapsed
   timeout alone is not sufficient. Unknown physical outcomes remain blocked.
7. Kill switch denies admission immediately. Rollback cancels unstarted work;
   in-flight work stays quarantined until verified. Rollback must not implicitly
   re-enable the legacy runner while a broker request can still be in flight.

## Required verification and rollout

- Run a local fake-vendor broker test for both legacy and product paths, including
  simultaneous requests, stale tokens, DB connection loss, restart after send,
  unknown outcomes, kill switch and rollback. Assert exact mutation counts and
  persisted audit records, not only mocked gate return values.
- Reproduce all four saved-window wire payloads: times are starts, the midnight
  row is last, and all time/settings pairs remain unchanged. Unsupported
  `enableSell` is omitted; power readback permits only the established 10 W floor.
- Capture real legacy/shadow equivalence for the agreed observation period.
  Synthetic 14-date test rows are not deployment evidence.
- Prebuild the broker and adapted legacy runner in the isolated checkout. First
  verify them without credentials or write egress. Do not rebuild or replace the
  operational runner during a scheduled switch.
- At an operator-approved maintenance point, drain the old writer, route both
  paths through the broker and remove their direct mutation capability. Prove
  that an attempted bypass cannot reach Deye. Keep the product dispatcher disabled
  until this evidence exists.
- Execute one explicitly scoped hardware pilot and a rollback/reconciliation
  drill before granting product writer authority. Verify that fallback preserves
  the sole-writer boundary rather than restoring unrestricted direct credentials.

## Current executable admission slice

`IDeviceWriteBrokerAttemptStore` and `DapperDeviceWriteBrokerAttemptStore` now
provide a shared PostgreSQL admission port for legacy and product authorities.
Migration 0022 persists attempts independently of activation outbox rollback.
`Prepared`, `Initiated` and `Unknown` hold a unique per-site unresolved latch
without any expiry/automatic cleanup. An exact Begin replay is explicitly marked
`IsReplay`; it is never a fresh execution permit. A different payload/identity
under that attempt UUID is rejected.

Before preparation and again before `Initiated`, the store verifies the window,
current safety revision/authority, kill switch and live exact lease. Product
admission also locks and validates the exact live activation claim, site, date,
window, daily hash, owner, revision and fence. `Initiated` must commit before the
transport is called; its replay must never call transport again. The internal
executor below consumes this protocol; its authenticated service host and real
device transport are still to be implemented.

Only a Prepared attempt can become `NotSent`; only an Initiated attempt can
become `Verified`. `Unknown` stays unresolved and cannot be automatically
converted to either state. Observation codes are fixed enum-derived codes,
not caller-supplied text. Product claims are consumed once even after a terminal
outcome, verified site/date/window combinations cannot run twice, and prewrite
NotSent attempts are limited to three for a site/date/window. No operator
reconciliation/unlock endpoint exists yet.

Current admission tests use the conservative 15-minute pilot startup deadline
for both authorities. This is **not** verified equivalence of the legacy runner's
normal-delay policy and must not silently become its production policy. A
bounded legacy trigger authorization and delay-compatibility decision remain a
rollout gate. Legacy hash metadata must also be bound to a trusted server-side
saved-plan resolver; accepting an internal hash here does not validate a payload.

The runtime suite proves per-site serialization for 12 concurrent store clients,
replay/conflict handling, survival of database-client disposal/recreation and
lease takeover, kill-switch revalidation before initiation, fixed-state
observation rules, SQL uniqueness, missing legacy-stop evidence at product
initiation, and retention across product rollback.
It does not prove a Postgres server crash/restart, an HTTP broker process restart,
hardware readback, credential isolation or direct-write bypass prevention.
The store is not registered in a live host, and no endpoint or Deye caller was
added. Future server callers must use authenticated server identities and server
time; these internal request fields must not be exposed as an agent write API.

This is not a claim that a deployed broker or hardware pilot already exists. Implementing
and verifying this boundary remains part of the original productization goal.

## Internal broker execution slice

`DeviceWriteBrokerExecutionUseCase` now coordinates admission, trusted saved-plan
resolution, read-only preflight, durable initiation, a single driver invocation
and observation. It replaces request time with `IClock.UtcNow`. A Begin or
Initiated replay never invokes the mutation driver. Resolved plans are copied
into read-only collections and validated against the site, date, canonical daily
hash, exact Z1-Z4 set, selected window and bounded settings. Client JSON is not a
payload input to this executor. A concrete trusted saved-plan resolver is still
needed for the service; the new resolver interface alone is not that resolver.

Preflight failure may close Prepared as NotSent. Initiation commit errors are
outside that error handler: ambiguous commit acknowledgements must never clear
the latch or call the driver. Cancellation after initiation leaves Initiated.
Driver exceptions or unmatched/undefined readback results become Unknown, never
an automatic retry. Failure to persist the observation propagates while the
Initiated latch remains. Timing is checked after preflight and again after the
initiation commit, before driver invocation. An expired post-commit attempt
remains latched Unknown rather than being cleared as NotSent.

The driver port separates read-only preflight from one update plus fresh
readback, and the provided default driver is fail-closed with no network access.
These interface requirements do not prove a real Deye implementation obeys them.
The future concrete driver must recheck telemetry/timing at the actual network
boundary, perform no hidden mutation retries, and obtain independent device
readback rather than treating order status as proof.

Runtime integration tests use real PostgreSQL and HttpClient with an in-memory
fake-vendor handler. They count actual handler calls for both legacy and approved
product paths: concurrent requests produce one update; lost update responses,
lost initiation acknowledgements, failed observation persistence, cancellation,
late initiation and client restart cannot produce another update. Rollback
during product preflight sends zero updates; rollback after an update preserves
the broker's Unknown latch even for a proposed legacy fallback. Tests also prove
server time overrides client time and resolver-owned list mutation cannot alter
the copied approved payload. Database outages/acknowledgement loss are injected
at the port boundary, not an actual Postgres server crash. The handler's matched
readback is synthetic and is not hardware or wire-contract verification.

No live host registration, HTTP endpoint, production driver, legacy client or
operational setting was added or enabled by this execution slice. The next
implementation boundary is the separate authenticated application-server broker
host with trusted plan resolution and a default-disabled device adapter.

## Separate broker host and product payload resolution

`BatteryEms.BrokerHost` is now a distinct ASP.NET process/project in the
solution; the existing BESS host and Windows runner do not reference or start
it. It maps no mutation route by default (`Broker:Enabled=false`) and reports
503 readiness. When explicitly enabled, startup requires a non-wildcard HTTPS
listen URL, a database connection and two distinct long client tokens with
role/owner/site allowlists. The endpoint accepts only attempt ID, site, date,
window, daily hash, safety revision, fence and optional claim ID. It derives
writer role/owner from the authenticated client configuration, ignores no
unknown JSON fields, limits request bodies to 2 KiB and uses server time. Plain
HTTP is rejected. `livez` means the process responds; `healthz` deliberately
remains 503 because no physical driver is installed. Even an enabled shell
returns 503 before admission when its default fail-closed driver is present;
it does not consume an approved claim or create a broker attempt.

`DapperDeviceWriteBrokerPlanResolver` retrieves a product plan only from the
exact live `Claimed` outbox record: claim, site, date, hash, release owner,
selected window, revision, fence and claim expiry must match. It validates
the stored canonical daily payload and selected-window hash again. Corruption
or a foreign/stale claim returns no plan. Legacy shadow snapshots are
observational and are deliberately **not** promoted into write authority; the
legacy resolver path currently returns no plan. A separately attested saved
legacy scenario source is still required before routing the old runner.

The HTTP integration tests use `TestServer` (synthetic HTTPS scheme), owned
PostgreSQL and a test-only driver with a read-only refusal. They verify the
disabled route, startup configuration rejection, TLS/token/site checks,
bounded JSON, configured role/owner identity, default-driver 503 without a
database attempt, and replay. Resolver tests use real outbox claims and prove
stale/foreign identifiers and corrupted payloads cannot reach the fake vendor.
They **do not** prove a real TLS certificate, private-network deployment,
secret rotation, process restart, Deye readback or credential/egress isolation.

Do not deploy or point this host at an operational database yet. Enabled host
startup runs migrations against its configured database; test execution uses
only fresh isolated local clusters. A staging deployment needs operator-reviewed
database credentials, TLS certificate and private binding, synchronized clock,
client secret provisioning, service observability and restart/unknown-state
reconciliation. A hardware pilot additionally requires the real Deye driver,
legacy saved-plan equivalence, bypass-proof revocation of direct legacy/vendor
write capability and all earlier shadow/pilot/rollback gates. No production
driver or credential was added by this checkpoint.

## Concrete Deye protocol driver (isolated development)

`DeyeCloudDeviceWriteBrokerDriver` now implements the device protocol against a
server-configured HTTPS client. It is **not registered in either host** yet;
the broker's default driver/readiness refusal remains in force. Site, station,
master and slave bindings are server configuration, never client request fields.
The write opt-in defaults to false.

Admission preflight and write-boundary preflight independently read station
topology, latest telemetry, TOU capability and system configuration. Exactly the
configured two inverters must be online. Collection timestamps must be within
0-600 seconds; master SOC/voltage must be finite and SOC at least 30%; explicit
alarm/fault evidence must be clear; each inverter battery power is bounded by
80,000 W and aggregate absolute power by 160,000 W. Missing or duplicate metrics
refuse the operation. Requiring explicit alarm and batteryPower evidence is
additional hardening whose compatibility must be checked against actual station
responses before any pilot; synthetic fixtures do not prove field availability.

After that final network preflight, `IDeviceWriteBrokerMutationGate` revalidates
the exact durable Initiated attempt and current safety/lease/claim via
`DapperDeviceWriteBrokerAttemptStore`. The driver rechecks trigger timing and
telemetry age after this gate, then sends one `order/sys/tou/update`. Gate denial
after Initiated leaves the durable attempt Unknown; it never clears the latch
as NotSent. A kill switch can prevent a call that has not started, but cannot
recall an already in-flight vendor request. This is not a physical device fence
or an atomic transaction spanning PostgreSQL and Deye.

Wire mapping keeps start-time/settings pairs and rotates midnight last. Sell is
omitted when all six capability rows omit it; inconsistent capability is refused.
Order polling is capped at 45 seconds. Independent live readback is capped at
45 seconds with up to nine new read orders by default, five seconds between
completed snapshots, and at most 15 seconds polling any pending order. A later
fresh match supersedes an earlier mismatch within this observation. All six
times, supported flags, SOC and voltage must match; only the established downward
10 W power quantization is accepted. Order status, including 666, is not success
evidence. A failed order status can still be followed by independent matching
device state. Update calls are never retried; unknown outcomes stay latched.

Production composition must use the dedicated `CreateAuthenticatedClient`
factory with an already acquired token. It disables redirects/cookies, uses a
30-second request timeout and a 1 MiB response buffer. Do not attach the older
`DeyeCloudAuthenticationHandler` to this mutation transport: its 401 refresh path
re-sends a request. Token acquisition/refresh must finish before admission;
automatic authentication or resilience retries of an update are forbidden.

The concrete driver's HTTP tests pass 18/18; the existing Postgres/HTTP suite
passes 102/102 including concrete-driver execution, twelve concurrent callers,
lost update acknowledgement, recreated database clients and kill switch during
the second preflight. All vendor calls go to in-memory handlers. DeyeCloud and
its tests, plus BrokerHost/Application/Persistence, pass strict analyzer builds.
The DeyeCloud tests are now included in the solution and Docker test stage;
Docker CI itself was not executed in this Windows checkpoint.

Remaining work includes secure token provisioning/refresh, explicit broker-only
driver composition, authenticated bounded clients, trusted legacy plan
attestation, staging TLS/restart drills, real station schema/readback evidence,
removal of direct-writer credential/egress bypass, shadow equivalence and the
operator-approved hardware pilot/rollback. No live device operation was invoked.

## Explicit broker session composition

The newer checkpoint implements the previously missing token acquisition and
broker-only driver composition. The host needs three explicit switches:
`Broker:Enabled`, `Broker:Deye:Enabled`, `Broker:Deye:WriteEnabled`; all default
false. Enabling the global host runs configured database migrations, even when
device writes remain disabled. Deye configuration is bound to one site and both
authenticated client allowlists must contain only that site.

`IBrokerDriverSessionFactory` prepares authentication before durable admission.
A session contains a fixed bearer token and owns its client; no update passes
through a token-refresh/retry handler. Auth acquisition failure is a fixed 503,
not a durable Prepared/NotSent attempt. Replays still pass through session
preparation, so unavailable vendor authentication can make an HTTP replay return
503; it never grants a fresh execution permit. A token budget guard denies
preflight/execution after excessive database delay without refreshing the session.
The final authority gate remains the same persistent store instance as admission.

Protocol tests now pass 36/36 and isolated Postgres/HTTP tests 108/108. These are
synthetic protocol/TestServer checks, not live station or real TLS evidence.
The default readiness endpoint remains 503, including for explicitly configured
drivers, until deployment and hardware readiness criteria are implemented and
proved. External secret storage, bounded runner/agent clients, legacy attestation,
staging deployment/drills, direct-writer fencing and pilot/rollback are still open.
