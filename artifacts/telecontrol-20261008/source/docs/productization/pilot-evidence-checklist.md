# Live evidence required before the hardware pilot

Current verdict: **not established**. No real station, operational writer or
server DB was accessed by this cloud task. The available fixtures are synthetic.
The recovered staging deployment history explicitly says live Deye data was
absent and hardware control disabled. Passing local crash tests establishes
persistence behavior under the tested failures; it does not establish field
readback, real shadow equivalence or physical sole-writer enforcement.

## Known server and source mismatch

The server is `10.10.70.66`, Debian 12 x86_64, accessed previously from Windows
through WireGuard. The latest recorded staging image is
`bess-ems-staging:5ec99720-fix1` under
`/home/jar/apps/bess-ems-staging/5ec99720-fix1-20261007`.
The dashboard gateway was at port 3000, with a separate persistent DB and
read-only HTTP boundary. The image came from another source revision than this
cloud checkout (`7c59d1a`). Verify the running image/mounts and reconcile code
before transferring updates; do not overwrite the staging DB or deploy an older
Host simply because this BrokerHost builds successfully.

## Shadow evidence

Use existing authenticated read APIs or a server-selected read-only DB account.
`deploy/staging/pilot-evidence.sql` is a bounded read-only inspection of 14 exact
consecutive delivery days, matching the default `PilotReadinessOptions`.
Delivery dates must be supplied explicitly in Europe/Kyiv terms, including DST
rules. The SQL handles the older staging schema lacking the agent tables.
It does not initialize the schema or turn on a scheduler.

For each delivery day retain trusted source references for the real legacy
saved scenario and independently produced shadow schedule, capture timestamps,
canonical hashes and comparison output. Verify both are ready, the latest
comparison is equivalent, mismatch count is zero and no day is missing.
Run/source provenance must be inspected separately: the schema alone cannot
prove records came from real operation rather than a synthetic fixture.
Do not manually insert passing records to satisfy this gate.

## Physical writer boundary

Read the existing runner's scheduler/config and server process inventory without
printing credentials. Record which process can acquire vendor write credentials,
which paths can reach the mutation API, and whether fallback can bypass the broker.
A unique PostgreSQL lease, a stop-reference string, or one running app container
is insufficient evidence of a sole physical writer.

Before the pilot, require dated evidence that the old writer has been drained,
only the broker has mutation credentials/egress, and both legacy and agent paths
use bounded authenticated clients. Prove bypass is denied by inspecting identity
and network policy or an approved isolated negative check that cannot send a
real device command. Do not invoke TOU writes as a connectivity test.
This checklist does not authorize changing the working runner or its credentials.

## Recovery and operator evidence

Keep Initiated/Unknown unresolved until the old executor cannot send and the
device outcome has been reconciled from a fresh independent read. Retain
readback schema/freshness/topology evidence, kill-switch behavior, rollback
steps and reconciliation ownership. A service restart or timeout must not clear
these states. This task adds synthetic SIGKILL and PostgreSQL crash probes
(`scripts/test-broker-crash-recovery.py`); the counted vendor endpoint is local,
not a real Deye endpoint.

The actual pilot remains blocked until live evidence, writer isolation, trusted
legacy attestation and bounded clients are complete. Hardware pilot approval
and scope must identify the station, delivery date/window, operator and rollback.
