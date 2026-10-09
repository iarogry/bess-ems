# Reproducible broker recovery and dashboard verification

The persistence tests were separated into orchestration/safety, activation
dispatch and product broker scenarios, with shared asserted pilot setup in
`ReleasedPilotFixture`. All original test methods and 108 cases remain; no
analyzer or warnings-as-errors setting was relaxed. The strict integration
build and all 108 tests passed on a newly owned PostgreSQL 16.15 cluster.

The additional `BatteryEms.BrokerRecovery.TestHost` is a test-only executable.
It composes the real BrokerHost HTTP boundary, migrations and durable attempt
store with a synthetic saved plan/clock/driver. It rejects remote DB/vendor
addresses and does not load operational config. The test driver intentionally
pauses after durable Initiated, before sending or after one counted local
HTTP mutation. The parent sends SIGKILL, optionally stops PostgreSQL in
immediate/crash mode, restarts both, and checks original replay plus a new
attempt. Neither may send again; Initiated must persist. A trusted ephemeral
TLS certificate is verified by the client. The private key is removed with the
owned cluster. Test outputs contain no real credentials or station identifiers.

Build and run (activate the repository's configured SDK/PG tools first):

```bash
dotnet restore tests/support/BatteryEms.BrokerRecovery.TestHost/BatteryEms.BrokerRecovery.TestHost.csproj --locked-mode
dotnet build tests/support/BatteryEms.BrokerRecovery.TestHost/BatteryEms.BrokerRecovery.TestHost.csproj -c Release --no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false
python3 scripts/test-broker-crash-recovery.py --results /absolute/new/evidence-directory
node tests/operator/dashboard.test.cjs
python3 tests/staging/test_monitor_staging.py
```

Dependencies: .NET 10, PostgreSQL 16 utilities (`initdb`, `pg_ctl`, `createdb`,
`psql`, `postgres`), Python 3, OpenSSL and Node. No real DB connection string or
vendor secret is accepted by the crash script. It creates four separate temporary
clusters and removes them only after confirmed shutdown. Run as a non-root user.
The four scenarios prove behavior for a killed broker with persisted Initiated,
with and without an actual PostgreSQL crash. They do not exercise an operating
system power loss, product-claim cutover, a real Deye driver or manual Unknown
reconciliation. Existing regression tests cover synthetic product rollback and
Unknown outcomes; physical writer fencing remains an operational gate.

Dashboard reads now belong to a generation and an AbortController. Older
responses, including transports ignoring abort, cannot render after a newer
selection. Refresh clears old live values, independently loads panels, exposes
partial failures, uses a 15-second deadline and shows unknown stop state when
its status cannot be read. Live telemetry requires Valid API quality and a
bounded observation age (battery 10 minutes, site 40 minutes). Expired commands
are not shown as current power commands. Successful historical optimization
runs remain until another run or asset selection is chosen. Returning to a
suspended browser triggers an immediate refresh.

The dependency-free Node tests execute the actual dashboard source with mocked
DOM/HTTP for failure, stale quality, old responses, timeout and expired command.
They validate behavior; they do not replace a browser usability check on staging.
Monitoring tests use an owned TLS server, including refusal of untrusted TLS,
self-redirects, missing DB/UI, stale health and invalid response shapes.

Actual results for this turn are recorded in
`/workspace/onboarding-results/persistence.trx`,
`/workspace/onboarding-results/crash-recovery-20261007-final/summary.json`,
and the final task verification report. None of these are a deployment record.
